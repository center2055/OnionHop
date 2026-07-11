using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

public enum BridgeReachability
{
    Reachable,
    Slow,
    Unreachable,
    Unparsed,

    /// <summary>
    /// The transport's real endpoint is a URL/broker host, not the bridge-line IP:port (snowflake,
    /// meek, conjure, dnstt). We probed that url/front host and it answered — the bridge is usable
    /// even though its listed IP:port can't be pinged. (webtunnel is verified more strictly, via a
    /// real WebSocket-upgrade handshake, so it reports Reachable/Unreachable rather than Fronted.)
    /// </summary>
    Fronted
}

/// <summary>
/// Result of TCP-probing a single bridge line.
/// </summary>
public sealed record BridgeScanResult(
    string RawLine,
    string Transport,
    string Host,
    int Port,
    int? PingMs,
    BridgeReachability Reachability,
    string Detail)
{
    /// <summary>A bridge counts as "working" if the TCP handshake succeeded (fast or slow), or — for
    /// fronted transports (snowflake/meek/conjure) — if its broker/front host answered.</summary>
    public bool IsWorking => Reachability is BridgeReachability.Reachable
        or BridgeReachability.Slow
        or BridgeReachability.Fronted;
}

/// <summary>
/// TCP-pings Tor bridge lines concurrently to find which endpoints are reachable from the
/// current network, mirroring the community "Bridge Scanner" tooling. This only opens a TCP
/// connection to host:port and times the handshake; it does not perform a Tor handshake, so a
/// "reachable" result means the address is not blocked at the transport layer (a strong signal
/// for picking bridges that work in your region).
/// </summary>
public static class BridgeScanService
{
    // Bridges below this many milliseconds are "fast"; reachable-but-slower are flagged but still work.
    public const int SlowThresholdMs = 500;

    private static readonly Regex Ipv4Endpoint =
        new(@"(\d{1,3}(?:\.\d{1,3}){3}):(\d{1,5})", RegexOptions.Compiled);

