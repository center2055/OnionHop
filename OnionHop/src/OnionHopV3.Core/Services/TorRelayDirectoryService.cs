using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Models;

namespace OnionHopV3.Core.Services;

public sealed class TorRelayDirectoryService
{
    private const string OnionooUrl = "https://onionoo.torproject.org/details?running=true&fields=fingerprint,nickname,country,country_name,flags,advertised_bandwidth,or_addresses,first_seen,last_seen,last_restarted";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly string _cachePath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<TorRelayInfo>? _cachedRelays;
    private DateTimeOffset _cacheUpdatedUtc;

    public TorRelayDirectoryService()
    {
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OnionHop",
            "relay-directory.json");
    }

    public async Task<IReadOnlyList<TorRelayInfo>> GetRelaysAsync(Action<string> log, CancellationToken token)
    {
        if (_cachedRelays is { Count: > 0 } &&
            DateTimeOffset.UtcNow - _cacheUpdatedUtc <= CacheTtl)
        {
            return _cachedRelays;
        }

        await _loadLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cachedRelays is { Count: > 0 } &&
                DateTimeOffset.UtcNow - _cacheUpdatedUtc <= CacheTtl)
            {
                return _cachedRelays;
            }

            var loaded = LoadFromDisk();
            if (loaded.Relays is { Count: > 0 } && DateTimeOffset.UtcNow - loaded.UpdatedUtc <= CacheTtl)
            {
                _cachedRelays = loaded.Relays;
                _cacheUpdatedUtc = loaded.UpdatedUtc;
                return _cachedRelays;
            }

            var fetched = await FetchFromOnionooAsync(log, token).ConfigureAwait(false);
            if (fetched.Count > 0)
            {
                _cachedRelays = fetched;
                _cacheUpdatedUtc = DateTimeOffset.UtcNow;
                SaveToDisk(_cachedRelays, _cacheUpdatedUtc);
                return _cachedRelays;
            }

            if (loaded.Relays is { Count: > 0 })
            {
                _cachedRelays = loaded.Relays;
                _cacheUpdatedUtc = loaded.UpdatedUtc;
                return _cachedRelays;
            }

            return Array.Empty<TorRelayInfo>();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static async Task<IReadOnlyList<TorRelayInfo>> FetchFromOnionooAsync(Action<string> log, CancellationToken token)
    {
        try
        {
            using var response = await HttpClientFactory.LongTimeout.GetAsync(OnionooUrl, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log($"Relay directory fetch failed: HTTP {(int)response.StatusCode}.");
                return Array.Empty<TorRelayInfo>();
            }

            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("relays", out var relays) || relays.ValueKind != JsonValueKind.Array)
            {
                log("Relay directory fetch failed: Onionoo payload missing relay list.");
                return Array.Empty<TorRelayInfo>();
            }

            var list = new List<TorRelayInfo>();
            foreach (var relay in relays.EnumerateArray())
            {
                var fingerprint = relay.TryGetProperty("fingerprint", out var fingerprintElement)
                    ? fingerprintElement.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    continue;
                }

                var info = new TorRelayInfo
                {
                    Fingerprint = fingerprint,
                    Nickname = relay.TryGetProperty("nickname", out var nicknameElement)
                        ? nicknameElement.GetString()?.Trim() ?? fingerprint[..12]
                        : fingerprint[..12],
                    CountryCode = relay.TryGetProperty("country", out var countryElement)
                        ? countryElement.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                        : string.Empty,
                    CountryName = relay.TryGetProperty("country_name", out var countryNameElement)
                        ? countryNameElement.GetString()?.Trim() ?? string.Empty
                        : string.Empty,
                    AdvertisedBandwidth = relay.TryGetProperty("advertised_bandwidth", out var bandwidthElement)
                        ? bandwidthElement.GetInt64()
                        : 0,
                    FirstSeenUtc = ParseOnionooTimestamp(relay, "first_seen"),
                    LastSeenUtc = ParseOnionooTimestamp(relay, "last_seen"),
                    LastRestartedUtc = ParseOnionooTimestamp(relay, "last_restarted")
                };

                if (relay.TryGetProperty("or_addresses", out var addressElement) && addressElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in addressElement.EnumerateArray())
                    {
                        var address = item.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(address))
                        {
                            info.Addresses.Add(address);
                        }
                    }
                }

                if (relay.TryGetProperty("flags", out var flagsElement) && flagsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var flag in flagsElement.EnumerateArray())
                    {
                        var value = flag.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            info.Flags.Add(value);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(info.CountryName))
                {
                    info.CountryName = string.IsNullOrWhiteSpace(info.CountryCode)
                        ? "Unknown"
                        : info.CountryCode.ToUpperInvariant();
                }

                list.Add(info);
            }

            var ordered = list
                .OrderByDescending(relay => relay.AdvertisedBandwidth)
                .ThenBy(relay => relay.Nickname, StringComparer.OrdinalIgnoreCase)
                .ToList();

            log($"Relay directory updated: {ordered.Count} running relays loaded from Onionoo.");
            return ordered;
        }
        catch (Exception ex)
        {
            log($"Relay directory fetch failed: {ex.Message}");
            return Array.Empty<TorRelayInfo>();
        }
    }

    private (DateTimeOffset UpdatedUtc, IReadOnlyList<TorRelayInfo> Relays) LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return (DateTimeOffset.MinValue, Array.Empty<TorRelayInfo>());
            }

            var json = File.ReadAllText(_cachePath);
            var store = JsonSerializer.Deserialize<RelayCacheStore>(json);
            if (store?.Relays == null || store.Relays.Count == 0)
            {
                return (DateTimeOffset.MinValue, Array.Empty<TorRelayInfo>());
            }

            return (store.UpdatedUtc, store.Relays);
        }
        catch
        {
            return (DateTimeOffset.MinValue, Array.Empty<TorRelayInfo>());
        }
    }

    private void SaveToDisk(IReadOnlyList<TorRelayInfo> relays, DateTimeOffset updatedUtc)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var store = new RelayCacheStore
            {
                UpdatedUtc = updatedUtc,
                Relays = relays.ToList()
            };

            File.WriteAllText(_cachePath, JsonSerializer.Serialize(store, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // Cache is best-effort.
        }
    }

    private static DateTimeOffset? ParseOnionooTimestamp(JsonElement relay, string propertyName)
    {
        if (!relay.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed class RelayCacheStore
    {
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<TorRelayInfo> Relays { get; set; } = [];
    }
}
