using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Platform;

namespace OnionHopV3.Core.Services;

public sealed class TorNodeDatabaseService
{
    private const string OnionooUrl = "https://onionoo.torproject.org/details?running=true&fields=country,country_name,flags";
    private const string BundledSnapshotResourceName = "OnionHopV3.Core.Resources.tor-countries.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    // After a direct lookup failed (Onionoo blocked or offline), direct lookups wait this long before
    // trying again, so repeated calls do not each sit through the 30-second timeout. Lookups through
    // Tor are not held back.
    private static readonly TimeSpan DirectRetryDelay = TimeSpan.FromMinutes(10);

    private readonly string _cachePath;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private IReadOnlyList<TorCountryNodeStats>? _cachedCountries;
    private DateTimeOffset _cacheUpdatedUtc;
    private DateTimeOffset _directLookupFailedUtc = DateTimeOffset.MinValue;

    public TorNodeDatabaseService()
        : this(Path.Combine(AppDirectories.LocalData, "OnionHop V3", "tor-node-db.json"))
    {
    }

    internal TorNodeDatabaseService(string cachePath)
    {
        _cachePath = cachePath;
    }

    /// <summary>
    /// Whether the list held now is current Onionoo data (fetched, or cached on disk, within the last
    /// 12 hours). False while it is the bundled snapshot or an older cache, so a later call, for
    /// example through Tor once connected, still tries to fetch it.
    /// </summary>
    public bool HasCurrentData =>
        _cachedCountries is { Count: > 0 } && DateTimeOffset.UtcNow - _cacheUpdatedUtc <= CacheTtl;

    /// <summary>
    /// The country list available without the network: the last Onionoo list saved on disk, else the
    /// snapshot that ships with the app. Onionoo is on torproject.org, which is blocked in Russia, Iran,
    /// China and elsewhere, and the exit location is chosen before connecting, so the list has to be
    /// complete without it.
    /// </summary>
    public IReadOnlyList<TorCountryNodeStats> GetOfflineCountryStats()
    {
        var loaded = LoadFromDisk();
        return loaded.Countries.Count > 0 ? loaded.Countries : LoadBundledSnapshot().Countries;
    }

    /// <summary>The list held in memory, else <see cref="GetOfflineCountryStats"/>. Never uses the network.</summary>
    public IReadOnlyList<TorCountryNodeStats> GetCountryStatsWithoutLookup() =>
        _cachedCountries is { Count: > 0 } cached ? cached : GetOfflineCountryStats();

    /// <summary>
    /// Loads the country list from Onionoo, directly or through <paramref name="torClient"/> when one is
    /// given (a client for the live Tor SOCKS port), unless current data is cached. When the fetch
    /// fails it returns the older disk cache or the bundled snapshot.
    /// </summary>
    public async Task<IReadOnlyList<TorCountryNodeStats>> GetCountryStatsAsync(
        Action<string> log,
        CancellationToken token,
        HttpClient? torClient = null)
    {
        if (HasCurrentData)
        {
            return _cachedCountries!;
        }

        await _updateLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (HasCurrentData)
            {
                return _cachedCountries!;
            }

            var loaded = LoadFromDisk();
            if (loaded.Countries is { Count: > 0 } && DateTimeOffset.UtcNow - loaded.UpdatedUtc <= CacheTtl)
            {
                _cachedCountries = loaded.Countries;
                _cacheUpdatedUtc = loaded.UpdatedUtc;
                return _cachedCountries;
            }

            var directRecentlyFailed = torClient == null && DateTimeOffset.UtcNow - _directLookupFailedUtc < DirectRetryDelay;
            var fetched = directRecentlyFailed
                ? Array.Empty<TorCountryNodeStats>()
                : await FetchFromOnionooAsync(torClient ?? HttpClientFactory.Default, torClient != null, log, token).ConfigureAwait(false);
            if (fetched.Count == 0 && torClient == null && !directRecentlyFailed)
            {
                _directLookupFailedUtc = DateTimeOffset.UtcNow;
            }

            if (fetched.Count > 0)
            {
                _cachedCountries = fetched;
                _cacheUpdatedUtc = DateTimeOffset.UtcNow;
                SaveToDisk(_cachedCountries, _cacheUpdatedUtc);
                return _cachedCountries;
            }

            // Keep the date of what is returned, so it does not count as current and the next call
            // (through Tor once connected) tries again. The old fallback was stamped "now", which held
            // a list of 8 countries for 12 hours.
            if (loaded.Countries is { Count: > 0 })
            {
                _cachedCountries = loaded.Countries;
                _cacheUpdatedUtc = loaded.UpdatedUtc;
                return _cachedCountries;
            }

            var snapshot = LoadBundledSnapshot();
            if (torClient == null && !directRecentlyFailed)
            {
                log($"Using the country list that ships with OnionHop ({snapshot.Countries.Count} countries, from {snapshot.UpdatedUtc:yyyy-MM-dd}); it is loaded through Tor once you are connected.");
            }

            _cachedCountries = snapshot.Countries;
            _cacheUpdatedUtc = DateTimeOffset.MinValue;
            return _cachedCountries;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <summary>
    /// After connecting: fetches the list through Tor when what is held is not current, and returns it
    /// (null when nothing changed or the fetch failed).
    /// </summary>
    public async Task<IReadOnlyList<TorCountryNodeStats>?> RefreshThroughTorIfOutdatedAsync(
        HttpClient torClient,
        Action<string> log,
        CancellationToken token)
    {
        if (HasCurrentData)
        {
            return null;
        }

        var countries = await GetCountryStatsAsync(log, token, torClient).ConfigureAwait(false);
        return HasCurrentData ? countries : null;
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

    private static async Task<IReadOnlyList<TorCountryNodeStats>> FetchFromOnionooAsync(
        HttpClient client,
        bool throughTor,
        Action<string> log,
        CancellationToken token)
    {
        try
        {
            using var response = await client.GetAsync(OnionooUrl, token).ConfigureAwait(false);
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

            log(throughTor
                ? $"Tor node DB updated: {result.Count} countries loaded from Onionoo through Tor."
                : $"Tor node DB updated: {result.Count} countries loaded from Onionoo.");
            return result;
        }
        catch (Exception ex)
        {
            log(throughTor
                ? $"Tor node DB update through Tor failed: {ex.Message}"
                : $"Tor node DB update failed: {ex.Message}");
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

    /// <summary>
    /// The Onionoo country counts that ship with the app (Resources/tor-countries.json, in the disk
    /// cache's format). Refresh it with Onionoo's /details?running=true&amp;fields=country,country_name,flags
    /// before a release when the countries with exits have changed.
    /// </summary>
    internal static (DateTimeOffset UpdatedUtc, IReadOnlyList<TorCountryNodeStats> Countries) LoadBundledSnapshot()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledSnapshotResourceName);
            if (stream != null)
            {
                var store = JsonSerializer.Deserialize<NodeDbCacheStore>(stream);
                if (store?.Countries is { Count: > 0 })
                {
                    return (store.UpdatedUtc, store.Countries);
                }
            }
        }
        catch
        {
        }

        return (DateTimeOffset.MinValue, Array.Empty<TorCountryNodeStats>());
    }

    private sealed class NodeDbCacheStore
    {
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<TorCountryNodeStats> Countries { get; set; } = [];
    }
}
