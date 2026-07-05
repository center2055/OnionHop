using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

public enum SniReachability
{
    /// <summary>TLS handshake completed quickly - the SNI is not blocked and the host is reachable.</summary>
    Reachable,

    /// <summary>TLS handshake completed but slowly.</summary>
    Slow,

    /// <summary>TCP/TLS failed - the SNI is likely blocked (RST/timeout) or the host is down.</summary>
    Blocked,

    /// <summary>The domain could not be resolved / the target was skipped.</summary>
    Unresolved
}

/// <summary>Result of probing one SNI against one endpoint.</summary>
public sealed record SniScanResult(
    string Sni,
    string Ip,
    int Port,
    int? PingMs,
    bool TlsOk,
    SniReachability Reachability,
    string Detail)
{
    /// <summary>A "working" SNI is one whose TLS handshake completed (fast or slow) - usable as a
    /// front / sni= value for webtunnel/meek/snowflake bridges.</summary>
    public bool IsWorking => Reachability is SniReachability.Reachable or SniReachability.Slow;
}

/// <summary>
/// SNI scanner (v3.6). Two modes for finding SNI/front hosts that work on the current network:
///
/// - <b>Domain mode</b> (<see cref="ScanDomainsAsync"/>): a list of candidate domains is probed by
///   resolving each and doing a TLS handshake on :443 with that domain as the SNI. A completed
///   handshake means the domain is reachable and its SNI is not blocked - a good front/SNI candidate.
///
/// - <b>Range mode</b> (<see cref="ScanCidrAsync"/>): one fixed SNI plus an IPv4 CIDR range; each IP
///   in the range gets a TLS handshake using that SNI, finding which IPs serve it (domain-fronting
///   IP discovery). Bounded by a host cap so a wide range can't run unbounded.
///
/// Both reuse <see cref="BridgeScanService.IsDisallowedProbeTarget"/> so the scanner can never be
/// turned into an internal port scanner, and only ever open a TLS connection - no data is sent.
/// </summary>
public static class SniScanService
{
    public const int SlowThresholdMs = 700;
    public const int DefaultPort = 443;

    /// <summary>Hard cap on IPs enumerated from a CIDR range, so a wide prefix stays bounded.</summary>
    public const int MaxRangeHosts = 4096;

    /// <summary>
    /// Probe each candidate domain: resolve it, then TLS-handshake its address on <paramref name="port"/>
    /// using the domain as SNI. Reports each result as it completes.
    /// </summary>
    public static async Task<IReadOnlyList<SniScanResult>> ScanDomainsAsync(
        IReadOnlyList<string> domains,
        int workers,
        TimeSpan timeout,
        int port,
        IProgress<SniScanResult>? progress,
        CancellationToken token)
    {
        if (domains == null)
        {
            throw new ArgumentNullException(nameof(domains));
        }

        var effectivePort = NormalizePort(port);
        var candidates = domains
            .Select(NormalizeSniHost)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await RunAsync(
            candidates,
            workers,
            timeout,
            (sni, to, ct) => ProbeDomainAsync(sni, effectivePort, to, ct),
            progress,
            token).ConfigureAwait(false);
    }

