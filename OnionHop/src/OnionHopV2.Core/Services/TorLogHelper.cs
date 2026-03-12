using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OnionHopV2.Core.Services;

internal static class TorLogHelper
{
    internal static int ExtractProgress(string line)
    {
        var percentIndex = line.IndexOf('%');
        if (percentIndex <= 0)
        {
            return 0;
        }

        var start = percentIndex - 1;
        while (start >= 0 && char.IsDigit(line[start]))
        {
            start--;
        }

        var number = line.Substring(start + 1, percentIndex - start - 1);
        return int.TryParse(number, out var value) ? value : 0;
    }

    internal static string? ExtractBootstrapSummary(string line)
    {
        var colonIndex = line.IndexOf("):", StringComparison.Ordinal);
        if (colonIndex < 0 || colonIndex + 2 >= line.Length)
        {
            return null;
        }

        var summary = line[(colonIndex + 2)..].Trim();
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    internal static bool IsFatalTorBootstrapLine(string line)
    {
        return line.Contains("no configured transport called", StringComparison.OrdinalIgnoreCase)
               || line.Contains("no such transport is supported", StringComparison.OrdinalIgnoreCase)
               || line.Contains("didn't launch any pluggable transport listeners", StringComparison.OrdinalIgnoreCase)
               || line.Contains("failed to bind", StringComparison.OrdinalIgnoreCase)
               || line.Contains("could not bind", StringComparison.OrdinalIgnoreCase)
               || line.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
               || line.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTorProxyHandshakeFailureLine(string line)
    {
        return line.Contains("handshaking (proxy)", StringComparison.OrdinalIgnoreCase)
               && line.Contains("general SOCKS server failure", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldLogTorLine(string line)
    {
        return line.Contains("Failed", StringComparison.OrdinalIgnoreCase)
               || line.Contains("[warn]", StringComparison.OrdinalIgnoreCase)
               || line.Contains("[err]", StringComparison.OrdinalIgnoreCase)
               || line.Contains("warn", StringComparison.OrdinalIgnoreCase)
               || line.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<int> ParseAllowedPorts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [80, 443];
        }

        var result = new List<int>();
        foreach (var token in raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var port) && port is >= 1 and <= 65535 && !result.Contains(port))
            {
                result.Add(port);
            }
        }

        return result.Count == 0 ? [80, 443] : result;
    }

    internal static IReadOnlyList<string> ParseProcessNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var token in line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = token.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.Contains('\\') || name.Contains('/'))
                {
                    // Path.GetFileName doesn't handle backslashes on Unix,
                    // so extract the filename manually for Windows-style paths.
                    var lastSep = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
                    name = lastSep >= 0 ? name[(lastSep + 1)..] : name;
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    results.Add(name);
                }
            }
        }

        return results
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static int NormalizePreferredProxyPort(int preferredPort, int fallbackPort)
    {
        return preferredPort is >= 1 and <= 65535
            ? preferredPort
            : fallbackPort;
    }

    internal static TimeSpan? ResolveConnectTimeout(int? configuredSeconds, TimeSpan automaticTimeout)
    {
        if (!configuredSeconds.HasValue)
        {
            return automaticTimeout;
        }

        if (configuredSeconds.Value <= 0)
        {
            return null;
        }

        var clampedSeconds = Math.Clamp(configuredSeconds.Value, 10, 3600);
        return TimeSpan.FromSeconds(clampedSeconds);
    }

    internal static bool? ParseToggleMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        if (string.Equals(mode, OnionHopConnectOptions.ToggleModeEnabled, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(mode, OnionHopConnectOptions.ToggleModeDisabled, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    internal static string? ParseConnectionPaddingMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        if (string.Equals(mode, OnionHopConnectOptions.ConnectionPaddingAuto, StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (string.Equals(mode, OnionHopConnectOptions.ConnectionPaddingEnabled, StringComparison.OrdinalIgnoreCase))
        {
            return "1";
        }

        if (string.Equals(mode, OnionHopConnectOptions.ConnectionPaddingDisabled, StringComparison.OrdinalIgnoreCase))
        {
            return "0";
        }

        return null;
    }

    internal static string NormalizeTunStackModeForSingBox(string? mode)
    {
        if (string.Equals(mode, OnionHopConnectOptions.TunStackSystem, StringComparison.OrdinalIgnoreCase))
        {
            return "system";
        }

        if (string.Equals(mode, OnionHopConnectOptions.TunStackGvisor, StringComparison.OrdinalIgnoreCase))
        {
            return "gvisor";
        }

        return "mixed";
    }

    internal static IReadOnlyList<string> LimitBridgeLinesForLaunch(IReadOnlyList<string> bridgeLines, int maxLines, int maxChars, Action<string> log)
    {
        if (bridgeLines.Count == 0)
        {
            return bridgeLines;
        }

        var selected = new List<string>(Math.Min(maxLines, bridgeLines.Count));
        var totalChars = 0;

        foreach (var line in bridgeLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            var estimatedArgChars = trimmed.Length + 12;
            if (selected.Count >= maxLines || totalChars + estimatedArgChars > maxChars)
            {
                break;
            }

            selected.Add(trimmed);
            totalChars += estimatedArgChars;
        }

        if (selected.Count == 0)
        {
            selected.Add(bridgeLines[0].Trim());
        }

        if (selected.Count < bridgeLines.Count)
        {
            log($"Using {selected.Count} of {bridgeLines.Count} bridge lines to avoid command-line length limits.");
        }

        return selected;
    }

    internal static string BuildManualProxyHint(string bindAddress, int socksPort, int? httpPort)
    {
        var endpointHost = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1" : bindAddress.Trim();
        var localHost = string.Equals(endpointHost, "0.0.0.0", StringComparison.Ordinal)
            ? "127.0.0.1"
            : endpointHost;
        var lanNote = string.Equals(endpointHost, "0.0.0.0", StringComparison.Ordinal)
            ? " LAN access is enabled; use this device's LAN IP from other devices."
            : string.Empty;

        if (httpPort.HasValue)
        {
            return $"Local proxy mode: configure apps manually (SOCKS {localHost}:{socksPort}, HTTP {localHost}:{httpPort.Value}).{lanNote}";
        }

        return $"Local proxy mode: configure apps manually (SOCKS {localHost}:{socksPort}).{lanNote}";
    }
}
