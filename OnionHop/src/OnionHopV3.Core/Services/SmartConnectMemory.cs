using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Remembers which connection strategy actually worked, keyed by the user's network (country + a
/// coarse IP-prefix fingerprint). On the next connect from the same network, Smart Connect can lead
/// with the remembered winner instead of re-deriving the whole ladder - turning a repeat connect from
/// "probe everything again" into "try what worked last time first". A failure of the remembered
/// strategy invalidates it so we don't keep retrying something that stopped working (censors move).
/// </summary>
public sealed class SmartConnectMemory
{
    /// <summary>A remembered successful strategy for one network.</summary>
    public sealed record Entry(string Transport, bool UseBridges, DateTimeOffset SucceededUtc);

    private static readonly TimeSpan EntryTtl = TimeSpan.FromDays(14);
    private const int MaxEntries = 64;

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;

    public SmartConnectMemory(string? overridePath = null)
    {
        _path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OnionHop",
            "smartconnect-memory.json");
    }

    /// <summary>
    /// Build the network key from a country code and public IP. Uses the first two octets of an IPv4
    /// address (or the first IPv6 hextet pair) as a coarse network fingerprint: stable enough to
    /// survive a dynamic-IP change within the same ISP region, specific enough to tell networks apart.
    /// Returns null when there isn't enough to form a meaningful key.
    /// </summary>
    public static string? BuildNetworkKey(string? countryCode, string? publicIp)
    {
        var country = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim().ToUpperInvariant();
        var prefix = BuildIpPrefix(publicIp);
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