    /// <summary>
    /// Probe a single SNI against every host in an IPv4 CIDR range (capped at <see cref="MaxRangeHosts"/>).
    /// <paramref name="truncated"/> reports whether the range was larger than the cap.
    /// </summary>
    public static async Task<IReadOnlyList<SniScanResult>> ScanCidrAsync(
        string sni,
        string cidr,
        int workers,
        TimeSpan timeout,
        int port,
        IProgress<SniScanResult>? progress,
        CancellationToken token)
    {
        var host = NormalizeSniHost(sni);
        if (host.Length == 0)
        {
            throw new ArgumentException("An SNI host is required for range mode.", nameof(sni));
        }

        if (!TryEnumerateCidr(cidr, MaxRangeHosts, out var addresses, out _))
        {
            throw new ArgumentException($"'{cidr}' is not a valid IPv4 CIDR range (e.g. 104.16.0.0/24).", nameof(cidr));
        }

        var effectivePort = NormalizePort(port);
        return await RunAsync(
            addresses,
            workers,
            timeout,
            (ip, to, ct) => ProbeIpAsync(host, IPAddress.Parse(ip), effectivePort, to, ct),
            progress,
            token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SniScanResult>> RunAsync(
        IReadOnlyList<string> items,
        int workers,
        TimeSpan timeout,
        Func<string, TimeSpan, CancellationToken, Task<SniScanResult>> probe,
        IProgress<SniScanResult>? progress,
        CancellationToken token)
    {
        var clampedWorkers = Math.Clamp(workers, 1, 64);
        var clampedTimeout = timeout < TimeSpan.FromMilliseconds(500)
            ? TimeSpan.FromMilliseconds(500)
            : timeout > TimeSpan.FromSeconds(60)
                ? TimeSpan.FromSeconds(60)
                : timeout;

        var results = new List<SniScanResult>(items.Count);
        var resultsLock = new object();
        using var throttle = new SemaphoreSlim(clampedWorkers, clampedWorkers);

        var tasks = items.Select(async item =>
        {
            await throttle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var result = await probe(item, clampedTimeout, token).ConfigureAwait(false);
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

    private static async Task<SniScanResult> ProbeDomainAsync(string sni, int port, TimeSpan timeout, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        IPAddress? address;
        try
        {
            var resolved = await Dns.GetHostAddressesAsync(sni, timeoutCts.Token).ConfigureAwait(false);
            address = resolved.FirstOrDefault(a => !BridgeScanService.IsDisallowedProbeTarget(a));
            if (address == null)
            {
                return new SniScanResult(sni, "?", port, null, false, SniReachability.Unresolved, "no public address");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SniScanResult(sni, "?", port, null, false, SniReachability.Unresolved, "DNS timed out");
        }
        catch (SocketException)
        {
            return new SniScanResult(sni, "?", port, null, false, SniReachability.Unresolved, "DNS failed");
        }

        return await ProbeIpAsync(sni, address, port, timeout, token).ConfigureAwait(false);
    }

    private static async Task<SniScanResult> ProbeIpAsync(string sni, IPAddress address, int port, TimeSpan timeout, CancellationToken token)
    {
        var ip = address.ToString();
        if (BridgeScanService.IsDisallowedProbeTarget(address))
        {
            // Never TLS-probe internal/loopback addresses (SSRF / internal-scan guard).
            return new SniScanResult(sni, ip, port, null, false, SniReachability.Unresolved, "internal address (skipped)");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);

            await using var network = new NetworkStream(socket, ownsSocket: false);
            // Certificate is intentionally not validated: this is a reachability/SNI-block probe, and
            // in range mode we connect to arbitrary IPs where a cert-name match is never expected. A
            // completed handshake is the signal we care about (the SNI was not reset/blocked). The
            // accept-any callback is set ONLY via the options below - also passing it to the SslStream
            // constructor makes AuthenticateAsClientAsync throw "already set" and every probe fail.
            await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            var authOptions = new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            };

            await ssl.AuthenticateAsClientAsync(authOptions, timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var ms = (int)stopwatch.ElapsedMilliseconds;
            var reachability = ms < SlowThresholdMs ? SniReachability.Reachable : SniReachability.Slow;
            return new SniScanResult(sni, ip, port, ms, true, reachability, $"{ms} ms");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SniScanResult(sni, ip, port, null, false, SniReachability.Blocked, "timed out");
        }
        catch (AuthenticationException)
        {
            // The TCP connection opened but TLS did not complete - a common censorship signature for a
            // blocked SNI (the middlebox resets after seeing the ClientHello SNI).
            return new SniScanResult(sni, ip, port, null, false, SniReachability.Blocked, "TLS failed (SNI likely blocked)");
        }
        catch (SocketException ex)
        {
            return new SniScanResult(sni, ip, port, null, false, SniReachability.Blocked, DescribeSocketError(ex));
        }
        catch (Exception ex)
        {
            return new SniScanResult(sni, ip, port, null, false, SniReachability.Blocked, ex.Message);
        }
    }

    /// <summary>
    /// Enumerate the usable host addresses of an IPv4 CIDR range, up to <paramref name="max"/>. Returns
    /// false for anything that is not a valid IPv4 CIDR. <paramref name="truncated"/> is true when the
    /// range held more hosts than the cap.
    /// </summary>
    public static bool TryEnumerateCidr(string cidr, int max, out List<string> addresses, out bool truncated)
    {
        addresses = new List<string>();
        truncated = false;
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        var slash = cidr.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var ipPart = cidr[..slash].Trim();
        var prefixPart = cidr[(slash + 1)..].Trim();
        if (!IPAddress.TryParse(ipPart, out var baseIp)
            || baseIp.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(prefixPart, out var prefix)
            || prefix is < 0 or > 32)
        {
            return false;
        }

        var baseBytes = baseIp.GetAddressBytes();
        var baseValue = (uint)((baseBytes[0] << 24) | (baseBytes[1] << 16) | (baseBytes[2] << 8) | baseBytes[3]);
        var mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        var network = baseValue & mask;

        // Total addresses in the block as a long so prefix 0 (2^32) doesn't overflow a uint shift.
        long total = 1L << (32 - prefix);
        // Skip the network + broadcast address for /0../30; use every address for /31 and /32.
        long firstOffset = prefix <= 30 ? 1L : 0L;
        long lastOffset = prefix <= 30 ? total - 2L : total - 1L;

        for (long offset = firstOffset; offset <= lastOffset; offset++)
        {
            if (addresses.Count >= max)
            {
                truncated = true;
                break;
            }

            var value = network + (uint)offset;
            addresses.Add($"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}");
        }

        return true;
    }

    /// <summary>Strip scheme/path/port so an entry like "https://example.com/x" or "example.com:443"
    /// becomes the bare host used as the SNI.</summary>
    public static string NormalizeSniHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var v = value.Trim();
        if (v.StartsWith("#", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var scheme = v.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            v = v[(scheme + 3)..];
        }

        var slash = v.IndexOf('/');
        if (slash >= 0)
        {
            v = v[..slash];
        }

        // Strip a trailing :port (but keep IPv6 bracket forms out - SNI is a hostname, not an IP:port).
        var colon = v.LastIndexOf(':');
        if (colon > 0 && v.IndexOf(':') == colon && int.TryParse(v[(colon + 1)..], out _))
        {
            v = v[..colon];
        }

        return v.Trim().TrimEnd('.');
    }

    private static int NormalizePort(int port) => port is > 0 and <= 65535 ? port : DefaultPort;

    private static string DescribeSocketError(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.TimedOut => "timed out",
            SocketError.ConnectionRefused => "refused",
            SocketError.ConnectionReset => "reset (likely blocked)",
            SocketError.HostUnreachable => "host unreachable",
            SocketError.NetworkUnreachable => "network unreachable",
            _ => ex.SocketErrorCode.ToString()
        };
    }
}
