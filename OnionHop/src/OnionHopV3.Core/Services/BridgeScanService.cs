using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    /// The transport reaches Tor through domain fronting / a broker (snowflake, meek, conjure), so
    /// there is no direct bridge endpoint to ping. We instead probed the front/broker host and it
    /// answered — the bridge is usable even though it has no classic IP:port to time.
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
    /// Transports that don't connect to a fixed bridge IP:port — they reach Tor through a broker
    /// and/or domain fronting (the listed endpoint is a placeholder such as 192.0.2.x). For these we
    /// probe the front/broker host instead of the placeholder.
    /// </summary>
    private static readonly HashSet<string> FrontedTransports = new(StringComparer.OrdinalIgnoreCase)
    {
        "snowflake", "meek", "meek_lite", "meek-azure", "conjure", "dnstt"
    };

    // RFC 5737 documentation ranges + unspecified addresses used as placeholders in fronted lines.
    private static readonly string[] PlaceholderPrefixes =
    {
        "192.0.2.", "198.51.100.", "203.0.113.", "0.0.0.0"
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
            return new BridgeScanResult(line, transport, frontHost, 443, ms.Value,
                BridgeReachability.Fronted, $"broker {ms.Value} ms");
        }

        return new BridgeScanResult(line, transport, frontHost, 443, null,
            BridgeReachability.Unreachable, $"broker {error ?? "unreachable"}");
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

    private static bool IsPlaceholderHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        foreach (var prefix in PlaceholderPrefixes)
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal))
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
