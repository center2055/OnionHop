using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Platform;
using OnionHopV3.Core.Services;

namespace OnionHopV3.Core.Tor;

internal sealed class TorBridgeManager
{
    private static readonly Regex BridgeFrontsRegex = new(@"\bfronts=(?<value>[^\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BridgeFrontRegex = new(@"\bfront=(?<value>[^\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BridgeSniRegex = new(@"\bsni=(?<value>[^\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WebTunnelBridgeRegex = new(
        @"(?:^|[\r\n])\s*(?:Bridge\s+)?(?<line>webtunnel\s+[^\s<]+:\d{1,5}\s+[A-Fa-f0-9]{40}\s+url=[^\s<]+(?:\s+[^\s<]+=[^\s<]+)*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BridgeFingerprintRegex = new("^[A-Fa-f0-9]{40}$", RegexOptions.Compiled);

    public const string AutomaticBridgeType = "automatic";
    public const string VanillaBridgeType = "vanilla";
    private const string WebTunnelBridgeType = "webtunnel";
    private const string SnowflakeBridgeType = "snowflake";
    private const string Obfs4BridgeType = "obfs4";
    private const string ConjureBridgeType = "conjure";
    private const string DnsttBridgeType = "dnstt";
    private static readonly string[] AutomaticBridgeFallbackChain = [WebTunnelBridgeType, SnowflakeBridgeType, Obfs4BridgeType];
    private const string BundledBridgeFilePrefix = "bridges-";
    private const string CommunityBridgeFilePrefix = "bridges-community-";
    private const string BundledBridgeFileExtension = ".txt";

    // A live bridge-service fetch under this many bridges is "thin" and gets topped up from the
    // offline lists, capped at MaxSupplementedBridgeCount so Tor isn't handed an unwieldy bridge set.
    private const int MinHealthyBridgeCount = 8;
    private const int MaxSupplementedBridgeCount = 14;
    // How long a cached bridge set is considered "fresh" before we proactively try to re-fetch. Kept
    // generous (a week) so a reinstall, app update, or restart reuses the bridges that already worked
    // instead of going back to the network every 12 hours - and if a re-fetch fails (e.g. a censored
    // network blocking the bridge sources), GetStaleCacheFallbackBridgeLines still serves the old cache
    // rather than nothing. Users can always force a refresh from the Home/Scanner "Update bridges" button.
    private static readonly TimeSpan BridgeCacheTtl = TimeSpan.FromDays(7);
    private const int RuntimeBridgeFailureThreshold = 3;
    private static readonly TimeSpan RuntimeBridgeFailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RuntimeBridgePenaltyDuration = TimeSpan.FromMinutes(30);
    private const string MoatBuiltinUrl = "https://bridges.torproject.org/moat/circumvention/builtin";
    private const string MoatSettingsUrl = "https://bridges.torproject.org/moat/circumvention/settings";
    private const string MoatDefaultsUrl = "https://bridges.torproject.org/moat/circumvention/defaults";
    private enum BridgeSourcePreference
    {
        Auto,
        OnlineOnly,
        OfflineOnly,
        CollectorOnly
    }

    private readonly string _baseDir;
    private readonly string _bridgeCachePath;
    private readonly SemaphoreSlim _bridgeFetchLock = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<string>> _runtimeFetchedBridges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fetchAttempted = new(StringComparer.OrdinalIgnoreCase);
    private BridgeFetchCacheStore? _cacheStore;
    private bool _cacheLoaded;
    private readonly object _runtimeBridgeHealthLock = new();
    private readonly Dictionary<string, RuntimeBridgeHealthEntry> _runtimeBridgeHealth = new(StringComparer.OrdinalIgnoreCase);

    public TorBridgeManager(string baseDir)
    {
        _baseDir = baseDir;
        _bridgeCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OnionHop V3",
            "bridge-cache.json");
    }

    public string? BridgeValidationMessage { get; private set; }
    private static string WebTunnelClientFileName => PlatformHelper.WebTunnelClientBinaryName;
    private static string LyrebirdFileName => PlatformHelper.LyrebirdBinaryName;
    private static string SnowflakeClientFileName => PlatformHelper.SnowflakeClientBinaryName;

    public readonly record struct BridgeDataRefreshSummary(
        int AttemptedTypes,
        int UpdatedTypes,
        DateTimeOffset? LastUpdatedUtc);

    public async Task<BridgeDataRefreshSummary> RefreshBridgeDataAsync(
        IReadOnlyList<string> bridgeTypes,
        Action<string> log,
        CancellationToken token,
        HttpClient? httpClient = null)
    {
        if (bridgeTypes.Count == 0)
        {
            return new BridgeDataRefreshSummary(0, 0, GetLatestBridgeCacheUpdateUtc());
        }

        var targets = bridgeTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(NormalizeBridgeTypeKey)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Where(type => !string.Equals(type, AutomaticBridgeType, StringComparison.OrdinalIgnoreCase))
            .Where(type => !string.Equals(type, "custom", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            return new BridgeDataRefreshSummary(0, 0, GetLatestBridgeCacheUpdateUtc());
        }

        log($"Bridge data refresh: updating {targets.Count} bridge type(s): {string.Join(", ", targets)}.");

        var updated = 0;
        foreach (var bridgeType in targets)
        {
            token.ThrowIfCancellationRequested();

            // dnstt is a DNS tunnel, not a Tor pluggable transport - it isn't served by the Tor bridge
            // service or the collector, so an online refresh always comes back empty. Its bridges ship
            // built-in, so report those instead of a misleading "FAILED - no usable bridges".
            if (string.Equals(bridgeType, DnsttBridgeType, StringComparison.OrdinalIgnoreCase))
            {
                var builtIn = TryLoadBundledBridgeLines(bridgeType, static _ => { });
                if (builtIn.Count == 0)
                {
                    builtIn = TryLoadOfflineBridgeLines(bridgeType, static _ => { });
                }

                updated++;
                log(builtIn.Count > 0
                    ? $"Bridge data refresh: {bridgeType} uses {builtIn.Count} built-in bridge(s) (not online-refreshable)."
                    : $"Bridge data refresh: {bridgeType} uses built-in bridges (not online-refreshable).");
                continue;
            }

            var lines = await TryFetchBridgeLinesAsync(
                bridgeType,
                log,
                token,
                forceRefresh: true,
                httpClientOverride: httpClient).ConfigureAwait(false);
            // Explicit per-type result so the user can see at a glance which sources succeeded.
            // (The fetch above already logs WHERE each set came from - collector / bridge service.)
            if (lines.Count > 0)
            {
                updated++;
                log($"Bridge data refresh: {bridgeType} OK - {lines.Count} bridge line(s).");
            }
            else
            {
                log($"Bridge data refresh: {bridgeType} FAILED - no usable bridges from any source.");
            }
        }

        return new BridgeDataRefreshSummary(targets.Count, updated, GetLatestBridgeCacheUpdateUtc(targets));
    }

    public DateTimeOffset? GetLatestBridgeCacheUpdateUtc(IReadOnlyList<string>? bridgeTypes = null)
    {
        EnsureBridgeCacheLoaded();
        if (_cacheStore?.Items == null || _cacheStore.Items.Count == 0)
        {
            return null;
        }

        DateTimeOffset latest = DateTimeOffset.MinValue;

        if (bridgeTypes == null || bridgeTypes.Count == 0)
        {
            foreach (var entry in _cacheStore.Items.Values)
            {
                if (entry.Lines is not { Count: > 0 })
                {
                    continue;
                }

                if (entry.UpdatedUtc > latest)
                {
                    latest = entry.UpdatedUtc;
                }
            }
        }
        else
        {
            foreach (var bridgeType in bridgeTypes)
            {
                var updatedUtc = GetCachedBridgeUpdatedUtc(bridgeType);
                if (!updatedUtc.HasValue)
                {
                    continue;
                }

                if (updatedUtc.Value > latest)
                {
                    latest = updatedUtc.Value;
                }
            }
        }

        return latest == DateTimeOffset.MinValue ? null : latest;
    }

    public static bool IsPlaceholderBridgeLine(string line)
    {
        return IsExplicitPlaceholderBridgeLine(line);
    }

    public static bool IsAutomaticBridgeType(string? bridgeType)
    {
        return string.Equals(bridgeType, AutomaticBridgeType, StringComparison.OrdinalIgnoreCase);
    }

    // Transports that reach Tor through a broker / domain fronting and have no fixed bridge IP:port
    // to TCP-probe (their bridge lines carry placeholder addresses). Reachability pre-probing can't
    // measure these, so Smart Connect leaves them in their default ladder position.
    private static readonly HashSet<string> NonProbeableTransports = new(StringComparer.OrdinalIgnoreCase)
    {
        "snowflake", "meek", "meek_lite", "meek-azure", "conjure", "dnstt"
    };

    /// <summary>
    /// True when a bridge transport connects to a fixed IP:port that can be TCP-probed for
    /// reachability (obfs4/webtunnel/vanilla), false for fronted/brokered transports.
    /// </summary>
    public static bool BridgeTypeHasProbeableEndpoint(string? bridgeType)
    {
        if (string.IsNullOrWhiteSpace(bridgeType))
        {
            return false;
        }

        return !NonProbeableTransports.Contains(bridgeType.Trim());
    }

    public void ReportRuntimeBridgeFailure(string bridgeType, string endpoint, Action<string>? log = null)
    {
        var safeBridgeType = NormalizeBridgeTypeKey(bridgeType);
        if (safeBridgeType.Length == 0 || string.Equals(safeBridgeType, AutomaticBridgeType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalizedEndpoint = TryNormalizeBridgeEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(normalizedEndpoint))
        {
            return;
        }

        var key = BuildRuntimeBridgeHealthKey(safeBridgeType, normalizedEndpoint);
        var now = DateTimeOffset.UtcNow;
        var newlyBlocked = false;
        DateTimeOffset blockedUntilUtc = default;

        lock (_runtimeBridgeHealthLock)
        {
            if (!_runtimeBridgeHealth.TryGetValue(key, out var entry))
            {
                entry = new RuntimeBridgeHealthEntry();
                _runtimeBridgeHealth[key] = entry;
            }

            if (entry.LastFailureUtc == default || now - entry.LastFailureUtc > RuntimeBridgeFailureWindow)
            {
                entry.FailureCount = 0;
            }

            entry.FailureCount++;
            entry.LastFailureUtc = now;

            if (entry.FailureCount >= RuntimeBridgeFailureThreshold)
            {
                var candidateUntil = now.Add(RuntimeBridgePenaltyDuration);
                if (candidateUntil > entry.BlockedUntilUtc)
                {
                    entry.BlockedUntilUtc = candidateUntil;
                    newlyBlocked = true;
                }
            }

            blockedUntilUtc = entry.BlockedUntilUtc;
        }

        if (newlyBlocked)
        {
            log?.Invoke($"Temporarily suppressing unstable {safeBridgeType} bridge endpoint {normalizedEndpoint} until {blockedUntilUtc:HH:mm:ss} UTC.");
        }
    }

    public static IReadOnlyList<string> BuildAutomaticBridgeFallbackOrder(OnionHopConnectOptions options)
    {
        var ordered = new List<string>(AutomaticBridgeFallbackChain);

        if (options.UseSnowflakeAmp)
        {
            ordered.Remove(SnowflakeBridgeType);
            ordered.Insert(0, SnowflakeBridgeType);
        }

        if (!string.IsNullOrWhiteSpace(options.CustomSniHosts))
        {
            ordered.Remove(WebTunnelBridgeType);
            ordered.Insert(0, WebTunnelBridgeType);
        }

        return ordered
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static BridgeSourcePreference ResolveBridgeSourcePreference(string? mode)
    {
        if (string.Equals(mode, OnionHopConnectOptions.BridgeSourceOnlineOnly, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "Bridge" + "DB only", StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourcePreference.OnlineOnly;
        }

        if (string.Equals(mode, OnionHopConnectOptions.BridgeSourceOfflineOnly, StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourcePreference.OfflineOnly;
        }

        if (string.Equals(mode, OnionHopConnectOptions.BridgeSourceCollectorOnly, StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourcePreference.CollectorOnly;
        }

        return BridgeSourcePreference.Auto;
    }

    public static IReadOnlyList<string> GetBridgeTypeKeys(PluggableTransportConfig? config)
    {
        var bridgeKeys = config?.Bridges?.Keys?
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        if (bridgeKeys.Count == 0)
        {
            bridgeKeys.AddRange([VanillaBridgeType, Obfs4BridgeType, SnowflakeBridgeType, ConjureBridgeType, "meek-azure"]);
        }

        if (!bridgeKeys.Any(key => string.Equals(key, AutomaticBridgeType, StringComparison.OrdinalIgnoreCase)))
        {
            bridgeKeys.Insert(0, AutomaticBridgeType);
        }

        if (!bridgeKeys.Any(key => string.Equals(key, VanillaBridgeType, StringComparison.OrdinalIgnoreCase)))
        {
            bridgeKeys.Insert(Math.Min(1, bridgeKeys.Count), VanillaBridgeType);
        }

        if (!bridgeKeys.Any(key => string.Equals(key, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase)))
        {
            bridgeKeys.Add(WebTunnelBridgeType);
        }

        if (!bridgeKeys.Any(key => string.Equals(key, ConjureBridgeType, StringComparison.OrdinalIgnoreCase))
            && config?.PluggableTransports?.ContainsKey(ConjureBridgeType) == true)
        {
            bridgeKeys.Add(ConjureBridgeType);
        }

        if (!bridgeKeys.Any(key => string.Equals(key, "custom", StringComparison.OrdinalIgnoreCase)))
        {
            bridgeKeys.Add("custom");
        }

        var preferredOrder = new[] { AutomaticBridgeType, VanillaBridgeType, WebTunnelBridgeType, SnowflakeBridgeType, Obfs4BridgeType, ConjureBridgeType, "meek-azure", "meek" };
        var result = new List<string>();

        foreach (var key in preferredOrder)
        {
            if (bridgeKeys.Any(value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(key);
            }
        }

        var remaining = bridgeKeys
            .Where(value => !string.Equals(value, "custom", StringComparison.OrdinalIgnoreCase))
            .Where(value => !result.Any(added => string.Equals(added, value, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        result.AddRange(remaining);

        if (bridgeKeys.Any(value => string.Equals(value, "custom", StringComparison.OrdinalIgnoreCase)))
        {
            result.RemoveAll(value => string.Equals(value, "custom", StringComparison.OrdinalIgnoreCase));
            result.Add("custom");
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetBridgeLinesAsync(
        OnionHopConnectOptions options,
        PluggableTransportConfig? config,
        Action<string> log,
        CancellationToken token)
    {
        BridgeValidationMessage = null;
        var sourcePreference = ResolveBridgeSourcePreference(options.BridgeSourceMode);
        var collectorOnly = sourcePreference == BridgeSourcePreference.CollectorOnly;
        var allowBridgeServiceFetch = sourcePreference != BridgeSourcePreference.OfflineOnly;
        // CollectorOnly fetches online (from the collector) but, like OnlineOnly, should not silently
        // drop back to offline lists - if the collector has nothing, surface that instead.
        var allowOfflineFallback = sourcePreference != BridgeSourcePreference.OnlineOnly
                                   && sourcePreference != BridgeSourcePreference.CollectorOnly;
        var selectedBridgeType = ResolveSelectedBridgeType(options, config, log);
        IReadOnlyList<string> selected = Array.Empty<string>();
        var usingCustom = false;

        var custom = ExtractBridgeLines(options.CustomBridges);
        if (custom.Count > 0)
        {
            var filteredCustom = custom
                .Where(line => !IsExplicitPlaceholderBridgeLine(line))
                .ToList();
            if (filteredCustom.Count != custom.Count)
            {
                log($"Ignored {custom.Count - filteredCustom.Count} custom bridge line(s) because they look like placeholder examples.");
            }

            if (filteredCustom.Count == 0 && custom.Count > 0)
            {
                BridgeValidationMessage = "All custom bridge lines were filtered because they look like placeholder examples.";
            }

            custom = filteredCustom;
        }

        if (custom.Count > 0)
        {
            selected = custom;
            usingCustom = true;
        }
        else
        {
            if (allowBridgeServiceFetch)
            {
                // On the first connect of a session for a collector-backed transport (obfs4/webtunnel/
                // vanilla), force a fresh fetch so we pull the collector's pre-tested "Tested & Active"
                // set instead of trusting a possibly-stale on-disk cache that may hold the small,
                // overloaded Moat builtin set. TryFetchBridgeLinesAsync now tries the collector before
                // Moat, so a forced fetch yields the validated bridges. Within the same session the
                // runtime cache short-circuits, so this happens at most once per transport per run.
                var preferFreshTested = sourcePreference == BridgeSourcePreference.Auto
                    && IsCollectorSupportedType(selectedBridgeType)
                    && !_runtimeFetchedBridges.ContainsKey(selectedBridgeType);
                var fetched = await TryFetchBridgeLinesAsync(
                    selectedBridgeType, log, token, forceRefresh: preferFreshTested, collectorOnly: collectorOnly).ConfigureAwait(false);
                if (fetched.Count > 0)
                {
                    selected = fetched;
                }
            }

            // The Tor bridge service (and its on-disk cache) commonly returns only 2-3 bridges. For a
            // slow transport like webtunnel, if those few happen to be overloaded the bootstrap stalls
            // even though the transport itself works. When the live set is thin, top it up with offline
            // /bundled bridges of the same transport so Tor has more entry options to find a working
            // one. The live bridges are kept; offline ones are appended (shuffled, de-duplicated) up to
            // a healthy total. Does not run when the user supplied custom bridges or offline is disabled.
            if (selected.Count > 0 && selected.Count < MinHealthyBridgeCount && allowOfflineFallback)
            {
                var supplement = TryLoadOfflineBridgeLines(selectedBridgeType, log);
                if (supplement.Count == 0)
                {
                    supplement = TryLoadBundledBridgeLines(selectedBridgeType, log);
                }

                if (supplement.Count > 0)
                {
                    var extras = new List<string>(supplement);
                    if (extras.Count > 1)
                    {
                        ShuffleInPlace(extras);
                    }

                    var merged = new List<string>(selected);
                    var seen = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                    foreach (var line in extras)
                    {
                        if (merged.Count >= MaxSupplementedBridgeCount)
                        {
                            break;
                        }

                        if (seen.Add(line))
                        {
                            merged.Add(line);
                        }
                    }

                    if (merged.Count > selected.Count)
                    {
                        log($"Supplemented {selected.Count} live {selectedBridgeType} bridge(s) with {merged.Count - selected.Count} offline bridge(s) for a healthier set.");
                        selected = merged;
                    }
                }
            }

            if (selected.Count == 0 && allowOfflineFallback)
            {
                if (allowBridgeServiceFetch && sourcePreference == BridgeSourcePreference.Auto)
                {
                    log($"Tor bridge service fetch for {selectedBridgeType} was unavailable. Falling back to offline bridge lists.");
                }

                selected = TryLoadOfflineBridgeLines(selectedBridgeType, log);
            }

            if (selected.Count == 0 && allowOfflineFallback)
            {
                selected = TryLoadBundledBridgeLines(selectedBridgeType, log);
            }

            if (selected.Count == 0 &&
                allowOfflineFallback &&
                config?.Bridges != null &&
                config.Bridges.TryGetValue(selectedBridgeType, out var bridges) &&
                bridges.Count > 0)
            {
                var sanitizedBridges = bridges
                    .Where(line => !IsReservedOrPlaceholderBridgeLine(line))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sanitizedBridges.Count == 0)
                {
                    log($"Ignored bundled {selectedBridgeType} bridge lines from pt_config because they only contained placeholder/documentation addresses.");
                    BridgeValidationMessage ??= "Bundled bridge lines were filtered because they use placeholder/documentation addresses.";
                }
                else
                {
                    // When using bundled bridge entries, shuffle the order so we don't get stuck retrying
                    // the same dead/blocked bridge on every connect attempt.
                    // (Users can still paste Custom Bridges to control ordering.)
                    if (sanitizedBridges.Count > 1)
                    {
                        var shuffled = new List<string>(sanitizedBridges);
                        ShuffleInPlace(shuffled);
                        selected = shuffled;
                        log($"Shuffled bundled {selectedBridgeType} bridges.");
                    }
                    else
                    {
                        selected = sanitizedBridges;
                    }
                }
            }
        }

        if (selected.Count > 0)
        {
            // Users sometimes paste bridge lines missing the leading transport (e.g. "Bridge 1.2.3.4:443 ...").
            // Tor expects the first token to be the transport for PT bridges (e.g. "snowflake ...", "obfs4 ...").
            selected = NormalizeTransportPrefix(selected, selectedBridgeType, log);
        }

        // If custom bridges were provided but none are usable, fall back to bundled bridge entries.
        if (usingCustom && selected.Count == 0 && allowOfflineFallback && config?.Bridges != null &&
            config.Bridges.TryGetValue(selectedBridgeType, out var fallback) &&
            fallback.Count > 0)
        {
            usingCustom = false;
            selected = fallback
                .Where(line => !IsReservedOrPlaceholderBridgeLine(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (selected.Count == 0 && allowOfflineFallback)
        {
            selected = TryLoadOfflineBridgeLines(selectedBridgeType, log);
        }

        if (selected.Count == 0 && allowOfflineFallback)
        {
            selected = TryLoadBundledBridgeLines(selectedBridgeType, log);
        }

        if (!usingCustom && selected.Count > 1)
        {
            var randomized = new List<string>(selected);
            ShuffleInPlace(randomized);
            selected = randomized;
        }

        var customSni = ExtractSniHosts(options.CustomSniHosts);
        if (customSni.Count > 0 && selected.Count > 0)
        {
            selected = ApplyCustomSniHosts(selected, customSni);
        }

        var filteredToZeroByHealth = false;
        if (selected.Count > 0)
        {
            var beforeFilter = selected.Count;
            selected = FilterTemporarilyUnhealthyBridgeLines(selectedBridgeType, selected, log);
            filteredToZeroByHealth = beforeFilter > 0 && selected.Count == 0;
        }

        if (filteredToZeroByHealth && !usingCustom && allowOfflineFallback)
        {
            var healthyOffline = FilterTemporarilyUnhealthyBridgeLines(selectedBridgeType, TryLoadOfflineBridgeLines(selectedBridgeType, log), log);
            if (healthyOffline.Count == 0)
            {
                healthyOffline = FilterTemporarilyUnhealthyBridgeLines(selectedBridgeType, TryLoadBundledBridgeLines(selectedBridgeType, log), log);
            }

            if (healthyOffline.Count > 0)
            {
                selected = healthyOffline;
            }
        }

        if (selected.Count == 0 && string.IsNullOrWhiteSpace(BridgeValidationMessage))
        {
            BridgeValidationMessage = sourcePreference switch
            {
                BridgeSourcePreference.OnlineOnly => "No usable bridge lines were fetched from the Tor bridge service.",
                BridgeSourcePreference.OfflineOnly => "No usable offline bridge lines were found.",
                BridgeSourcePreference.CollectorOnly => IsCollectorSupportedType(selectedBridgeType)
                    ? "No usable bridge lines were fetched from the OnionHop collector."
                    : $"The OnionHop collector does not provide '{selectedBridgeType}' bridges. It covers obfs4, webtunnel, and vanilla. Pick one of those, or use a different bridge source.",
                _ => "No usable bridge lines were found (Tor bridge service and offline fallback both failed)."
            };
        }

        return selected;
    }

    private string ResolveSelectedBridgeType(OnionHopConnectOptions options, PluggableTransportConfig? config, Action<string> log)
    {
        var selected = options.SelectedBridgeType?.Trim();
        if (!IsAutomaticBridgeType(selected))
        {
            return string.IsNullOrWhiteSpace(selected) ? Obfs4BridgeType : selected;
        }

        var chain = BuildAutomaticBridgeFallbackOrder(options);
        if (config?.Bridges is { Count: > 0 })
        {
            var available = new HashSet<string>(config.Bridges.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in chain)
            {
                if (available.Contains(candidate))
                {
                    log($"Automatic bridges selected: {candidate} (single-attempt resolution).");
                    return candidate;
                }
            }
        }

        foreach (var candidate in chain)
        {
            if (TryLoadBundledBridgeLines(candidate, static _ => { }).Count > 0)
            {
                log($"Automatic bridges selected: {candidate} (bundled fallback).");
                return candidate;
            }
        }

        var fallback = chain.FirstOrDefault() ?? SnowflakeBridgeType;
        log($"Automatic bridges selected: {fallback} (single-attempt resolution).");
        return fallback;
    }

    private static void ShuffleInPlace(List<string> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static IReadOnlyList<string> BuildBridgeSourceUrls(string bridgeType)
    {
        var encoded = Uri.EscapeDataString(bridgeType);
        var urls = new List<string>
        {
            $"https://bridges.torproject.org/bridges?transport={encoded}&format=plain",
            $"https://bridges.torproject.org/bridges?transport={encoded}"
        };

        if (string.Equals(bridgeType, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
        {
            urls.Add($"https://bridges.torproject.org/bridges?transport={encoded}&ipv6=yes&format=plain");
            urls.Add($"https://bridges.torproject.org/bridges?transport={encoded}&ipv6=yes");
        }

        return urls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> TryFetchBridgeLinesAsync(
        string bridgeType,
        Action<string> log,
        CancellationToken token,
        bool forceRefresh = false,
        HttpClient? httpClientOverride = null,
        bool collectorOnly = false)
    {
        if (string.IsNullOrWhiteSpace(bridgeType) || string.Equals(bridgeType, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        if (!forceRefresh &&
            _runtimeFetchedBridges.TryGetValue(bridgeType, out var runtimeCached) &&
            runtimeCached.Count > 0)
        {
            return runtimeCached;
        }

        if (!forceRefresh)
        {
            var diskCached = GetCachedBridgeLines(bridgeType);
            if (diskCached.Count > 0)
            {
                _runtimeFetchedBridges[bridgeType] = diskCached;
                log($"Loaded {diskCached.Count} cached {bridgeType} bridge lines.");
                return diskCached;
            }

            if (_fetchAttempted.Contains(bridgeType))
            {
                return Array.Empty<string>();
            }
        }

        await _bridgeFetchLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (forceRefresh)
            {
                _runtimeFetchedBridges.Remove(bridgeType);
                _fetchAttempted.Remove(bridgeType);
            }
            else
            {
                if (_runtimeFetchedBridges.TryGetValue(bridgeType, out runtimeCached) && runtimeCached.Count > 0)
                {
                    return runtimeCached;
                }

                var diskCached = GetCachedBridgeLines(bridgeType);
                if (diskCached.Count > 0)
                {
                    _runtimeFetchedBridges[bridgeType] = diskCached;
                    log($"Loaded {diskCached.Count} cached {bridgeType} bridge lines.");
                    return diskCached;
                }

                if (_fetchAttempted.Contains(bridgeType))
                {
                    return Array.Empty<string>();
                }
            }

            _fetchAttempted.Add(bridgeType);

            var ownsHttpClient = httpClientOverride == null;
            var httpClient = httpClientOverride ?? new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHopV3/3.0");
            }

            try
            {
                // "OnionHop collector only" skips Moat and the Tor bridge service entirely and pulls
                // straight from the collector mirrors.
                if (collectorOnly)
                {
                    var onlyCollector = await TryFetchBridgeLinesFromCollectorAsync(httpClient, bridgeType, log, token).ConfigureAwait(false);
                    if (onlyCollector.Count > 0)
                    {
                        _runtimeFetchedBridges[bridgeType] = onlyCollector;
                        SaveCachedBridgeLines(bridgeType, onlyCollector);
                        log($"Loaded {onlyCollector.Count} {bridgeType} bridges from the OnionHop bridge collector.");
                        return onlyCollector;
                    }

                    log($"OnionHop collector returned no usable {bridgeType} bridges.");
                    return GetStaleCacheFallbackBridgeLines(bridgeType, log);
                }

                // Prefer the OnionHop collector's pre-tested set (Tested & Active, then Full Archive)
                // for the transports it covers, BEFORE Moat. Moat returns Tor's handful of built-in
                // bridges, which are heavily shared and often overloaded/unusable; the collector's set
                // has actually passed reachability testing, so a connect is far more likely to find a
                // usable guard. Moat and the Tor bridge service remain fallbacks below.
                if (IsCollectorSupportedType(bridgeType))
                {
                    var preferredCollector = await TryFetchBridgeLinesFromCollectorAsync(httpClient, bridgeType, log, token).ConfigureAwait(false);
                    if (preferredCollector.Count > 0)
                    {
                        _runtimeFetchedBridges[bridgeType] = preferredCollector;
                        SaveCachedBridgeLines(bridgeType, preferredCollector);
                        log($"Loaded {preferredCollector.Count} pre-tested {bridgeType} bridges from the OnionHop collector.");
                        return preferredCollector;
                    }
                }

                var moatLines = await TryFetchBridgeLinesViaMoatAsync(httpClient, bridgeType, token).ConfigureAwait(false);
                if (moatLines.Count > 0)
                {
                    _runtimeFetchedBridges[bridgeType] = moatLines;
                    SaveCachedBridgeLines(bridgeType, moatLines);
                    log($"Loaded {moatLines.Count} {bridgeType} bridges from Tor Moat (Ask Tor endpoint).");
                    return moatLines;
                }

                foreach (var sourceUrl in BuildBridgeSourceUrls(bridgeType))
                {
                    using var response = await httpClient.GetAsync(sourceUrl, token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        log($"Bridge auto-fetch for {bridgeType} failed at {sourceUrl}: HTTP {(int)response.StatusCode}.");
                        continue;
                    }

                    var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    var lines = SanitizeBridgeLines(bridgeType, ExtractBridgeLinesFromSource(content, bridgeType));

                    if (lines.Count == 0)
                    {
                        continue;
                    }

                    _runtimeFetchedBridges[bridgeType] = lines;
                    SaveCachedBridgeLines(bridgeType, lines);
                    log($"Loaded {lines.Count} {bridgeType} bridges from the Tor bridge service.");
                    return lines;
                }

                // (The OnionHop collector was already tried first, above, for the transports it covers.)
                log($"Bridge auto-fetch for {bridgeType} returned no usable lines (the Tor bridge service may require an interactive challenge).");
                return GetStaleCacheFallbackBridgeLines(bridgeType, log);
            }
            finally
            {
                if (ownsHttpClient)
                {
                    httpClient.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            log($"Bridge auto-fetch for {bridgeType} failed: {ex.Message}");
            return GetStaleCacheFallbackBridgeLines(bridgeType, log);
        }
        finally
        {
            _bridgeFetchLock.Release();
        }
    }

    private static bool IsCollectorSupportedType(string bridgeType) =>
        string.Equals(bridgeType, Obfs4BridgeType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(bridgeType, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(bridgeType, VanillaBridgeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fetch bridges from the OnionHop bridge collector (multi-mirror). Prefers the pre-tested
    /// lists, falling back to the full archive, and merges IPv4 + IPv6. The collector only covers
    /// obfs4/webtunnel/vanilla; other transports return empty. Lines are run through the same
    /// extraction/sanitization as the official source so formatting is identical.
    /// </summary>
    private async Task<IReadOnlyList<string>> TryFetchBridgeLinesFromCollectorAsync(
        HttpClient httpClient,
        string bridgeType,
        Action<string> log,
        CancellationToken token)
    {
        if (!IsCollectorSupportedType(bridgeType))
        {
            return Array.Empty<string>();
        }

        foreach (var category in new[] { "Tested & Active", "Full Archive" })
        {
            var gathered = new List<string>();
            foreach (var ipVersion in new[] { "IPv4", "IPv6" })
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var fetch = await BridgeSourceService
                        .FetchAsync(category, bridgeType, ipVersion, httpClient, log, token)
                        .ConfigureAwait(false);
                    if (fetch is { Lines.Count: > 0 })
                    {
                        gathered.AddRange(fetch.Lines);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log($"Collector fetch for {bridgeType} ({category}, {ipVersion}) failed: {ex.Message}");
                }
            }

            if (gathered.Count == 0)
            {
                continue;
            }

            var extracted = ExtractBridgeLinesFromSource(string.Join("\n", gathered), bridgeType);
            var sanitized = SanitizeBridgeLines(bridgeType, extracted);
            if (sanitized.Count > 0)
            {
                return sanitized;
            }
        }

        return Array.Empty<string>();
    }

    private async Task<IReadOnlyList<string>> TryFetchBridgeLinesViaMoatAsync(HttpClient httpClient, string bridgeType, CancellationToken token)
    {
        var moatBuiltin = await TryFetchBridgeLinesFromMoatBuiltinAsync(httpClient, bridgeType, token).ConfigureAwait(false);
        if (moatBuiltin.Count > 0)
        {
            return moatBuiltin;
        }

        var transports = BuildMoatTransportRequestList(bridgeType);
        var moatSettings = await TryFetchBridgeLinesFromMoatSettingsEndpointAsync(httpClient, MoatSettingsUrl, bridgeType, transports, token).ConfigureAwait(false);
        if (moatSettings.Count > 0)
        {
            return moatSettings;
        }

        return await TryFetchBridgeLinesFromMoatSettingsEndpointAsync(httpClient, MoatDefaultsUrl, bridgeType, transports, token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> TryFetchBridgeLinesFromMoatBuiltinAsync(
        HttpClient httpClient,
        string bridgeType,
        CancellationToken token)
    {
        try
        {
            using var response = await httpClient.GetAsync(MoatBuiltinUrl, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var lines = ExtractBridgeLinesFromMoatBuiltin(content, bridgeType);
            return SanitizeBridgeLines(bridgeType, lines);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<string>> TryFetchBridgeLinesFromMoatSettingsEndpointAsync(
        HttpClient httpClient,
        string endpointUrl,
        string bridgeType,
        IReadOnlyList<string> transports,
        CancellationToken token)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new MoatSettingsRequest
            {
                Transports = transports.ToList()
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/vnd.api+json");
            using var response = await httpClient.PostAsync(endpointUrl, content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var responseBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<MoatSettingsResponse>(responseBody);
            if (parsed?.Settings is not { Count: > 0 })
            {
                return Array.Empty<string>();
            }

            var lines = parsed.Settings
                .Where(setting => string.Equals(setting.Bridge?.Type, bridgeType, StringComparison.OrdinalIgnoreCase))
                .SelectMany(setting => setting.Bridge?.BridgeStrings ?? [])
                .ToList();
            return SanitizeBridgeLines(bridgeType, lines);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> BuildMoatTransportRequestList(string bridgeType)
    {
        var transports = new List<string>();

        static void AddUnique(List<string> values, string value)
        {
            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(value);
            }
        }

        AddUnique(transports, bridgeType);
        AddUnique(transports, Obfs4BridgeType);
        AddUnique(transports, SnowflakeBridgeType);
        AddUnique(transports, WebTunnelBridgeType);

        return transports;
    }

    private static IReadOnlyList<string> ExtractBridgeLinesFromMoatBuiltin(string jsonContent, string bridgeType)
    {
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(jsonContent);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, bridgeType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<string>();
                }

                var lines = new List<string>();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        lines.Add(value);
                    }
                }

                return lines;
            }
        }
        catch
        {
            // Moat parsing is best-effort.
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> SanitizeBridgeLines(string bridgeType, IEnumerable<string> lines)
    {
        return lines
            .Select(NormalizeBridgeLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !IsReservedOrPlaceholderBridgeLine(line))
            .Where(line => IsBridgeLineCompatibleWithType(bridgeType, line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractBridgeLinesFromSource(string content, string bridgeType)
    {
        if (string.Equals(bridgeType, VanillaBridgeType, StringComparison.OrdinalIgnoreCase))
        {
            return ExtractVanillaBridgeLines(content);
        }

        if (string.Equals(bridgeType, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
        {
            return ExtractWebTunnelBridgeLines(content);
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = bridgeType + " ";
        foreach (var raw in EnumerateBridgeSourceLines(content))
        {
            var line = NormalizeBridgeLine(raw);
            if (line.Length == 0)
            {
                continue;
            }

            var marker = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                continue;
            }

            line = line.Substring(marker).Trim();
            if (line.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring("Bridge ".Length).Trim();
            }

            line = TrimTrailingBridgeAlias(line);

            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Length > 0 && seen.Add(line))
            {
                results.Add(line);
            }
        }

        return results;
    }

    private static IReadOnlyList<string> ExtractVanillaBridgeLines(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in EnumerateBridgeSourceLines(content))
        {
            var line = NormalizeVanillaBridgeLine(raw);
            if (!IsValidVanillaBridgeLine(line))
            {
                continue;
            }

            if (seen.Add(line))
            {
                results.Add(line);
            }
        }

        return results;
    }

    private static IReadOnlyList<string> ExtractWebTunnelBridgeLines(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in WebTunnelBridgeRegex.Matches(content))
        {
            var line = NormalizeBridgeLine(match.Groups["line"].Success ? match.Groups["line"].Value : match.Value);
            line = TrimTrailingBridgeAlias(line);
            if (!IsValidWebTunnelBridgeLine(line))
            {
                continue;
            }

            if (seen.Add(line))
            {
                results.Add(line);
            }
        }

        if (results.Count > 0)
        {
            return results;
        }

        foreach (var raw in EnumerateBridgeSourceLines(content))
        {
            var line = NormalizeBridgeLine(raw);
            var marker = line.IndexOf("webtunnel ", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                continue;
            }

            line = line.Substring(marker).Trim();
            line = TrimTrailingBridgeAlias(line);
            if (!IsValidWebTunnelBridgeLine(line))
            {
                continue;
            }

            if (seen.Add(line))
            {
                results.Add(line);
            }
        }

        return results;
    }

    private static IEnumerable<string> EnumerateBridgeSourceLines(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        var normalized = WebUtility.HtmlDecode(content);
        normalized = Regex.Replace(normalized, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"</(p|div|li|h1|h2|h3|h4|h5|h6)>", "\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<[^>]+>", " ");

        foreach (var raw in normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw?.Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static string NormalizeBridgeLine(string line)
    {
        var normalized = WebUtility.HtmlDecode(line ?? string.Empty).Trim();
        if (normalized.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("Bridge ".Length).Trim();
        }

        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.TrimEnd(',', ';', '.');
    }

    private static string TrimTrailingBridgeAlias(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var trimmed = line.Trim();
        var marker = trimmed.LastIndexOf(" - ", StringComparison.Ordinal);
        if (marker <= 0 || marker + 3 >= trimmed.Length)
        {
            return trimmed;
        }

        var alias = trimmed[(marker + 3)..].Trim();
        if (alias.Length == 0)
        {
            return trimmed;
        }

        // Bridge aliases are commonly appended as " - Name" in community lists.
        // Keep key=value/endpoint-like suffixes untouched.
        if (alias.Contains('=') || alias.Contains(':'))
        {
            return trimmed;
        }

        var withoutAlias = trimmed[..marker].TrimEnd();
        return withoutAlias.Length == 0 ? trimmed : withoutAlias;
    }

    private static bool IsValidWebTunnelBridgeLine(string line)
    {
        if (!line.StartsWith("webtunnel ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        if (!parts[1].Contains(':'))
        {
            return false;
        }

        if (!BridgeFingerprintRegex.IsMatch(parts[2]))
        {
            return false;
        }

        if (!TryParseBridgeEndpointToken(parts[1], out var endpointHost, out var endpointPort) ||
            endpointPort is <= 0 or > 65535 ||
            !IPAddress.TryParse(endpointHost, out _))
        {
            return false;
        }

        return line.Contains(" url=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBridgeEndpointToken(string endpointToken, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpointToken))
        {
            return false;
        }

        var endpoint = endpointToken.Trim();
        if (endpoint.StartsWith("[", StringComparison.Ordinal))
        {
            var closeBracket = endpoint.IndexOf(']');
            if (closeBracket <= 1 || closeBracket + 2 > endpoint.Length || endpoint[closeBracket + 1] != ':')
            {
                return false;
            }

            host = endpoint.Substring(1, closeBracket - 1);
            return int.TryParse(endpoint.Substring(closeBracket + 2), out port);
        }

        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon + 1 >= endpoint.Length)
        {
            return false;
        }

        host = endpoint.Substring(0, colon);
        return int.TryParse(endpoint.Substring(colon + 1), out port);
    }

    public IReadOnlyList<string> GetClientTransportPlugins(
        OnionHopConnectOptions options,
        IReadOnlyList<string> bridgeLines,
        string torDir,
        PluggableTransportConfig? config,
        Action<string> log)
    {
        var ptPath = Path.Combine(torDir, "pluggable_transports");
        // Use absolute paths so pluggable transports are found regardless of
        // working directory (critical when relaunched as root for TUN mode).
        var ptRelativePath = ptPath;
        var ptRelativePathWithSlash = ptPath + Path.DirectorySeparatorChar;

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in bridgeLines)
        {
            var transport = ExtractBridgeTransport(line);
            if (!string.IsNullOrWhiteSpace(transport) &&
                !LooksLikeEndpoint(transport) &&
                !string.Equals(transport, VanillaBridgeType, StringComparison.OrdinalIgnoreCase))
            {
                needed.Add(transport);
            }
        }

        if (needed.Count == 0)
        {
            return Array.Empty<string>();
        }

        string? webTunnelPlugin = null;
        if (needed.Contains(WebTunnelBridgeType))
        {
            if (!TryEnsureWebTunnelClient(ptPath, log, out webTunnelPlugin))
            {
                return Array.Empty<string>();
            }
        }

        if (config?.PluggableTransports != null && config.PluggableTransports.Count > 0)
        {
            var transportMap = BuildTransportPluginMap(config.PluggableTransports, ptRelativePathWithSlash);
            var pluginLines = new List<string>();
            foreach (var transport in needed)
            {
                if (string.Equals(transport, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
                {
                    pluginLines.Add(webTunnelPlugin!);
                    continue;
                }

                if (transportMap.TryGetValue(transport, out var plugin))
                {
                    pluginLines.Add(ReplaceTransportSegment(plugin, transport));
                    continue;
                }

                pluginLines.Add($"ClientTransportPlugin {transport} exec {Path.Combine(ptRelativePath, LyrebirdFileName)}");
            }

            return ApplySnowflakeOptions(options, pluginLines, ptRelativePath, log)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return ApplySnowflakeOptions(options, BuildFallbackTransportPlugins(needed, ptRelativePath, webTunnelPlugin), ptRelativePath, log)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ApplySnowflakeOptions(
        OnionHopConnectOptions options,
        IReadOnlyList<string> pluginLines,
        string ptRelativePath,
        Action<string> log)
    {
        if (pluginLines.Count == 0)
        {
            return pluginLines;
        }

        var updated = new List<string>(pluginLines.Count);
        foreach (var line in pluginLines)
        {
            if (!IsSnowflakePluginLine(line))
            {
                updated.Add(line);
                continue;
            }

            var pluginLine = EnsureSnowflakeClientPlugin(line, ptRelativePath);
            pluginLine = ApplySnowflakeAmpCache(options, pluginLine, log);
            updated.Add(pluginLine);
        }

        return updated;
    }

    private static bool IsSnowflakePluginLine(string pluginLine)
    {
        if (string.IsNullOrWhiteSpace(pluginLine))
        {
            return false;
        }

        var trimmed = pluginLine.TrimStart();
        const string prefix = "ClientTransportPlugin ";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var after = trimmed.Substring(prefix.Length).TrimStart();
        return after.StartsWith("snowflake", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureSnowflakeClientPlugin(string pluginLine, string ptRelativePath)
    {
        // Lyrebird 0.8+ natively supports the snowflake transport — no separate
        // snowflake-client binary is needed.  Accept either lyrebird or snowflake-client.
        if (pluginLine.Contains(LyrebirdFileName, StringComparison.OrdinalIgnoreCase) ||
            pluginLine.Contains(SnowflakeClientFileName, StringComparison.OrdinalIgnoreCase))
        {
            return pluginLine;
        }

        // Fallback: use lyrebird for the snowflake transport.
        return $"ClientTransportPlugin snowflake exec {Path.Combine(ptRelativePath, LyrebirdFileName)}";
    }

    private static string ApplySnowflakeAmpCache(OnionHopConnectOptions options, string pluginLine, Action<string> log)
    {
        if (!options.UseSnowflakeAmp)
        {
            return pluginLine;
        }

        if (pluginLine.Contains("-ampcache", StringComparison.OrdinalIgnoreCase))
        {
            return pluginLine;
        }

        var cache = options.SnowflakeAmpCache;
        if (string.IsNullOrWhiteSpace(cache))
        {
            cache = "https://cdn.ampproject.org/";
        }
        cache = cache.Trim();

        if (!Uri.TryCreate(cache, UriKind.Absolute, out var uri))
        {
            log($"Invalid Snowflake AMP cache URL: {cache}");
            return pluginLine;
        }

        log($"Snowflake AMP cache enabled: {uri}");
        return pluginLine + $" -ampcache {uri}";
    }

    public static string NormalizeClientTransportPlugin(string pluginLine)
    {
        var trimmed = pluginLine.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        const string execToken = " exec ";
        var execIndex = trimmed.IndexOf(execToken, StringComparison.OrdinalIgnoreCase);
        if (execIndex < 0)
        {
            return trimmed;
        }

        var prefix = trimmed[..(execIndex + execToken.Length)];
        var suffix = trimmed[(execIndex + execToken.Length)..].TrimStart();
        if (suffix.Length == 0 || suffix[0] == '"')
        {
            return trimmed;
        }

        var tokens = suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1)
        {
            return trimmed;
        }

        var executableTokenCount = 1;
        while (executableTokenCount < tokens.Length &&
               !tokens[executableTokenCount].StartsWith("-", StringComparison.Ordinal))
        {
            executableTokenCount++;
        }

        if (executableTokenCount <= 1)
        {
            return trimmed;
        }

        var executablePath = string.Join(" ", tokens.Take(executableTokenCount));
        if (!LooksLikeTransportExecutablePath(executablePath))
        {
            return trimmed;
        }

        // Tor's ClientTransportPlugin parser splits the line on whitespace and does NOT honor
        // quotes around the exec path. A path with a space (e.g. a Windows user folder like
        // "C:\Users\First Last\...") therefore gets truncated at the first space and the managed
        // proxy fails to launch ("there is no configured transport called ..."). On Windows,
        // collapse the path to its space-free 8.3 short form so the unquoted token survives the
        // splitter. Quoting is only a last resort when the space cannot be removed (e.g. 8.3 short
        // names disabled on the volume, or a non-Windows path).
        var safeExecutablePath = MakeExecutablePathTokenSafe(executablePath);
        var stillHasSpace = safeExecutablePath.IndexOf(' ') >= 0;

        var builder = new StringBuilder(prefix.Length + suffix.Length + 4);
        builder.Append(prefix);
        if (stillHasSpace)
        {
            builder.Append('"');
            builder.Append(safeExecutablePath.Replace("\"", "\\\"", StringComparison.Ordinal));
            builder.Append('"');
        }
        else
        {
            builder.Append(safeExecutablePath);
        }

        if (executableTokenCount < tokens.Length)
        {
            builder.Append(' ');
            builder.Append(string.Join(" ", tokens.Skip(executableTokenCount)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Makes a pluggable-transport executable path safe to pass on a single, unquoted Tor
    /// <c>ClientTransportPlugin ... exec &lt;path&gt;</c> token. Tor splits that line on whitespace
    /// and ignores quotes, so a path containing a space is truncated at the first space and the
    /// managed proxy fails to launch. On Windows the path is collapsed to its space-free 8.3 short
    /// form; on macOS/Linux (e.g. the "~/Library/Application Support/..." data dir, which has a
    /// space) we materialize a space-free symlink to the real binary in the temp dir and return
    /// that. Returns the original path unchanged when it has no space or no space-free form can be
    /// produced (the caller then quotes it as a last resort).
    /// </summary>
    internal static string MakeExecutablePathTokenSafe(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath) || executablePath.IndexOf(' ') < 0)
        {
            return executablePath;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var shortPath = GetWindowsShortPath(executablePath);
                if (!string.IsNullOrEmpty(shortPath) && shortPath!.IndexOf(' ') < 0)
                {
                    return shortPath!;
                }

                // 8.3 short-name creation is disabled on many modern Windows volumes (fsutil
                // 8dot3name), so GetShortPathName returns the long, spaced path. Tor's transport
                // parser then truncates the exec token at the first space and the managed proxy never
                // launches ("CreateProcessA() failed: The system cannot find the file specified"),
                // breaking every bridge for anyone whose folder has a space (e.g. "C:\Users\First Last").
                // Mirror the binary into a guaranteed space-free directory and hand Tor that path.
                var copied = TryCreateWindowsSpaceFreeCopy(executablePath);
                if (!string.IsNullOrEmpty(copied) && copied!.IndexOf(' ') < 0)
                {
                    return copied!;
                }
            }
            else
            {
                // No 8.3 equivalent on macOS/Linux. Tor needs an absolute, space-free path it can
                // exec regardless of working directory, so point a symlink in the (space-free) temp
                // dir at the real binary and hand Tor the link.
                var linked = TryCreateSpaceFreeSymlink(executablePath);
                if (!string.IsNullOrEmpty(linked) && linked!.IndexOf(' ') < 0)
                {
                    return linked!;
                }
            }
        }
        catch
        {
            // Fall through and return the original path; the caller quotes it as a last resort.
        }

        return executablePath;
    }

    /// <summary>
    /// Creates (or refreshes) a space-free symlink to <paramref name="targetPath"/> under the temp
    /// directory and returns the link path, or null if the temp dir itself has a space or the
    /// target is missing. Used so Tor can exec a pluggable transport whose real path contains a
    /// space (Tor's transport-line parser splits on whitespace and ignores quotes).
    /// </summary>
    private static string? TryCreateSpaceFreeSymlink(string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return null;
        }

        var tempRoot = Path.GetTempPath();
        if (string.IsNullOrEmpty(tempRoot) || tempRoot.IndexOf(' ') >= 0)
        {
            return null;
        }

        var linkDir = Path.Combine(tempRoot, "onionhop-pt");
        Directory.CreateDirectory(linkDir);

        var linkPath = Path.Combine(linkDir, Path.GetFileName(targetPath));
        if (linkPath.IndexOf(' ') >= 0)
        {
            return null;
        }

        // Recreate the link if it is missing or points somewhere else (e.g. after an update moved
        // the real binary). File.Delete is a no-op when the path does not exist and removes the
        // link itself (not the target) when it does.
        var current = TryReadLinkTarget(linkPath);
        if (!string.Equals(current, targetPath, StringComparison.Ordinal))
        {
            try { File.Delete(linkPath); } catch { /* best effort */ }
            File.CreateSymbolicLink(linkPath, targetPath);
        }

        return linkPath;
    }

    private static string? TryReadLinkTarget(string linkPath)
    {
        try
        {
            return File.ResolveLinkTarget(linkPath, returnFinalTarget: false)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Windows fallback when 8.3 short names are unavailable: copy the pluggable-transport binary into
    /// a directory that is guaranteed to be space-free and writable without admin (e.g.
    /// <c>C:\Users\Public\OnionHop\pt</c>) and return that path, so Tor's whitespace-splitting transport
    /// parser can exec it. The transports are single static Go binaries, so a plain copy is sufficient;
    /// re-copies only when size or timestamp changes (i.e. after an app update). Returns null if no
    /// space-free base directory exists or the copy fails.
    /// </summary>
    private static string? TryCreateWindowsSpaceFreeCopy(string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return null;
        }

        string? baseDir = null;
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("PUBLIC"),
                     Environment.GetEnvironmentVariable("ProgramData"),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                 })
        {
            if (!string.IsNullOrEmpty(candidate) && candidate!.IndexOf(' ') < 0 && Directory.Exists(candidate))
            {
                baseDir = candidate;
                break;
            }
        }

        if (string.IsNullOrEmpty(baseDir))
        {
            return null;
        }

        var dir = Path.Combine(baseDir!, "OnionHop", "pt");
        if (dir.IndexOf(' ') >= 0)
        {
            return null;
        }

        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(targetPath));
        if (dest.IndexOf(' ') >= 0)
        {
            return null;
        }

        var src = new FileInfo(targetPath);
        var dst = new FileInfo(dest);
        if (!dst.Exists || dst.Length != src.Length || dst.LastWriteTimeUtc != src.LastWriteTimeUtc)
        {
            File.Copy(targetPath, dest, overwrite: true);
            try { File.SetLastWriteTimeUtc(dest, src.LastWriteTimeUtc); } catch { /* best effort */ }
        }

        return dest;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsShortPath(string longPath)
    {
        // GetShortPathNameW requires the full path (including the final component) to exist.
        var buffer = new StringBuilder(longPath.Length + 16);
        var length = GetShortPathNameW(longPath, buffer, (uint)buffer.Capacity);
        if (length == 0)
        {
            return null;
        }

        if (length > buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length);
            length = GetShortPathNameW(longPath, buffer, (uint)buffer.Capacity);
            if (length == 0)
            {
                return null;
            }
        }

        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    private static bool LooksLikeTransportExecutablePath(string value)
    {
        return value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar)
            || value.Contains(":\\", StringComparison.Ordinal)
            || value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildFallbackTransportPlugins(IReadOnlyCollection<string> transports, string ptRelativePath, string? webTunnelPlugin)
    {
        var plugins = new List<string>();
        foreach (var transport in transports)
        {
            if (string.Equals(transport, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(webTunnelPlugin))
                {
                    plugins.Add(webTunnelPlugin);
                }
                continue;
            }

            if (string.Equals(transport, "snowflake", StringComparison.OrdinalIgnoreCase))
            {
                plugins.Add($"ClientTransportPlugin snowflake exec {Path.Combine(ptRelativePath, LyrebirdFileName)}");
                continue;
            }

            plugins.Add($"ClientTransportPlugin {transport} exec {Path.Combine(ptRelativePath, LyrebirdFileName)}");
        }

        return plugins.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ExtractBridgeTransport(string bridgeLine)
    {
        if (string.IsNullOrWhiteSpace(bridgeLine))
        {
            return null;
        }

        var trimmed = bridgeLine.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return null;
        }

        return trimmed.Substring(0, firstSpace);
    }

    private static IReadOnlyList<string> NormalizeTransportPrefix(IReadOnlyList<string> lines, string selectedBridgeType, Action<string> log)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(selectedBridgeType))
        {
            return lines;
        }

        var normalizedType = selectedBridgeType.Trim();
        if (string.Equals(normalizedType, VanillaBridgeType, StringComparison.OrdinalIgnoreCase))
        {
            return lines
                .Select(NormalizeVanillaBridgeLine)
                .Where(IsValidVanillaBridgeLine)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var updated = false;
        var result = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var firstToken = ExtractBridgeTransport(trimmed);
            if (string.IsNullOrWhiteSpace(firstToken))
            {
                // No space in the line. Only prefix if it looks like an endpoint; otherwise ignore it as invalid input.
                if (LooksLikeEndpoint(trimmed))
                {
                    result.Add($"{normalizedType} {trimmed}");
                    updated = true;
                }
                else
                {
                    log($"Ignoring invalid bridge line: {trimmed}");
                }
                continue;
            }

            // If the first token looks like an endpoint (IP:port/host:port), the line is missing the transport.
            if (LooksLikeEndpoint(firstToken))
            {
                result.Add($"{normalizedType} {trimmed}");
                updated = true;
                continue;
            }

            result.Add(trimmed);
        }

        if (updated)
        {
            log($"Normalized bridge lines by prefixing missing transport: {normalizedType}");
        }

        return result;
    }

    private static bool LooksLikeEndpoint(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // Typical bridge endpoint forms:
        // - 1.2.3.4:443
        // - example.com:443
        // - [2001:db8::1]:443 (rare in bridges, but possible)
        if (!token.Contains(':'))
        {
            return false;
        }

        // Require at least one digit to avoid preferring random words with a colon.
        return token.Any(char.IsDigit);
    }

    private static Dictionary<string, string> BuildTransportPluginMap(Dictionary<string, string> pluginLines, string ptPathWithSlash)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pluginLines)
        {
            var normalized = entry.Value.Replace("${pt_path}", ptPathWithSlash, StringComparison.OrdinalIgnoreCase);
            var line = normalized.Trim();
            if (!line.StartsWith("ClientTransportPlugin ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            var transports = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var transport in transports)
            {
                map[transport] = line;
            }
        }

        return map;
    }

    private static string ReplaceTransportSegment(string pluginLine, string transport)
    {
        // Replace the transport list after ClientTransportPlugin with the single one Tor wants.
        var prefix = "ClientTransportPlugin ";
        var index = pluginLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return pluginLine;
        }

        var afterPrefix = pluginLine.Substring(index + prefix.Length);
        var space = afterPrefix.IndexOf(' ');
        if (space < 0)
        {
            return pluginLine;
        }

        return prefix + transport + afterPrefix.Substring(space);
    }

    private bool TryEnsureWebTunnelClient(string ptPath, Action<string> log, out string? pluginLine)
    {
        pluginLine = null;
        BridgeValidationMessage = null;

        var clientPath = Path.Combine(ptPath, WebTunnelClientFileName);
        if (!File.Exists(clientPath))
        {
            var source = FindWebTunnelClientSource();
            if (source != null)
            {
                try
                {
                    Directory.CreateDirectory(ptPath);
                    File.Copy(source.Path, clientPath, true);
                    log($"Copied {WebTunnelClientFileName} from {source.Label}.");
                }
                catch (Exception ex)
                {
                    BridgeValidationMessage = $"Failed to copy {WebTunnelClientFileName}: {ex.Message}";
                    return false;
                }
            }
        }

        if (!File.Exists(clientPath))
        {
            BridgeValidationMessage = $"Webtunnel client is missing ({WebTunnelClientFileName}). Reinstall OnionHop or install Tor Browser and copy it into tor/pluggable_transports.";
            return false;
        }

        pluginLine = $"ClientTransportPlugin webtunnel exec {Path.Combine(ptPath, WebTunnelClientFileName)}";
        return true;
    }

    private static CopySource? FindWebTunnelClientSource()
    {
        foreach (var candidate in BuildBundledPtCandidates())
        {
            if (File.Exists(candidate))
            {
                return new CopySource(candidate, "the OnionHop bundle");
            }
        }

        foreach (var candidate in BuildTorBrowserPtCandidates())
        {
            if (File.Exists(candidate))
            {
                return new CopySource(candidate, "Tor Browser");
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildBundledPtCandidates()
    {
        var candidates = new List<string>();
        void AddCandidate(string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            candidates.Add(Path.Combine(root, "tor", "pluggable_transports", WebTunnelClientFileName));
            candidates.Add(Path.Combine(root, "pluggable_transports", WebTunnelClientFileName));
        }

        AddCandidate(AppContext.BaseDirectory);

        var exePath = Environment.ProcessPath;
        var exeDir = exePath != null ? Path.GetDirectoryName(exePath) : null;
        if (!string.IsNullOrWhiteSpace(exeDir) &&
            !string.Equals(exeDir, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(exeDir);
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildTorBrowserPtCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName)
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                Path.Combine("/Applications", "Tor Browser.app", "Contents", "Resources", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Tor Browser.app", "Contents", "Resources", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName)
            ];
        }

        return
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tor-browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
            Path.Combine("/usr", "share", "torbrowser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName)
        ];
    }

    private sealed record CopySource(string Path, string Label);

    private static List<string> ExtractBridgeLines(string? text)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = NormalizeBridgeLine(line);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(line);
        }

        return results;
    }

    private static List<string> ExtractSniHosts(string? text)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

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

            foreach (var raw in line.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var host = raw.Trim();
                if (IsValidSniHost(ref host))
                {
                    results.Add(host);
                }
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsValidSniHost(ref string host)
    {
        host = host.Trim();
        if (host.Length == 0)
        {
            return false;
        }

        // Allow users to paste URLs.
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
        }

        host = host.Trim().TrimEnd('.');

        // Strip a single :port suffix if present (and not an IPv6 literal).
        var firstColon = host.IndexOf(':');
        var lastColon = host.LastIndexOf(':');
        if (firstColon > 0 && firstColon == lastColon)
        {
            host = host.Substring(0, firstColon);
        }

        return host.Length > 0;
    }

    private static IReadOnlyList<string> ApplyCustomSniHosts(IReadOnlyList<string> bridgeLines, IReadOnlyList<string> sniHosts)
    {
        if (bridgeLines.Count == 0 || sniHosts.Count == 0)
        {
            return bridgeLines;
        }

        var frontsValue = string.Join(",", sniHosts);
        var updated = new List<string>(bridgeLines.Count);
        foreach (var line in bridgeLines)
        {
            var chosen = sniHosts[Random.Shared.Next(sniHosts.Count)];
            var modified = BridgeFrontsRegex.Replace(line, $"fronts={frontsValue}");
            modified = BridgeFrontRegex.Replace(modified, $"front={chosen}");
            modified = BridgeSniRegex.Replace(modified, $"sni={chosen}");
            updated.Add(modified);
        }

        return updated;
    }

    private IReadOnlyList<string> FilterTemporarilyUnhealthyBridgeLines(string bridgeType, IReadOnlyList<string> lines, Action<string> log)
    {
        if (lines.Count == 0)
        {
            return lines;
        }

        var safeBridgeType = NormalizeBridgeTypeKey(bridgeType);
        if (safeBridgeType.Length == 0 || string.Equals(safeBridgeType, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return lines;
        }

        var now = DateTimeOffset.UtcNow;
        var filtered = new List<string>(lines.Count);
        var skipped = 0;

        foreach (var line in lines)
        {
            var endpoint = TryExtractBridgeEndpointFromLine(line);
            if (!string.IsNullOrWhiteSpace(endpoint) && IsBridgeEndpointTemporarilyBlocked(safeBridgeType, endpoint, now))
            {
                skipped++;
                continue;
            }

            filtered.Add(line);
        }

        if (skipped == 0)
        {
            return lines;
        }

        log($"Skipped {skipped} recently unstable {safeBridgeType} bridge endpoint(s).");
        if (filtered.Count == 0)
        {
            BridgeValidationMessage ??= $"All discovered {safeBridgeType} bridge endpoints are temporarily marked unstable. Try again later or switch bridge type.";
        }

        return filtered;
    }

    private bool IsBridgeEndpointTemporarilyBlocked(string bridgeType, string normalizedEndpoint, DateTimeOffset now)
    {
        var key = BuildRuntimeBridgeHealthKey(bridgeType, normalizedEndpoint);

        lock (_runtimeBridgeHealthLock)
        {
            if (!_runtimeBridgeHealth.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (entry.BlockedUntilUtc > now)
            {
                return true;
            }

            if (entry.LastFailureUtc != default && now - entry.LastFailureUtc > RuntimeBridgeFailureWindow)
            {
                _runtimeBridgeHealth.Remove(key);
            }
            else if (entry.FailureCount >= RuntimeBridgeFailureThreshold)
            {
                entry.FailureCount = RuntimeBridgeFailureThreshold - 1;
            }

            return false;
        }
    }

    private static string BuildRuntimeBridgeHealthKey(string bridgeType, string normalizedEndpoint)
    {
        return $"{NormalizeBridgeTypeKey(bridgeType)}|{normalizedEndpoint}";
    }

    private static string NormalizeBridgeTypeKey(string? bridgeType)
    {
        return string.IsNullOrWhiteSpace(bridgeType) ? string.Empty : bridgeType.Trim().ToLowerInvariant();
    }

    private static string? TryExtractBridgeEndpointFromLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var normalized = NormalizeBridgeLine(line);
        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var endpointIndex = LooksLikeEndpoint(parts[0]) ? 0 : 1;
        if (parts.Length <= endpointIndex)
        {
            return null;
        }

        return TryNormalizeBridgeEndpoint(parts[endpointIndex]);
    }

    private static string? TryNormalizeBridgeEndpoint(string endpointToken)
    {
        if (!TryParseBridgeEndpointToken(endpointToken, out var host, out var port) ||
            port is <= 0 or > 65535)
        {
            return null;
        }

        host = host.Trim().Trim('[', ']').ToLowerInvariant();
        if (host.Length == 0)
        {
            return null;
        }

        return $"{host}:{port}";
    }

    private IReadOnlyList<string> TryLoadOfflineBridgeLines(string bridgeType, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(bridgeType))
        {
            return Array.Empty<string>();
        }

        foreach (var candidate in GetOfflineBridgeFileCandidates(bridgeType))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(candidate);
                var lines = ExtractBridgeLinesFromSource(content, bridgeType)
                    .Where(line => !IsReservedOrPlaceholderBridgeLine(line))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (lines.Count == 0)
                {
                    continue;
                }

                log($"Loaded {lines.Count} offline {bridgeType} bridge lines.");
                return lines;
            }
            catch
            {
                // Offline bridge file loading is best-effort.
            }
        }

        return Array.Empty<string>();
    }

    private IReadOnlyList<string> TryLoadBundledBridgeLines(string bridgeType, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(bridgeType))
        {
            return Array.Empty<string>();
        }

        foreach (var candidate in GetBundledBridgeFileCandidates(bridgeType))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(candidate);
                var lines = ExtractBridgeLinesFromSource(content, bridgeType)
                    .Where(line => !IsReservedOrPlaceholderBridgeLine(line))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (lines.Count == 0)
                {
                    continue;
                }

                log($"Loaded {lines.Count} bundled {bridgeType} bridge lines.");
                return lines;
            }
            catch
            {
                // Bundled fallback is best-effort.
            }
        }

        return Array.Empty<string>();
    }

    private IEnumerable<string> GetOfflineBridgeFileCandidates(string bridgeType)
    {
        var safeBridgeType = bridgeType.Trim().ToLowerInvariant();
        var fileName = $"{CommunityBridgeFilePrefix}{safeBridgeType}{BundledBridgeFileExtension}";
        yield return Path.Combine(_baseDir, "tor", "pluggable_transports", fileName);
        yield return Path.Combine(_baseDir, "tor", fileName);
    }

    private IEnumerable<string> GetBundledBridgeFileCandidates(string bridgeType)
    {
        var safeBridgeType = bridgeType.Trim().ToLowerInvariant();
        var fileName = $"{BundledBridgeFilePrefix}{safeBridgeType}{BundledBridgeFileExtension}";
        yield return Path.Combine(_baseDir, "tor", "pluggable_transports", fileName);
        yield return Path.Combine(_baseDir, "tor", fileName);
    }

    // When live fetching fails (network blocked, SSL intercepted, censorship), fall back to whatever we
    // cached before, even if it is older than the freshness window - stale working bridges beat no
    // bridges at all, which is exactly the situation in heavily censored networks where the bridge
    // sources are unreachable. Marks the result as the runtime set so the rest of the session reuses it.
    private IReadOnlyList<string> GetStaleCacheFallbackBridgeLines(string bridgeType, Action<string> log)
    {
        var stale = GetCachedBridgeLines(bridgeType, ignoreAge: true);
        if (stale.Count > 0)
        {
            _runtimeFetchedBridges[bridgeType] = stale;
            log($"Using {stale.Count} previously cached {bridgeType} bridge(s) as a fallback (could not reach the bridge sources; the cache may be old).");
        }

        return stale;
    }

    private IReadOnlyList<string> GetCachedBridgeLines(string bridgeType, bool ignoreAge = false)
    {
        EnsureBridgeCacheLoaded();
        if (_cacheStore?.Items == null)
        {
            return Array.Empty<string>();
        }

        var key = bridgeType.Trim().ToLowerInvariant();
        if (!_cacheStore.Items.TryGetValue(key, out var entry) ||
            entry.Lines is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }

        var age = DateTimeOffset.UtcNow - entry.UpdatedUtc;
        if (!ignoreAge && age > BridgeCacheTtl)
        {
            return Array.Empty<string>();
        }

        var filtered = entry.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !IsReservedOrPlaceholderBridgeLine(line))
            .Where(line => IsBridgeLineCompatibleWithType(key, line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (filtered.Count != entry.Lines.Count)
        {
            if (filtered.Count == 0)
            {
                _cacheStore.Items.Remove(key);
            }
            else
            {
                entry.Lines = filtered;
                entry.UpdatedUtc = DateTimeOffset.UtcNow;
            }

            try
            {
                var cacheDir = Path.GetDirectoryName(_bridgeCachePath);
                if (!string.IsNullOrWhiteSpace(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }

                var json = JsonSerializer.Serialize(_cacheStore, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_bridgeCachePath, json);
            }
            catch
            {
                // Cache migration is best-effort.
            }
        }

        return filtered;
    }

    private DateTimeOffset? GetCachedBridgeUpdatedUtc(string bridgeType)
    {
        if (string.IsNullOrWhiteSpace(bridgeType))
        {
            return null;
        }

        EnsureBridgeCacheLoaded();
        if (_cacheStore?.Items == null)
        {
            return null;
        }

        var key = bridgeType.Trim().ToLowerInvariant();
        if (!_cacheStore.Items.TryGetValue(key, out var entry) ||
            entry.Lines is not { Count: > 0 })
        {
            return null;
        }

        return entry.UpdatedUtc;
    }

    private static bool IsReservedOrPlaceholderBridgeLine(string line)
    {
        return IsExplicitPlaceholderBridgeLine(line);
    }

    private static bool IsExplicitPlaceholderBridgeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        if (line.Contains("example.", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("yourdomain", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private void SaveCachedBridgeLines(string bridgeType, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        EnsureBridgeCacheLoaded();
        _cacheStore ??= new BridgeFetchCacheStore();
        _cacheStore.Items ??= new Dictionary<string, BridgeFetchCacheEntry>(StringComparer.OrdinalIgnoreCase);

        var key = bridgeType.Trim().ToLowerInvariant();
        _cacheStore.Items[key] = new BridgeFetchCacheEntry
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            Lines = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => IsBridgeLineCompatibleWithType(key, line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        try
        {
            var cacheDir = Path.GetDirectoryName(_bridgeCachePath);
            if (!string.IsNullOrWhiteSpace(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            var json = JsonSerializer.Serialize(_cacheStore, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_bridgeCachePath, json);
        }
        catch
        {
            // Cache persistence is best-effort only.
        }
    }

    private static bool IsBridgeLineCompatibleWithType(string bridgeType, string line)
    {
        if (string.IsNullOrWhiteSpace(bridgeType) || string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (string.Equals(bridgeType, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
        {
            return IsValidWebTunnelBridgeLine(line);
        }

        if (string.Equals(bridgeType, VanillaBridgeType, StringComparison.OrdinalIgnoreCase))
        {
            return IsValidVanillaBridgeLine(NormalizeVanillaBridgeLine(line));
        }

        return true;
    }

    public static bool BridgeLinesNeedClientTransportPlugins(IReadOnlyList<string> bridgeLines)
    {
        foreach (var line in bridgeLines)
        {
            var transport = ExtractBridgeTransport(line);
            if (!string.IsNullOrWhiteSpace(transport) &&
                !LooksLikeEndpoint(transport) &&
                !string.Equals(transport, VanillaBridgeType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeVanillaBridgeLine(string line)
    {
        var normalized = TrimTrailingBridgeAlias(NormalizeBridgeLine(line));
        if (normalized.StartsWith(VanillaBridgeType + " ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(VanillaBridgeType.Length).Trim();
        }

        return normalized;
    }

    private static bool IsValidVanillaBridgeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !TryParseBridgeEndpointToken(parts[0], out _, out var port))
        {
            return false;
        }

        return port is > 0 and <= 65535;
    }

    private void EnsureBridgeCacheLoaded()
    {
        if (_cacheLoaded)
        {
            return;
        }

        _cacheLoaded = true;

        try
        {
            if (!File.Exists(_bridgeCachePath))
            {
                return;
            }

            var json = File.ReadAllText(_bridgeCachePath);
            var store = JsonSerializer.Deserialize<BridgeFetchCacheStore>(json);
            if (store?.Items == null)
            {
                return;
            }

            _cacheStore = store;
        }
        catch
        {
            _cacheStore = null;
        }
    }

    private sealed class MoatSettingsRequest
    {
        [JsonPropertyName("transports")]
        public List<string> Transports { get; set; } = [];
    }

    private sealed class MoatSettingsResponse
    {
        [JsonPropertyName("settings")]
        public List<MoatSetting>? Settings { get; set; }
    }

    private sealed class MoatSetting
    {
        [JsonPropertyName("bridges")]
        public MoatBridge? Bridge { get; set; }
    }

    private sealed class MoatBridge
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("bridge_strings")]
        public List<string>? BridgeStrings { get; set; }
    }

    private sealed class BridgeFetchCacheStore
    {
        public Dictionary<string, BridgeFetchCacheEntry> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BridgeFetchCacheEntry
    {
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<string> Lines { get; set; } = [];
    }

    private sealed class RuntimeBridgeHealthEntry
    {
        public int FailureCount { get; set; }
        public DateTimeOffset LastFailureUtc { get; set; }
        public DateTimeOffset BlockedUntilUtc { get; set; }
    }
}
