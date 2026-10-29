using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OnionHopV3.Core.Platform;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Remembers which connection strategy actually worked, keyed by the user's network (country + a
/// coarse IP-prefix fingerprint). On the next connect from the same network, Smart Connect can lead
/// with the remembered winner instead of re-deriving the whole ladder - turning a repeat connect from
/// "probe everything again" into "try what worked last time first". A failure of the remembered
/// strategy invalidates it so we don't keep retrying something that stopped working (censors move).
/// Failed attempts are remembered per transport too, so for a while a transport that just failed on
/// a network is tried last there instead of costing the user the same wait on every connect.
/// </summary>
public sealed class SmartConnectMemory
{
    /// <summary>A remembered successful strategy for one network.</summary>
    public sealed record Entry(string Transport, bool UseBridges, DateTimeOffset SucceededUtc);

    private static readonly TimeSpan EntryTtl = TimeSpan.FromDays(14);
    private const int MaxEntries = 64;

    // How long a failed transport stays at the back of the line on a network. Long enough that the
    // next few connects skip what just failed, short enough to try it again once censors move on.
    private static readonly TimeSpan FailureMemory = TimeSpan.FromHours(48);
    private const int MaxFailureEntries = 256;

    private readonly string _path;
    private readonly string _failuresPath;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;
    private Dictionary<string, DateTimeOffset>? _failures;

    public SmartConnectMemory(string? overridePath = null)
    {
        _path = overridePath ?? Path.Combine(
            AppDirectories.RoamingData,
            "OnionHop",
            "smartconnect-memory.json");
        _failuresPath = Path.Combine(
            Path.GetDirectoryName(_path) ?? string.Empty,
            Path.GetFileNameWithoutExtension(_path) + "-failures.json");
    }

    /// <summary>
    /// Build the network key from a country code and public IP. Uses the first two octets of an IPv4
    /// address (or the first IPv6 hextet pair) as a coarse network fingerprint: stable enough to
    /// survive a dynamic-IP change within the same ISP region, specific enough to tell networks apart.
    /// Where the address lookups are blocked the public IP is unknown, and every network in the
    /// country shared one memory (#89); the local default gateway then tells them apart instead, e.g.
    /// a home router from a phone's hotspot. Returns null when there isn't enough to form a key.
    /// </summary>
    public static string? BuildNetworkKey(string? countryCode, string? publicIp, string? localGateway = null)
    {
        var country = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim().ToUpperInvariant();
        var prefix = BuildIpPrefix(publicIp);
        if (prefix == null && !string.IsNullOrWhiteSpace(localGateway))
        {
            prefix = "gw:" + localGateway.Trim();
        }

        if (country == null && prefix == null)
        {
            return null;
        }

        return $"{country ?? "??"}/{prefix ?? "?"}";
    }

    private static string? BuildIpPrefix(string? publicIp)
    {
        if (string.IsNullOrWhiteSpace(publicIp) || !System.Net.IPAddress.TryParse(publicIp.Trim(), out var ip))
        {
            return null;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return $"{bytes[0]}.{bytes[1]}";
        }

        // IPv6: first two bytes (the top hextet) as a coarse prefix.
        return bytes.Length >= 2 ? $"{bytes[0]:x2}{bytes[1]:x2}" : null;
    }

    /// <summary>Return the remembered winner for this network, or null if none / expired.</summary>
    public Entry? TryGet(string? networkKey)
    {
        if (string.IsNullOrWhiteSpace(networkKey))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureLoaded();
            if (_entries!.TryGetValue(networkKey, out var entry))
            {
                if (DateTimeOffset.UtcNow - entry.SucceededUtc <= EntryTtl)
                {
                    return entry;
                }

                _entries.Remove(networkKey);
                Persist();
            }

            return null;
        }
    }

    /// <summary>Record that <paramref name="transport"/> connected successfully on this network.</summary>
    public void RecordSuccess(string? networkKey, string transport, bool useBridges)
    {
        if (string.IsNullOrWhiteSpace(networkKey) || string.IsNullOrWhiteSpace(transport))
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();
            _entries![networkKey] = new Entry(transport.Trim().ToLowerInvariant(), useBridges, DateTimeOffset.UtcNow);
            TrimToCapacity();
            Persist();

            // It works here now, whatever happened to it before.
            EnsureFailuresLoaded();
            if (_failures!.Remove(FailureKey(networkKey, transport)))
            {
                PersistFailures();
            }
        }
    }

    /// <summary>
    /// Record that <paramref name="transport"/> ("direct", "obfs4", ...) did not connect on this
    /// network, so the next connects try it last for a while.
    /// </summary>
    public void RecordFailure(string? networkKey, string transport)
    {
        if (string.IsNullOrWhiteSpace(networkKey) || string.IsNullOrWhiteSpace(transport))
        {
            return;
        }

        lock (_gate)
        {
            EnsureFailuresLoaded();
            _failures![FailureKey(networkKey, transport)] = DateTimeOffset.UtcNow;
            if (_failures.Count > MaxFailureEntries)
            {
                foreach (var oldest in _failures.OrderBy(pair => pair.Value).Take(_failures.Count - MaxFailureEntries).Select(pair => pair.Key).ToList())
                {
                    _failures.Remove(oldest);
                }
            }

            PersistFailures();
        }
    }

    /// <summary>Transports that failed on this network within the last 48 hours.</summary>
    public IReadOnlySet<string> GetRecentlyFailedTransports(string? networkKey)
    {
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(networkKey))
        {
            return failed;
        }

        var prefix = networkKey + "|";
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            EnsureFailuresLoaded();
            foreach (var (key, failedUtc) in _failures!)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal) && now - failedUtc <= FailureMemory)
                {
                    failed.Add(key[prefix.Length..]);
                }
            }
        }

        return failed;
    }

    private static string FailureKey(string networkKey, string transport) =>
        networkKey + "|" + transport.Trim().ToLowerInvariant();

    private void EnsureFailuresLoaded()
    {
        if (_failures != null)
        {
            return;
        }

        try
        {
            if (File.Exists(_failuresPath))
            {
                _failures = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_failuresPath));
            }
        }
        catch
        {
            // Corrupt/unreadable: start fresh.
        }

        _failures ??= new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    }

    private void PersistFailures()
    {
        try
        {
            var directory = Path.GetDirectoryName(_failuresPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_failuresPath, JsonSerializer.Serialize(_failures));
        }
        catch
        {
            // Persistence is best-effort.
        }
    }

    /// <summary>Forget the remembered winner for this network (it stopped working).</summary>
    public void Invalidate(string? networkKey)
    {
        if (string.IsNullOrWhiteSpace(networkKey))
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();
            if (_entries!.Remove(networkKey))
            {
                Persist();
            }
        }
    }

    private void TrimToCapacity()
    {
        if (_entries!.Count <= MaxEntries)
        {
            return;
        }

        // Drop the oldest entries beyond the cap.
        foreach (var key in _entries
                     .OrderBy(pair => pair.Value.SucceededUtc)
                     .Take(_entries.Count - MaxEntries)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(key);
        }
    }

    private void EnsureLoaded()
    {
        if (_entries != null)
        {
            return;
        }

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json)
                           ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
                return;
            }
        }
        catch
        {
            // Corrupt/unreadable cache: start fresh.
        }

        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch
        {
            // Persistence is best-effort.
        }
    }
}
