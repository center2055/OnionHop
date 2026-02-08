using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV2.Core.Models;

namespace OnionHopV2.Core.Services;

public sealed class TorNodeDatabaseService
{
    private const string OnionooUrl = "https://onionoo.torproject.org/details?running=true&fields=country,country_name,flags";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly string _cachePath;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private IReadOnlyList<TorCountryNodeStats>? _cachedCountries;
    private DateTimeOffset _cacheUpdatedUtc;

    public TorNodeDatabaseService()
    {
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OnionHop V2",
            "tor-node-db.json");
    }

    public async Task<IReadOnlyList<TorCountryNodeStats>> GetCountryStatsAsync(Action<string> log, CancellationToken token)
    {
        if (_cachedCountries is { Count: > 0 } && DateTimeOffset.UtcNow - _cacheUpdatedUtc <= CacheTtl)
        {
            return _cachedCountries;
        }

        await _updateLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cachedCountries is { Count: > 0 } && DateTimeOffset.UtcNow - _cacheUpdatedUtc <= CacheTtl)
            {
                return _cachedCountries;
            }

            var loaded = LoadFromDisk();
            if (loaded.Countries is { Count: > 0 } && DateTimeOffset.UtcNow - loaded.UpdatedUtc <= CacheTtl)
            {
                _cachedCountries = loaded.Countries;
                _cacheUpdatedUtc = loaded.UpdatedUtc;
                return _cachedCountries;
            }

            var fetched = await FetchFromOnionooAsync(log, token).ConfigureAwait(false);
            if (fetched.Count > 0)
            {
                _cachedCountries = fetched;
                _cacheUpdatedUtc = DateTimeOffset.UtcNow;
                SaveToDisk(_cachedCountries, _cacheUpdatedUtc);
                return _cachedCountries;
            }

            if (loaded.Countries is { Count: > 0 })
            {
                _cachedCountries = loaded.Countries;
                _cacheUpdatedUtc = loaded.UpdatedUtc;
                return _cachedCountries;
            }

            _cachedCountries = GetFallbackCountries();
            _cacheUpdatedUtc = DateTimeOffset.UtcNow;
            return _cachedCountries;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public static string NormalizeSelectionToCountryCode(string? selection, IReadOnlyList<TorCountryNodeStats> countries)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return string.Empty;
        }

        var trimmed = selection.Trim();
        if (string.Equals(trimmed, OnionHopConnectOptions.AutomaticLocationLabel, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (trimmed.Length == 2)
        {
            return trimmed.ToLowerInvariant();
        }

        var match = countries.FirstOrDefault(country =>
            string.Equals(country.CountryName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match.CountryCode;
        }

        // Legacy settings compatibility.
        return trimmed switch
        {
            "United States" => "us",
            "United Kingdom" => "gb",
            "Germany" => "de",
            "France" => "fr",
            "Switzerland" => "ch",
            "Netherlands" => "nl",
            "Canada" => "ca",
            "Singapore" => "sg",
            _ => string.Empty
        };
    }

    public static bool HasExitNodes(IReadOnlyList<TorCountryNodeStats> countries, string countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return true;
        }

        return countries.Any(country =>
            string.Equals(country.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)
            && country.ExitNodes > 0);
    }

    public static bool HasEntryNodes(IReadOnlyList<TorCountryNodeStats> countries, string countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return true;
        }

        return countries.Any(country =>
            string.Equals(country.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)
            && country.EntryNodes > 0);
    }

    private static async Task<IReadOnlyList<TorCountryNodeStats>> FetchFromOnionooAsync(Action<string> log, CancellationToken token)
    {
        try
        {
            using var response = await HttpClientFactory.Default.GetAsync(OnionooUrl, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log($"Tor node DB update failed: HTTP {(int)response.StatusCode}.");
                return Array.Empty<TorCountryNodeStats>();
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("relays", out var relays) || relays.ValueKind != JsonValueKind.Array)
            {
                log("Tor node DB update failed: Onionoo payload missing relay list.");
                return Array.Empty<TorCountryNodeStats>();
            }

            var map = new Dictionary<string, TorCountryNodeStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var relay in relays.EnumerateArray())
            {
                if (!relay.TryGetProperty("country", out var countryCodeElement))
                {
                    continue;
                }

                var countryCode = countryCodeElement.GetString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(countryCode))
                {
                    continue;
                }

                if (!map.TryGetValue(countryCode, out var stats))
                {
                    var countryName = relay.TryGetProperty("country_name", out var countryNameElement)
                        ? countryNameElement.GetString()
                        : null;
                    stats = new TorCountryNodeStats
                    {
                        CountryCode = countryCode,
                        CountryName = string.IsNullOrWhiteSpace(countryName)
                            ? countryCode.ToUpperInvariant()
                            : countryName
                    };
                    map[countryCode] = stats;
                }

                stats.TotalNodes++;

                if (relay.TryGetProperty("flags", out var flagsElement) && flagsElement.ValueKind == JsonValueKind.Array)
                {
                    var hasGuard = false;
                    var hasExit = false;
                    foreach (var flag in flagsElement.EnumerateArray())
                    {
                        var value = flag.GetString();
                        if (!hasGuard && string.Equals(value, "Guard", StringComparison.OrdinalIgnoreCase))
                        {
                            hasGuard = true;
                        }

                        if (!hasExit && string.Equals(value, "Exit", StringComparison.OrdinalIgnoreCase))
                        {
                            hasExit = true;
                        }
                    }

                    if (hasGuard)
                    {
                        stats.EntryNodes++;
                    }

                    if (hasExit)
                    {
                        stats.ExitNodes++;
                    }
                }
            }

            var result = map.Values
                .OrderBy(country => country.CountryName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            log($"Tor node DB updated: {result.Count} countries loaded from Onionoo.");
            return result;
        }
        catch (Exception ex)
        {
            log($"Tor node DB update failed: {ex.Message}");
            return Array.Empty<TorCountryNodeStats>();
        }
    }

    private (DateTimeOffset UpdatedUtc, IReadOnlyList<TorCountryNodeStats> Countries) LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return (DateTimeOffset.MinValue, Array.Empty<TorCountryNodeStats>());
            }

            var json = File.ReadAllText(_cachePath);
            var store = JsonSerializer.Deserialize<NodeDbCacheStore>(json);
            if (store?.Countries == null || store.Countries.Count == 0)
            {
                return (DateTimeOffset.MinValue, Array.Empty<TorCountryNodeStats>());
            }

            return (store.UpdatedUtc, store.Countries);
        }
        catch
        {
            return (DateTimeOffset.MinValue, Array.Empty<TorCountryNodeStats>());
        }
    }

    private void SaveToDisk(IReadOnlyList<TorCountryNodeStats> countries, DateTimeOffset updatedUtc)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var store = new NodeDbCacheStore
            {
                UpdatedUtc = updatedUtc,
                Countries = countries.ToList()
            };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Cache is best-effort.
        }
    }

    private static IReadOnlyList<TorCountryNodeStats> GetFallbackCountries()
    {
        return
        [
            new TorCountryNodeStats { CountryCode = "us", CountryName = "United States" },
            new TorCountryNodeStats { CountryCode = "gb", CountryName = "United Kingdom" },
            new TorCountryNodeStats { CountryCode = "de", CountryName = "Germany" },
            new TorCountryNodeStats { CountryCode = "fr", CountryName = "France" },
            new TorCountryNodeStats { CountryCode = "ch", CountryName = "Switzerland" },
            new TorCountryNodeStats { CountryCode = "nl", CountryName = "Netherlands" },
            new TorCountryNodeStats { CountryCode = "ca", CountryName = "Canada" },
            new TorCountryNodeStats { CountryCode = "sg", CountryName = "Singapore" }
        ];
    }

    private sealed class NodeDbCacheStore
    {
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<TorCountryNodeStats> Countries { get; set; } = [];
    }
}