    private static readonly Regex Ipv6Endpoint =
        new(@"\[([0-9a-fA-F:]+)\]:(\d{1,5})", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownTransports = new(StringComparer.OrdinalIgnoreCase)
    {
        "obfs4", "webtunnel", "snowflake", "meek_lite", "meek-azure", "meek", "conjure", "scramblesuit",
        "obfs3", "obfs2", "vanilla", "dnstt"
    };

    /// <summary>
    /// Transports whose real endpoint is a URL/broker host rather than the bridge-line IP:port: the
    /// domain-fronted/broker transports (snowflake, meek, conjure, dnstt). For these we probe the
    /// url/front host, not the placeholder IP, otherwise they could never show as reachable. webtunnel
    /// is listed here too (its IP:port is an RFC 3849 2001:db8:: placeholder, and a no-<c>url=</c> line
    /// falls back to a front probe), but it is normally intercepted earlier in <see cref="ProbeAsync"/>
    /// and verified end-to-end with a real WebSocket-upgrade handshake instead of a front probe.
    /// </summary>
    private static readonly HashSet<string> FrontedTransports = new(StringComparer.OrdinalIgnoreCase)
    {
        "snowflake", "meek", "meek_lite", "meek-azure", "conjure", "dnstt", "webtunnel"
    };

    // Documentation/placeholder address ranges that never route: RFC 5737 (IPv4) and RFC 3849 (IPv6,
    // 2001:db8::/32). webtunnel/fronted lines carry these as filler; their real endpoint is a URL.
    private static readonly string[] PlaceholderPrefixes =
    {
        "192.0.2.", "198.51.100.", "203.0.113.", "0.0.0.0", "2001:db8:", "2001:0db8:"
    };

    /// <summary>
    /// Parse a bridge line into its transport label and TCP endpoint. Returns false when no
    /// IP:port endpoint can be extracted (e.g. snowflake/meek lines that have no direct endpoint).
    /// </summary>
    public static bool TryParseEndpoint(string line, out string transport, out string host, out int port)
    {
        transport = "vanilla";
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        // A "Bridge " prefix is allowed in torrc; strip it so the first token is the transport.
        if (trimmed.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring("Bridge ".Length).Trim();
        }

        var firstToken = trimmed.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        if (KnownTransports.Contains(firstToken))
        {
            transport = firstToken.ToLowerInvariant();
        }

        var v4 = Ipv4Endpoint.Match(trimmed);
        if (v4.Success && int.TryParse(v4.Groups[2].Value, out var p4) && p4 is > 0 and <= 65535)
        {
            host = v4.Groups[1].Value;
            port = p4;
            return true;
        }

        var v6 = Ipv6Endpoint.Match(trimmed);
        if (v6.Success && int.TryParse(v6.Groups[2].Value, out var p6) && p6 is > 0 and <= 65535)
        {
            host = v6.Groups[1].Value;
            port = p6;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Scan all bridge lines, reporting each result via <paramref name="progress"/> as it completes.
    /// Honors a worker cap and per-probe timeout. Cancellation stops further probes promptly.
    /// </summary>
    public static async Task<IReadOnlyList<BridgeScanResult>> ScanAsync(
        IReadOnlyList<string> bridgeLines,
        int workers,
        TimeSpan timeout,
        IProgress<BridgeScanResult>? progress,
        CancellationToken token)
    {
        if (bridgeLines == null)
        {
            throw new ArgumentNullException(nameof(bridgeLines));
        }

        var clampedWorkers = Math.Clamp(workers, 1, 64);
        var clampedTimeout = timeout < TimeSpan.FromMilliseconds(500)
            ? TimeSpan.FromMilliseconds(500)
            : timeout > TimeSpan.FromSeconds(60)
                ? TimeSpan.FromSeconds(60)
                : timeout;

        var candidates = bridgeLines
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        var results = new List<BridgeScanResult>(candidates.Count);
        var resultsLock = new object();
        using var throttle = new SemaphoreSlim(clampedWorkers, clampedWorkers);

        var tasks = candidates.Select(async line =>
        {
            await throttle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var result = await ProbeAsync(line, clampedTimeout, token).ConfigureAwait(false);
                lock (resultsLock)
                {
                    results.Add(result);
                }

                progress?.Report(result);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static async Task<BridgeScanResult> ProbeAsync(string line, TimeSpan timeout, CancellationToken token)
    {
        var transport = ParseTransport(line);
        var hasEndpoint = TryParseEndpoint(line, out _, out var host, out var port);

        // webtunnel has a real end-to-end handshake we can verify - a WebSocket Upgrade to its exact
        // url= endpoint must return 101 - unlike the broker/domain-fronted transports below. Probe the
        // actual bridge, not just its CDN front, so a dead webtunnel bridge whose front still serves
        // TLS is correctly reported unreachable instead of masquerading as "reachable".
        if (string.Equals(transport, "webtunnel", StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeWebtunnelAsync(line, transport, timeout, token).ConfigureAwait(false);
        }

        // Fronted transports (snowflake/meek/conjure) and lines whose only endpoint is a placeholder
        // are not directly pingable — probe the broker/front host on 443 (TLS) instead.
        if (FrontedTransports.Contains(transport) || (hasEndpoint && IsPlaceholderHost(host)))
        {
            return await ProbeFrontedAsync(line, transport, timeout, token).ConfigureAwait(false);
        }

        if (!hasEndpoint)
        {
            return new BridgeScanResult(line, transport, "?", 0, null, BridgeReachability.Unparsed, "no host:port");
        }

        token.ThrowIfCancellationRequested();

        // Direct transports usually carry an IP literal, but resolve hostnames too for robustness.
        var (ms, error) = await ProbeTcpAsync(host, port, timeout, token).ConfigureAwait(false);
        if (ms.HasValue)
        {
            var reachability = ms.Value < SlowThresholdMs ? BridgeReachability.Reachable : BridgeReachability.Slow;
            return new BridgeScanResult(line, transport, host, port, ms.Value, reachability, $"{ms.Value} ms");
        }

        return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, error ?? "unreachable");
    }

    /// <summary>
    /// Probe a domain-fronted transport by connecting to its broker/front host on 443. A reachable
    /// front strongly implies the bridge is usable from this network (snowflake/meek/conjure carry
    /// no fixed bridge endpoint to time directly).
    /// </summary>
    private static async Task<BridgeScanResult> ProbeFrontedAsync(
        string line, string transport, TimeSpan timeout, CancellationToken token)
    {
        var frontHost = ExtractFrontHost(line);
        if (string.IsNullOrWhiteSpace(frontHost))
        {
            return new BridgeScanResult(line, transport, "(fronted)", 0, null,
                BridgeReachability.Unparsed, "no broker/front host");
        }

        token.ThrowIfCancellationRequested();

        var (ms, error) = await ProbeTcpAsync(frontHost, 443, timeout, token).ConfigureAwait(false);
        if (ms.HasValue)
        {
            // Keep Detail short ("123 ms"): the front host is already shown in the scanner's Host
            // column, so repeating it here just overflows the fixed-width Status badge.
            return new BridgeScanResult(line, transport, frontHost, 443, ms.Value,
                BridgeReachability.Fronted, $"{ms.Value} ms");
        }

        return new BridgeScanResult(line, transport, frontHost, 443, null,
            BridgeReachability.Unreachable, error ?? "unreachable");
    }

    /// <summary>
    /// Real liveness probe for a webtunnel bridge. A webtunnel bridge is reached by a WebSocket
    /// Upgrade over HTTPS to its exact <c>url=</c> endpoint (front host + secret path); a live bridge
    /// answers <c>101 Switching Protocols</c>, while a dead bridge - or a bare CDN/front with nothing
    /// behind that path - answers 4xx/5xx or times out (a live bridge often even answers 502 to a
    /// plain GET, so only the upgrade handshake is reliable). This is far stronger than a TCP/TLS
    /// reach to the front host, which every CDN passes even with no bridge behind it - the false
    /// positive that let dead webtunnel bridges show as "reachable".
    /// </summary>
    private static async Task<BridgeScanResult> ProbeWebtunnelAsync(
        string line, string transport, TimeSpan timeout, CancellationToken token)
    {
        var url = ExtractUrl(line);
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            // No usable url= to verify - fall back to the front-host reachability probe.
            return await ProbeFrontedAsync(line, transport, timeout, token).ConfigureAwait(false);
        }

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 443;
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        IPAddress? address;
        try
        {
            if (!IPAddress.TryParse(host, out address))
            {
                var resolved = await Dns.GetHostAddressesAsync(host, timeoutCts.Token).ConfigureAwait(false);
                address = resolved.FirstOrDefault(a => !IsDisallowedProbeTarget(a));
                if (address == null)
                {
                    return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, "DNS: no public address");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, "DNS timed out");
        }
        catch (SocketException)
        {
            return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, "DNS failed");
        }

        if (IsDisallowedProbeTarget(address))
        {
            return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, "internal address (skipped)");
        }

        var stopwatch = Stopwatch.StartNew();
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);

            await using var network = new NetworkStream(socket, ownsSocket: false);
            // Accept any certificate: this is a reachability probe to a bridge front, not an identity
            // check. The accept-any callback is set ONLY via the options below - also passing it to the
            // SslStream constructor makes AuthenticateAsClientAsync throw "already set" and every probe fail.
            await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            var authOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            };
            await ssl.AuthenticateAsClientAsync(authOptions, timeoutCts.Token).ConfigureAwait(false);

            // Send the WebSocket Upgrade the webtunnel client itself uses, to the exact secret path.
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request =
                $"GET {pathAndQuery} HTTP/1.1\r\n" +
                $"Host: {host}\r\n" +
                "User-Agent: Mozilla/5.0\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), timeoutCts.Token).ConfigureAwait(false);

            var buffer = new byte[512];
            var total = 0;
            int read;
            while (total < buffer.Length &&
                   (read = await ssl.ReadAsync(buffer.AsMemory(total), timeoutCts.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n", StringComparison.Ordinal))
                {
                    break;
                }
            }
            stopwatch.Stop();

            var statusLine = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n", 2)[0];
            if (IsSwitchingProtocols(statusLine))
            {
                var ms = (int)stopwatch.ElapsedMilliseconds;
                var reachability = ms < SlowThresholdMs ? BridgeReachability.Reachable : BridgeReachability.Slow;
                return new BridgeScanResult(line, transport, host, port, ms, reachability, $"{ms} ms");
            }

            return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, "front only, no bridge");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, "timed out");
        }
        catch (AuthenticationException)
        {
            return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, "TLS failed");
        }
        catch (SocketException ex)
        {
            return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, DescribeSocketError(ex));
        }
        catch (Exception ex)
        {
            return new BridgeScanResult(line, transport, host, port, null, BridgeReachability.Unreachable, ex.Message);
        }
    }

    /// <summary>True when an HTTP status line reports <c>101 Switching Protocols</c> - the successful
    /// webtunnel WebSocket upgrade, i.e. a live bridge is answering at that path.</summary>
    internal static bool IsSwitchingProtocols(string? statusLine)
    {
        if (string.IsNullOrWhiteSpace(statusLine))
        {
            return false;
        }

        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            && parts[1] == "101";
    }

    /// <summary>
    /// Open a TCP connection to host:port (resolving DNS for hostnames) and time the handshake.
    /// Returns the elapsed milliseconds on success, or a short error description on failure.
    /// </summary>
    private static async Task<(int? Ms, string? Error)> ProbeTcpAsync(
        string host, int port, TimeSpan timeout, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        IPAddress? address;
        try
        {
            if (!IPAddress.TryParse(host, out address))
            {
                var resolved = await Dns.GetHostAddressesAsync(host, timeoutCts.Token).ConfigureAwait(false);
                address = resolved.FirstOrDefault();
                if (address == null)
                {
                    return (null, "DNS: no address");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "DNS timed out");
        }
        catch (SocketException)
        {
            return (null, "DNS failed");
        }

        if (IsDisallowedProbeTarget(address))
        {
            // Bridge lines come from remote lists. Never let one steer the scanner into TCP-probing
            // internal/loopback addresses (an SSRF / port-scan-oracle vector). Real bridges are on
            // public IPs; placeholder/doc addresses are already filtered upstream by IsPlaceholderHost.
            return (null, "internal address (skipped)");
        }

        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return ((int)stopwatch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // User-requested stop: propagate so the overall scan ends.
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "timed out");
        }
        catch (SocketException ex)
        {
            return (null, DescribeSocketError(ex));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // Reject loopback / private / link-local / CGNAT / multicast / unspecified targets so a remote
    // bridge list can't turn the reachability scanner into an internal port-scanner.
    internal static bool IsDisallowedProbeTarget(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)            // link-local
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT 100.64.0.0/10
                || b[0] == 0
                || b[0] >= 224;                            // multicast / reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsDisallowedProbeTarget(address.MapToIPv4());
            }

            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC                   // unique local fc00::/7
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6Loopback);
        }

        return false;
    }

    /// <summary>Extract just the transport label from a bridge line (no endpoint required).</summary>
    private static string ParseTransport(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "vanilla";
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring("Bridge ".Length).Trim();
        }

        var firstToken = trimmed.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return KnownTransports.Contains(firstToken) ? firstToken.ToLowerInvariant() : "vanilla";
    }

    internal static bool IsPlaceholderHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        foreach (var prefix in PlaceholderPrefixes)
        {
            // IPv6 hex can be upper- or lower-case, so compare case-insensitively (harmless for IPv4).
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return host == "::" || host == "::1";
    }

    /// <summary>
    /// Pull the broker/front host from a fronted bridge line. Prefers the <c>url=</c> host, then a
    /// dnstt <c>doh=</c>/<c>dot=</c> resolver host, then the first <c>fronts=</c> entry, then <c>front=</c>.
    /// </summary>
    internal static string? ExtractFrontHost(string line)
    {
        var url = MatchKeyValue(line, "url");
        if (!string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        // dnstt rides a DNS-over-HTTPS/TLS resolver; probe that resolver host.
        var doh = MatchKeyValue(line, "doh");
        if (!string.IsNullOrWhiteSpace(doh) &&
            Uri.TryCreate(doh, UriKind.Absolute, out var dohUri) &&
            !string.IsNullOrWhiteSpace(dohUri.Host))
        {
            return dohUri.Host;
        }

        var dot = MatchKeyValue(line, "dot");
        if (!string.IsNullOrWhiteSpace(dot))
        {
            // dot is host:port; strip the port for a TLS reachability probe.
            var colon = dot.LastIndexOf(':');
            return colon > 0 ? dot[..colon] : dot;
        }

        var fronts = MatchKeyValue(line, "fronts");
        if (!string.IsNullOrWhiteSpace(fronts))
        {
            var first = fronts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        var front = MatchKeyValue(line, "front");
        return string.IsNullOrWhiteSpace(front) ? null : front;
    }

    /// <summary>Extract the raw <c>url=</c> value (webtunnel's real HTTPS endpoint) from a bridge line.</summary>
    internal static string? ExtractUrl(string line) => MatchKeyValue(line, "url");

    private static string? MatchKeyValue(string line, string key)
    {
        var match = Regex.Match(line, $@"(?:^|\s){Regex.Escape(key)}=([^\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string DescribeSocketError(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.TimedOut => "timed out",
            SocketError.ConnectionRefused => "refused",
            SocketError.HostUnreachable => "host unreachable",
            SocketError.NetworkUnreachable => "network unreachable",
            _ => ex.SocketErrorCode.ToString()
        };
    }
}
