using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using OnionHopV3.Core.Tor;

namespace OnionHopV3.Core.Services;

internal sealed class SingBoxLogProcessor
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex SingBoxConnectionToRegex = new(@"connection to (?<dest>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SingBoxConnectionIdRegex = new(@"\[(?<id>\d+)\s", RegexOptions.Compiled);
    private static readonly Regex SingBoxDirectOutboundDestRegex = new(@"outbound/direct\[[^\]]+\]: outbound connection to (?<dest>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SingBoxClosedConnectionDestRegex = new(@"->(?<dest>\[[^\]]+\]:\d+|[^:\s]+:\d+):", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly object _logLock = new();
    private readonly Queue<string> _recentLines = new();
    private readonly object _bridgeFailureLock = new();
    private readonly HashSet<string> _webTunnelConnectionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _webTunnelConnectionDestinations = new(StringComparer.Ordinal);
    private DateTime _lastVpnMessageUtc = DateTime.MinValue;
    private string _sourceLabel = "sing-box";

    public event Action<string>? LogReceived;
    public event Action<string>? DnsLogReceived;

    public void SetSourceLabel(string? sourceLabel)
    {
        _sourceLabel = string.IsNullOrWhiteSpace(sourceLabel)
            ? "vpn"
            : sourceLabel.Trim();
    }

    public string? ProcessLine(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        var line = AnsiEscapeRegex.Replace(data, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        LogReceived?.Invoke($"{_sourceLabel}: {line}");
        if (LooksLikeDnsLogLine(line))
        {
            DnsLogReceived?.Invoke($"{_sourceLabel}: {line}");
        }
        lock (_logLock)
        {
            _recentLines.Enqueue(line);
            while (_recentLines.Count > 40)
            {
                _recentLines.Dequeue();
            }
        }

        if (line.Contains("socks5: request rejected", StringComparison.OrdinalIgnoreCase))
        {
            var destMatch = SingBoxConnectionToRegex.Match(line);
            var dest = destMatch.Success ? destMatch.Groups["dest"].Value : "a destination";
            var now = DateTime.UtcNow;
            if (now - _lastVpnMessageUtc >= TimeSpan.FromSeconds(10))
            {
                _lastVpnMessageUtc = now;
                // A single per-connection exit rejection (commonly an IPv6 destination, or a port the
                // chosen exit's policy blocks). The tunnel itself is fine - Tor just retries on another
                // circuit - so surface this as a log note instead of hijacking the headline status with
                // an alarming "Tor rejected" message while we are actually Connected.
                LogReceived?.Invoke($"{_sourceLabel}: note - an exit rejected a connection to {dest} (often an IPv6 address or a port the exit's policy blocks); harmless, Tor retries on another circuit.");
            }
        }

        return line;
    }

    public void TrackWebTunnelBridgeHealth(string line, Func<string?> getActiveBridgeType, TorBridgeManager bridgeManager, Action<string> log)
    {
        var id = TryExtractConnectionId(line);
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (line.Contains("router: found process path:", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("webtunnel-client.exe", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("webtunnel-client", StringComparison.OrdinalIgnoreCase)))
        {
            lock (_bridgeFailureLock)
            {
                _webTunnelConnectionIds.Add(id);
                if (_webTunnelConnectionIds.Count > 4096)
                {
                    _webTunnelConnectionIds.Clear();
                    _webTunnelConnectionDestinations.Clear();
                }
            }

            return;
        }

        if (line.Contains("outbound/direct", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("outbound connection to ", StringComparison.OrdinalIgnoreCase))
        {
            var destinationMatch = SingBoxDirectOutboundDestRegex.Match(line);
            if (destinationMatch.Success)
            {
                lock (_bridgeFailureLock)
                {
                    if (_webTunnelConnectionIds.Contains(id))
                    {
                        _webTunnelConnectionDestinations[id] = destinationMatch.Groups["dest"].Value;
                    }
                }
            }

            return;
        }

        if (!line.Contains("connection download closed", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? destinationFromMap;
        bool wasTrackedWebTunnelConnection;
        lock (_bridgeFailureLock)
        {
            wasTrackedWebTunnelConnection = _webTunnelConnectionIds.Contains(id) || _webTunnelConnectionDestinations.ContainsKey(id);
            _webTunnelConnectionDestinations.TryGetValue(id, out destinationFromMap);
            _webTunnelConnectionDestinations.Remove(id);
            _webTunnelConnectionIds.Remove(id);
        }

        if (!wasTrackedWebTunnelConnection)
        {
            return;
        }

        var destinationMatchFromLine = SingBoxClosedConnectionDestRegex.Match(line);
        var destination = destinationMatchFromLine.Success
            ? destinationMatchFromLine.Groups["dest"].Value
            : destinationFromMap;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        var activeBridgeType = getActiveBridgeType();
        if (string.IsNullOrWhiteSpace(activeBridgeType) ||
            !string.Equals(activeBridgeType, "webtunnel", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bridgeManager.ReportRuntimeBridgeFailure(activeBridgeType, destination, log);
    }

    public void ClearConnectionTracking()
    {
        lock (_bridgeFailureLock)
        {
            _webTunnelConnectionIds.Clear();
            _webTunnelConnectionDestinations.Clear();
        }
    }

    public IReadOnlyList<string> GetRecentLines(int maxLines = 6)
    {
        lock (_logLock)
        {
            var lines = new List<string>(_recentLines);
            if (lines.Count > maxLines)
            {
                return lines.GetRange(lines.Count - maxLines, maxLines);
            }

            return lines;
        }
    }

    public void ClearRecentLines()
    {
        lock (_logLock)
        {
            _recentLines.Clear();
        }
    }

    internal static bool LooksLikeDnsLogLine(string line)
    {
        return line.Contains("doh", StringComparison.OrdinalIgnoreCase)
               || line.Contains("dns", StringComparison.OrdinalIgnoreCase)
               || line.Contains("hijack-dns", StringComparison.OrdinalIgnoreCase)
               || line.Contains("[dns]", StringComparison.OrdinalIgnoreCase)
               || line.Contains(" protocol=dns", StringComparison.OrdinalIgnoreCase)
               || line.Contains(" protocol dns", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? TryExtractConnectionId(string line)
    {
        var match = SingBoxConnectionIdRegex.Match(line);
        return match.Success ? match.Groups["id"].Value : null;
    }
}
