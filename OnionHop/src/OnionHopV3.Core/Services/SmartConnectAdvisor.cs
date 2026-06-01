using System.Globalization;
using System.Text;
using System.Text.Json;
using OnionHopV3.Core.Networking;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Builds a country-aware Tor connection strategy and fallback order for non-technical users.
/// </summary>
public sealed class SmartConnectAdvisor
{
    private const string OoniGeoLookupEndpoint = "https://api.ooni.io/api/v1/geolookup";
    private const string OoniVanillaTorStatsEndpointFormat = "https://api.ooni.io/api/_/vanilla_tor_stats?probe_cc={0}";
    private const string OoniMeasurementsEndpoint = "https://api.ooni.io/api/v1/measurements";
    private const string IpWhoIsEndpointFormat = "https://ipwho.is/{0}";
    private const string AutomaticBridgeType = "automatic";
    private const int MinimumReliableNetworkCount = 5;
    private const long MinimumReliableSampleCount = 200;
    private const int RecentMeasurementWindowDays = 30;
    private const int RecentMeasurementLimitPerTest = 150;
    private const string CsvBaselineEnvPath = "ONIONHOP_SMARTCONNECT_CSV";
    private static readonly string[] CsvBaselineSearchPatterns = ["*measurements*.csv", "*ooni*.csv"];
    private static readonly string[] CsvBaselineKnownFileNames =
    [
        "smartconnect-ooni-baseline.csv",
        "ooni-measurements.csv",
        "measurements.csv"
    ];
    private static readonly string[] OoniTorMeasurementTests = ["vanilla_tor", "torsf"];
    private static readonly HashSet<string> OoniTorBaselineTests = new(StringComparer.OrdinalIgnoreCase)
    {
        "vanilla_tor",
        "torsf",
        "tor",
        "vanillator"
    };
    private static readonly object CsvBaselineCacheLock = new();
    private static IReadOnlyDictionary<string, BaselineSummary>? _csvBaselineByCountry;

    internal const double OpenThreshold = 0.18;
    internal const double ModerateThreshold = 0.38;
    internal const double RestrictedThreshold = 0.62;

    public enum RiskLevel
    {
        Unknown,
        Open,
        Moderate,
        Restricted,
        Severe
    }

    public sealed record Strategy(string Name, string Reason, OnionHopConnectOptions Options);

    public sealed record Plan(
        string? PublicIp,
        string? CountryCode,
        RiskLevel Risk,
        double RestrictionScore,
        long SampleCount,
        int NetworkCount,
        int NotOkNetworks,
        string? LastTested,
        IReadOnlyList<Strategy> Strategies);

    private readonly record struct TorStatsSummary(
        long FailureCount,
        long SampleCount,
        int FailingNetworks,
        int NetworkCount,
        int NotOkNetworks,
        string? LastTested);

    private readonly record struct MeasurementSummary(
        long FailureCount,
        long SampleCount,
        long AnomalyCount,
        int UniqueAsnCount,
        DateTimeOffset? LastMeasurementUtc);

    private readonly record struct BaselineSummary(
        long FailureCount,
        long SampleCount,
        long AnomalyCount,
        int UniqueAsnCount,
        DateTimeOffset? LastMeasurementUtc);

    public async Task<Plan> BuildPlanAsync(OnionHopConnectOptions baseOptions, Action<string>? log, CancellationToken token)
    {
        var publicIp = await IpLookupService.TryFetchDirectIpAsync(
            message => log?.Invoke($"Smart Connect: {message}"),
            token).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(publicIp))
        {
            log?.Invoke($"Smart Connect: detected direct IP {publicIp}.");
        }
        else
        {
            log?.Invoke("Smart Connect: direct IP could not be determined.");
        }

        var countryCode = await ResolveCountryCodeAsync(publicIp, log, token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            log?.Invoke($"Smart Connect: detected country {countryCode}.");
        }
        else
        {
            log?.Invoke("Smart Connect: country detection failed; using generic strategy.");
        }

        TorStatsSummary? stats = null;
        MeasurementSummary? recentMeasurements = null;
        BaselineSummary? csvBaseline = null;
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            stats = await TryFetchTorStatsAsync(countryCode!, log, token).ConfigureAwait(false);
            recentMeasurements = await TryFetchRecentMeasurementsSummaryAsync(countryCode!, log, token).ConfigureAwait(false);
            csvBaseline = await TryLoadCsvBaselineSummaryAsync(countryCode!, log, token).ConfigureAwait(false);
        }

        var signals = new List<(string Source, double Score, double Confidence)>();
        var score = 0d;
        var totalConfidence = 0d;

        if (stats.HasValue)
        {
            var statsScore = ComputeRestrictionScore(
                stats.Value.FailureCount,
                stats.Value.SampleCount,
                stats.Value.FailingNetworks,
                stats.Value.NetworkCount,
                stats.Value.NotOkNetworks);
            var statsConfidence = ComputeTorStatsConfidence(stats.Value);
            signals.Add(("ooni-vanilla_tor_stats", statsScore, statsConfidence));
        }

        if (recentMeasurements.HasValue)
        {
            var measurementScore = ComputeMeasurementRestrictionScore(
                recentMeasurements.Value.FailureCount,
                recentMeasurements.Value.SampleCount,
                recentMeasurements.Value.AnomalyCount);
            var measurementConfidence = ComputeSignalConfidence(
                recentMeasurements.Value.SampleCount,
                recentMeasurements.Value.UniqueAsnCount,
                recentMeasurements.Value.LastMeasurementUtc,
                maxConfidence: 0.85d);
            signals.Add(("ooni-measurements", measurementScore, measurementConfidence));
        }

        if (csvBaseline.HasValue)
        {
            var baselineScore = ComputeMeasurementRestrictionScore(
                csvBaseline.Value.FailureCount,
                csvBaseline.Value.SampleCount,
                csvBaseline.Value.AnomalyCount);
            var baselineConfidence = ComputeSignalConfidence(
                csvBaseline.Value.SampleCount,
                csvBaseline.Value.UniqueAsnCount,
                csvBaseline.Value.LastMeasurementUtc,
                maxConfidence: 0.55d);
            signals.Add(("csv-baseline", baselineScore, baselineConfidence));
        }

        (score, totalConfidence) = CombineSignalScores(signals);
        var hasReliableData = totalConfidence >= 0.35d;
        var liveRisk = DetermineRiskLevel(score, hasReliableData);

        // Apply the offline censorship prior: in a country known to block Tor, never act on a risk
        // lower than that country's floor - even if the live signal was thin or optimistic (OONI is
        // frequently blocked in exactly those countries). The floor can only raise risk, never lower
        // it, and live signals can still push above the floor.
        var risk = CensorshipProfiles.ApplyFloor(liveRisk, countryCode);
        if (risk != liveRisk)
        {
            log?.Invoke($"Smart Connect: country {countryCode} censorship prior raised risk {liveRisk} -> {risk} (offline profile).");
        }

        var preferredTransports = CensorshipProfiles.GetPreferredTransports(countryCode);
        var strategies = BuildStrategiesForRisk(baseOptions, risk, preferredTransports);

        if (stats.HasValue)
        {
            log?.Invoke(
                $"Smart Connect: OONI stats sample={stats.Value.SampleCount}, networks={stats.Value.NetworkCount}, not-ok={stats.Value.NotOkNetworks}.");
        }
        else
        {
            log?.Invoke("Smart Connect: no OONI tor-stats data available.");
        }

        if (recentMeasurements.HasValue)
        {
            log?.Invoke(
                $"Smart Connect: measurements sample={recentMeasurements.Value.SampleCount}, asn={recentMeasurements.Value.UniqueAsnCount}, failures={recentMeasurements.Value.FailureCount}, anomalies={recentMeasurements.Value.AnomalyCount}.");
        }
        else
        {
            log?.Invoke("Smart Connect: no recent measurement summary available.");
        }

        if (csvBaseline.HasValue)
        {
            log?.Invoke(
                $"Smart Connect: CSV baseline sample={csvBaseline.Value.SampleCount}, asn={csvBaseline.Value.UniqueAsnCount}, failures={csvBaseline.Value.FailureCount}.");
        }
        else
        {
            log?.Invoke("Smart Connect: no CSV baseline detected.");
        }

        if (signals.Count > 0)
        {
            var parts = signals.Select(signal => $"{signal.Source}:score={signal.Score:0.000},conf={signal.Confidence:0.00}");
            log?.Invoke($"Smart Connect: signal blend -> {string.Join("; ", parts)}");
        }

        log?.Invoke($"Smart Connect: combined score={score:0.000}, confidence={totalConfidence:0.00}, risk={risk}.");

        if (strategies.Count > 0)
        {
            var order = string.Join(" -> ", strategies.Select(strategy => strategy.Name));
            log?.Invoke($"Smart Connect: strategy order {order}.");
        }

        return new Plan(
            PublicIp: publicIp,
            CountryCode: countryCode,
            Risk: risk,
            RestrictionScore: score,
            SampleCount: stats?.SampleCount ?? 0,
            NetworkCount: stats?.NetworkCount ?? 0,
            NotOkNetworks: stats?.NotOkNetworks ?? 0,
            LastTested: stats?.LastTested,
            Strategies: strategies);
    }

    internal static double ComputeRestrictionScore(
        long failureCount,
        long sampleCount,
        int failingNetworks,
        int networkCount,
        int notOkNetworks)
    {
        if (sampleCount <= 0 || networkCount <= 0)
        {
            return 0d;
        }

        var failureRate = Math.Clamp((double)failureCount / sampleCount, 0d, 1d);
        var failingNetworkShare = Math.Clamp((double)failingNetworks / networkCount, 0d, 1d);
        var notOkShare = Math.Clamp((double)notOkNetworks / Math.Max(1, networkCount), 0d, 1d);

        var score = (failureRate * 0.75d) + (failingNetworkShare * 0.20d) + (notOkShare * 0.05d);
        return Math.Clamp(score, 0d, 1d);
    }

    internal static double ComputeMeasurementRestrictionScore(long failureCount, long sampleCount, long anomalyCount)
    {
        if (sampleCount <= 0)
        {
            return 0d;
        }

        var failureRate = Math.Clamp((double)failureCount / sampleCount, 0d, 1d);
        var anomalyRate = Math.Clamp((double)anomalyCount / sampleCount, 0d, 1d);
        return Math.Clamp((failureRate * 0.85d) + (anomalyRate * 0.15d), 0d, 1d);
    }

    internal static double ComputeSignalConfidence(
        long sampleCount,
        int uniqueAsnCount,
        DateTimeOffset? newestMeasurementUtc,
        double maxConfidence)
    {
        if (sampleCount <= 0)
        {
            return 0d;
        }

        var sampleFactor = Math.Clamp(sampleCount / 120d, 0d, 1d);
        var asnFactor = Math.Clamp(uniqueAsnCount / 8d, 0d, 1d);
        var recencyFactor = newestMeasurementUtc.HasValue
            ? ComputeRecencyFactor(newestMeasurementUtc.Value)
            : 0.2d;

        var confidence = (sampleFactor * 0.55d) + (asnFactor * 0.30d) + (recencyFactor * 0.15d);
        return Math.Clamp(confidence, 0d, Math.Clamp(maxConfidence, 0d, 1d));
    }

    internal static (double Score, double Confidence) CombineSignalScores(IReadOnlyList<(string Source, double Score, double Confidence)> signals)
    {
        if (signals.Count == 0)
        {
            return (0d, 0d);
        }

        var valid = signals
            .Where(signal => signal.Confidence > 0d)
            .ToList();
        if (valid.Count == 0)
        {
            return (0d, 0d);
        }

        var weightSum = valid.Sum(signal => signal.Confidence);
        if (weightSum <= 0)
        {
            return (0d, 0d);
        }

        var score = valid.Sum(signal => signal.Score * signal.Confidence) / weightSum;
        var confidence = Math.Clamp(weightSum / valid.Count, 0d, 1d);
        return (Math.Clamp(score, 0d, 1d), confidence);
    }

    internal static RiskLevel DetermineRiskLevel(double score, bool hasReliableData)
    {
        if (!hasReliableData)
        {
            return RiskLevel.Unknown;
        }

        if (score < OpenThreshold)
        {
            return RiskLevel.Open;
        }

        if (score < ModerateThreshold)
        {
            return RiskLevel.Moderate;
        }

        if (score < RestrictedThreshold)
        {
            return RiskLevel.Restricted;
        }

        return RiskLevel.Severe;
    }

    public static IReadOnlyList<Strategy> BuildStrategiesForRisk(OnionHopConnectOptions baseOptions, RiskLevel risk)
        => BuildStrategiesForRisk(baseOptions, risk, Array.Empty<string>());

    // Default transport ladders per risk tier, ordered by what tends to survive that level of
    // censorship. "automatic" lets the bridge manager pick across types; the explicit entries that
    // follow give deterministic fallbacks. dnstt is the last-resort DNS tunnel for the hardest case.
    private static readonly string[] OpenLadder = ["obfs4"];
    private static readonly string[] ModerateLadder = [AutomaticBridgeType, "snowflake", "obfs4"];
    private static readonly string[] RestrictedLadder = [AutomaticBridgeType, "snowflake", "webtunnel", "obfs4"];
    private static readonly string[] SevereLadder = ["snowflake", "webtunnel", "conjure", "obfs4", "dnstt"];

    public static IReadOnlyList<Strategy> BuildStrategiesForRisk(
        OnionHopConnectOptions baseOptions,
        RiskLevel risk,
        IReadOnlyList<string> preferredTransports)
    {
        var strategies = new List<Strategy>();

        // A country's preferred transports (from the offline profile) lead the bridge ladder, then
        // the risk-tier defaults fill in anything not already covered. Deduplicated downstream.
        IEnumerable<string> BridgeLadder(IReadOnlyList<string> tierDefaults)
            => (preferredTransports ?? Array.Empty<string>()).Concat(tierDefaults);

        switch (risk)
        {
            case RiskLevel.Open:
                AddDirect(strategies, baseOptions, "Open network profile. Try direct Tor first.");
                AddBridge(strategies, baseOptions, AutomaticBridgeType, "Fallback: automatic bridges.");
                AddBridgeLadder(strategies, baseOptions, BridgeLadder(OpenLadder), "Fallback");
                break;

            case RiskLevel.Moderate:
                // Try direct briefly, but bridges are the real plan here.
                AddDirect(strategies, baseOptions, "Moderately restricted profile. Try a brief direct attempt.");
                AddBridgeLadder(strategies, baseOptions, BridgeLadder(ModerateLadder), "Bridge");
                break;

            case RiskLevel.Restricted:
                // No direct first - Tor is commonly blocked here. Bridges lead; direct is a last resort.
                AddBridgeLadder(strategies, baseOptions, BridgeLadder(RestrictedLadder), "Restricted profile bridge");
                AddDirect(strategies, baseOptions, "Last fallback: direct Tor.");
                break;

            case RiskLevel.Severe:
                // Aggressive, modern ladder. Snowflake with AMP fronting first, ending in dnstt
                // (works where only DNS gets through). Direct only as an absolute last resort.
                AddBridgeLadder(strategies, baseOptions, BridgeLadder(SevereLadder), "Heavily restricted profile bridge", forceSnowflakeAmp: true);
                AddDirect(strategies, baseOptions, "Last fallback: direct Tor.");
                break;

            default:
                AddDirect(strategies, baseOptions, "Unknown network profile. Start direct.");
                AddBridge(strategies, baseOptions, AutomaticBridgeType, "Fallback: automatic bridges.");
                AddBridgeLadder(strategies, baseOptions, BridgeLadder(["obfs4", "snowflake"]), "Fallback");
                break;
        }

        return DeduplicateStrategies(strategies);
    }

    private static void AddBridgeLadder(
        ICollection<Strategy> target,
        OnionHopConnectOptions baseOptions,
        IEnumerable<string> transports,
        string reasonPrefix,
        bool forceSnowflakeAmp = false)
    {
        foreach (var transport in transports)
        {
            if (string.IsNullOrWhiteSpace(transport))
            {
                continue;
            }

            var label = string.Equals(transport, AutomaticBridgeType, StringComparison.OrdinalIgnoreCase)
                ? "automatic bridges"
                : $"{transport} bridge";
            AddBridge(target, baseOptions, transport, $"{reasonPrefix}: {label}.", forceSnowflakeAmp);
        }
    }

    // Per-strategy fail-fast budgets. A reachable bridge / direct path bootstraps well within these;
    // anything slower is almost certainly blocked, so we escalate to the next strategy rather than
    // waiting out a multi-minute timeout on a dead path.
    internal const int DirectAttemptTimeoutSeconds = 30;
    internal const int BridgeAttemptTimeoutSeconds = 45;

    private static void AddDirect(ICollection<Strategy> target, OnionHopConnectOptions baseOptions, string reason)
    {
        target.Add(new Strategy(
            Name: "direct",
            Reason: reason,
            Options: baseOptions with
            {
                UseTorBridges = false,
                UseCensoredMode = false,
                SelectedBridgeType = AutomaticBridgeType,
                BridgeSourceMode = OnionHopConnectOptions.BridgeSourceAuto,
                CustomBridges = null,
                ExitNodeFingerprint = null,
                SmartConnectAttemptTimeoutSeconds = DirectAttemptTimeoutSeconds
            }));
    }

    private static void AddBridge(
        ICollection<Strategy> target,
        OnionHopConnectOptions baseOptions,
        string bridgeType,
        string reason,
        bool forceSnowflakeAmp = false)
    {
        var normalizedBridgeType = string.IsNullOrWhiteSpace(bridgeType) ? AutomaticBridgeType : bridgeType.Trim().ToLowerInvariant();
        var shouldApplySnowflakeAmp = normalizedBridgeType == "snowflake" || normalizedBridgeType == AutomaticBridgeType;
        var useSnowflakeAmp = shouldApplySnowflakeAmp
            ? (baseOptions.UseSnowflakeAmp || forceSnowflakeAmp)
            : baseOptions.UseSnowflakeAmp;

        target.Add(new Strategy(
            Name: $"bridge:{normalizedBridgeType}",
            Reason: reason,
            Options: baseOptions with
            {
                UseTorBridges = true,
                UseCensoredMode = true,
                SelectedBridgeType = normalizedBridgeType,
                BridgeSourceMode = OnionHopConnectOptions.BridgeSourceAuto,
                CustomBridges = null,
                CustomSniHosts = null,
                SelectedEntryLocation = OnionHopConnectOptions.AutomaticLocationLabel,
                ExitNodeFingerprint = null,
                UseSnowflakeAmp = useSnowflakeAmp,
                SmartConnectAttemptTimeoutSeconds = BridgeAttemptTimeoutSeconds
            }));
    }

    private static IReadOnlyList<Strategy> DeduplicateStrategies(IEnumerable<Strategy> strategies)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<Strategy>();
        foreach (var strategy in strategies)
        {
            var key = BuildStrategyKey(strategy.Options);
            if (!seen.Add(key))
            {
                continue;
            }

            unique.Add(strategy);
        }

        return unique;
    }

    private static string BuildStrategyKey(OnionHopConnectOptions options)
    {
        return string.Join(
            "|",
            options.SelectedConnectionMode.Trim(),
            options.UseHybridRouting ? "hybrid:1" : "hybrid:0",
            options.UseTorBridges ? "bridges:1" : "bridges:0",
            options.UseCensoredMode ? "censored:1" : "censored:0",
            options.SelectedBridgeType.Trim().ToLowerInvariant(),
            options.BridgeSourceMode.Trim(),
            options.UseSnowflakeAmp ? "amp:1" : "amp:0",
            options.ProxyScopeMode.Trim(),
            options.OnionDnsProxyEnabled ? "dnsproxy:1" : "dnsproxy:0");
    }

    private static async Task<string?> ResolveCountryCodeAsync(string? publicIp, Action<string>? log, CancellationToken token)
    {
        string? countryCode = null;
        try
        {
            countryCode = await TryFetchCountryCodeFromOoniGeoLookupAsync(publicIp, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Smart Connect: OONI geolookup failed: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            log?.Invoke("Smart Connect: country resolved from OONI geolookup.");
            return countryCode;
        }

        try
        {
            countryCode = await TryFetchCountryCodeFromIpWhoIsAsync(publicIp, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Smart Connect: ipwho.is lookup failed: {ex.Message}");
            countryCode = null;
        }

        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            log?.Invoke("Smart Connect: country resolved from ipwho.is.");
            return countryCode;
        }

        return null;
    }

    private static async Task<string?> TryFetchCountryCodeFromOoniGeoLookupAsync(string? publicIp, CancellationToken token)
    {
        var payload = string.IsNullOrWhiteSpace(publicIp)
            ? "{}"
            : JsonSerializer.Serialize(new { ip = publicIp.Trim() });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await HttpClientFactory.Default.PostAsync(OoniGeoLookupEndpoint, content, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (TryReadCountryCode(root, "probe_cc", out var direct))
        {
            return direct;
        }

        if (root.TryGetProperty("geolocation", out var geolocation) &&
            geolocation.ValueKind == JsonValueKind.Object &&
            (TryReadCountryCode(geolocation, "probe_cc", out var geolocProbeCc) ||
             TryReadCountryCode(geolocation, "country_code", out geolocProbeCc) ||
             TryReadCountryCode(geolocation, "countryCode", out geolocProbeCc)))
        {
            return geolocProbeCc;
        }

        return null;
    }

    private static async Task<string?> TryFetchCountryCodeFromIpWhoIsAsync(string? publicIp, CancellationToken token)
    {
        var suffix = string.IsNullOrWhiteSpace(publicIp) ? string.Empty : Uri.EscapeDataString(publicIp.Trim());
        var uri = new Uri(string.Format(CultureInfo.InvariantCulture, IpWhoIsEndpointFormat, suffix));

        using var response = await HttpClientFactory.Default.GetAsync(uri, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var success = true;
        if (root.TryGetProperty("success", out var successElement))
        {
            success = successElement.ValueKind == JsonValueKind.True ||
                      (successElement.ValueKind == JsonValueKind.String &&
                       bool.TryParse(successElement.GetString(), out var parsed) &&
                       parsed);
        }

        if (!success)
        {
            return null;
        }

        return TryReadCountryCode(root, "country_code", out var countryCode) ? countryCode : null;
    }

    private static async Task<TorStatsSummary?> TryFetchTorStatsAsync(string countryCode, Action<string>? log, CancellationToken token)
    {
        var normalizedCountryCode = countryCode.Trim().ToUpperInvariant();
        var uri = new Uri(string.Format(CultureInfo.InvariantCulture, OoniVanillaTorStatsEndpointFormat, normalizedCountryCode));

        try
        {
            using var response = await HttpClientFactory.Default.GetAsync(uri, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"Smart Connect: OONI stats request failed with HTTP {(int)response.StatusCode} for {normalizedCountryCode}.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var lastTested = ReadString(root, "last_tested");
            var notOkNetworks = ReadInt(root, "notok_networks");
            var networkCount = 0;
            var failingNetworks = 0;
            long failureCount = 0;
            long sampleCount = 0;

            if (root.TryGetProperty("networks", out var networks) &&
                networks.ValueKind == JsonValueKind.Array)
            {
                foreach (var network in networks.EnumerateArray())
                {
                    if (network.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var failures = Math.Max(0, ReadInt(network, "failure_count"));
                    var successes = Math.Max(0, ReadInt(network, "success_count"));
                    var total = ReadInt(network, "total_count");
                    if (total <= 0)
                    {
                        total = failures + successes;
                    }

                    if (total <= 0)
                    {
                        continue;
                    }

                    networkCount++;
                    sampleCount += total;
                    failureCount += Math.Min(failures, total);

                    if (failures > successes)
                    {
                        failingNetworks++;
                    }
                }
            }

            if (networkCount == 0 || sampleCount == 0)
            {
                return null;
            }

            return new TorStatsSummary(
                FailureCount: failureCount,
                SampleCount: sampleCount,
                FailingNetworks: failingNetworks,
                NetworkCount: networkCount,
                NotOkNetworks: Math.Max(0, notOkNetworks),
                LastTested: lastTested);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Smart Connect: OONI stats fetch failed for {normalizedCountryCode}: {ex.Message}");
            return null;
        }
    }

    private static async Task<MeasurementSummary?> TryFetchRecentMeasurementsSummaryAsync(
        string countryCode,
        Action<string>? log,
        CancellationToken token)
    {
        var normalizedCountryCode = countryCode.Trim().ToUpperInvariant();
        var since = DateTimeOffset.UtcNow.AddDays(-RecentMeasurementWindowDays)
            .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        long failureCount = 0;
        long anomalyCount = 0;
        long sampleCount = 0;
        var asns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? newestMeasurementUtc = null;

        foreach (var testName in OoniTorMeasurementTests)
        {
            token.ThrowIfCancellationRequested();
            var uri = BuildMeasurementsQueryUri(normalizedCountryCode, testName, since, RecentMeasurementLimitPerTest);
            try
            {
                using var response = await HttpClientFactory.Default.GetAsync(uri, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    log?.Invoke($"Smart Connect: measurements query failed for {testName} in {normalizedCountryCode} (HTTP {(int)response.StatusCode}).");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var measurement in results.EnumerateArray())
                {
                    if (measurement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    sampleCount++;
                    if (ReadBool(measurement, "failure"))
                    {
                        failureCount++;
                    }

                    if (ReadBool(measurement, "anomaly"))
                    {
                        anomalyCount++;
                    }

                    var asn = ReadString(measurement, "probe_asn");
                    if (!string.IsNullOrWhiteSpace(asn))
                    {
                        asns.Add(asn.Trim().ToUpperInvariant());
                    }

                    var timestampRaw = ReadString(measurement, "measurement_start_time");
                    if (TryParseDateTimeOffset(timestampRaw, out var parsedTimestamp))
                    {
                        if (!newestMeasurementUtc.HasValue || parsedTimestamp > newestMeasurementUtc.Value)
                        {
                            newestMeasurementUtc = parsedTimestamp;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Smart Connect: measurements query failed for {testName} in {normalizedCountryCode}: {ex.Message}");
            }
        }

        if (sampleCount <= 0)
        {
            return null;
        }

        return new MeasurementSummary(
            FailureCount: failureCount,
            SampleCount: sampleCount,
            AnomalyCount: anomalyCount,
            UniqueAsnCount: asns.Count,
            LastMeasurementUtc: newestMeasurementUtc);
    }

    private static Uri BuildMeasurementsQueryUri(string countryCode, string testName, string since, int limit)
    {
        var query = string.Join(
            "&",
            $"probe_cc={Uri.EscapeDataString(countryCode)}",
            $"test_name={Uri.EscapeDataString(testName)}",
            $"since={Uri.EscapeDataString(since)}",
            "order=desc",
            "order_by=measurement_start_time",
            $"limit={Math.Max(1, limit)}");
        return new Uri($"{OoniMeasurementsEndpoint}?{query}", UriKind.Absolute);
    }

    private static Task<BaselineSummary?> TryLoadCsvBaselineSummaryAsync(
        string countryCode,
        Action<string>? log,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var baselineByCountry = EnsureCsvBaselineLoaded(log, token);
        if (baselineByCountry.Count == 0)
        {
            return Task.FromResult<BaselineSummary?>(null);
        }

        if (!baselineByCountry.TryGetValue(countryCode.Trim().ToUpperInvariant(), out var summary))
        {
            return Task.FromResult<BaselineSummary?>(null);
        }

        return Task.FromResult<BaselineSummary?>(summary);
    }

    private static IReadOnlyDictionary<string, BaselineSummary> EnsureCsvBaselineLoaded(Action<string>? log, CancellationToken token)
    {
        lock (CsvBaselineCacheLock)
        {
            if (_csvBaselineByCountry != null)
            {
                return _csvBaselineByCountry;
            }

            var csvPath = ResolveCsvBaselinePath();
            if (string.IsNullOrWhiteSpace(csvPath))
            {
                _csvBaselineByCountry = new Dictionary<string, BaselineSummary>(StringComparer.OrdinalIgnoreCase);
                return _csvBaselineByCountry;
            }

            try
            {
                _csvBaselineByCountry = ParseCsvBaseline(csvPath!, log, token);
                if (_csvBaselineByCountry.Count > 0)
                {
                    log?.Invoke($"Smart Connect: loaded CSV baseline ({_csvBaselineByCountry.Count} countries) from {csvPath}.");
                }
                else
                {
                    log?.Invoke($"Smart Connect: CSV baseline was found but no usable Tor rows were parsed ({csvPath}).");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Smart Connect: failed to parse CSV baseline '{csvPath}': {ex.Message}");
                _csvBaselineByCountry = new Dictionary<string, BaselineSummary>(StringComparer.OrdinalIgnoreCase);
            }

            return _csvBaselineByCountry;
        }
    }

    private static string? ResolveCsvBaselinePath()
    {
        var envPath = Environment.GetEnvironmentVariable(CsvBaselineEnvPath);
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        var probeDirectories = new List<string>();
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnionHop");
        var localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnionHop V3");
        var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        probeDirectories.Add(appDataDir);
        probeDirectories.Add(localAppDataDir);
        probeDirectories.Add(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(downloadsDir))
        {
            probeDirectories.Add(downloadsDir);
        }

        var candidates = new List<(string Path, DateTime LastWriteUtc)>();
        foreach (var directory in probeDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var knownName in CsvBaselineKnownFileNames)
            {
                var knownPath = Path.Combine(directory, knownName);
                if (File.Exists(knownPath))
                {
                    candidates.Add((knownPath, File.GetLastWriteTimeUtc(knownPath)));
                }
            }

            foreach (var pattern in CsvBaselineSearchPatterns)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    candidates.Add((file, File.GetLastWriteTimeUtc(file)));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.LastWriteUtc)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();

        return selected;
    }

    private static IReadOnlyDictionary<string, BaselineSummary> ParseCsvBaseline(
        string csvPath,
        Action<string>? log,
        CancellationToken token)
    {
        using var stream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return new Dictionary<string, BaselineSummary>(StringComparer.OrdinalIgnoreCase);
        }

        var headers = ParseCsvRow(headerLine!);
        var indexByHeader = headers
            .Select((header, index) => (Header: header.Trim().Trim('\uFEFF').ToLowerInvariant(), Index: index))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Header))
            .GroupBy(entry => entry.Header, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(entry => entry.Header, entry => entry.Index, StringComparer.OrdinalIgnoreCase);

        if (!indexByHeader.ContainsKey("probe_cc") || !indexByHeader.ContainsKey("test_name"))
        {
            log?.Invoke($"Smart Connect: CSV baseline missing required columns (probe_cc/test_name): {csvPath}");
            return new Dictionary<string, BaselineSummary>(StringComparer.OrdinalIgnoreCase);
        }

        var accumulators = new Dictionary<string, BaselineAccumulator>(StringComparer.OrdinalIgnoreCase);
        long parsedRows = 0;
        long acceptedRows = 0;

        while (!reader.EndOfStream)
        {
            token.ThrowIfCancellationRequested();
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            parsedRows++;
            var row = ParseCsvRow(line);
            var countryRaw = GetCsvValue(row, indexByHeader, "probe_cc");
            if (!TryNormalizeCountryCode(countryRaw, out var countryCode))
            {
                continue;
            }

            var testName = GetCsvValue(row, indexByHeader, "test_name");
            if (string.IsNullOrWhiteSpace(testName) || !OoniTorBaselineTests.Contains(testName.Trim()))
            {
                continue;
            }

            if (!accumulators.TryGetValue(countryCode, out var accumulator))
            {
                accumulator = new BaselineAccumulator();
                accumulators[countryCode] = accumulator;
            }

            acceptedRows++;
            accumulator.SampleCount++;
            if (ParseCsvBoolean(GetCsvValue(row, indexByHeader, "failure")))
            {
                accumulator.FailureCount++;
            }

            if (ParseCsvBoolean(GetCsvValue(row, indexByHeader, "anomaly")))
            {
                accumulator.AnomalyCount++;
            }

            var asn = GetCsvValue(row, indexByHeader, "probe_asn");
            if (!string.IsNullOrWhiteSpace(asn))
            {
                accumulator.UniqueAsns.Add(asn.Trim().ToUpperInvariant());
            }

            var timestampRaw = GetCsvValue(row, indexByHeader, "measurement_start_time");
            if (TryParseDateTimeOffset(timestampRaw, out var parsedTimestamp))
            {
                if (!accumulator.LastMeasurementUtc.HasValue || parsedTimestamp > accumulator.LastMeasurementUtc.Value)
                {
                    accumulator.LastMeasurementUtc = parsedTimestamp;
                }
            }
        }

        if (acceptedRows == 0)
        {
            return new Dictionary<string, BaselineSummary>(StringComparer.OrdinalIgnoreCase);
        }

        log?.Invoke($"Smart Connect: parsed {acceptedRows}/{parsedRows} CSV baseline rows from {csvPath}.");

        return accumulators.ToDictionary(
            pair => pair.Key,
            pair => new BaselineSummary(
                FailureCount: pair.Value.FailureCount,
                SampleCount: pair.Value.SampleCount,
                AnomalyCount: pair.Value.AnomalyCount,
                UniqueAsnCount: pair.Value.UniqueAsns.Count,
                LastMeasurementUtc: pair.Value.LastMeasurementUtc),
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BaselineAccumulator
    {
        public long FailureCount { get; set; }
        public long SampleCount { get; set; }
        public long AnomalyCount { get; set; }
        public HashSet<string> UniqueAsns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? LastMeasurementUtc { get; set; }
    }

    internal static IReadOnlyList<string> ParseCsvRow(string line)
    {
        var values = new List<string>();
        if (line == null)
        {
            return values;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character == ',')
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else if (character == '"')
            {
                inQuotes = true;
            }
            else
            {
                current.Append(character);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static string? GetCsvValue(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> indexByHeader, string header)
    {
        if (!indexByHeader.TryGetValue(header, out var index))
        {
            return null;
        }

        if (index < 0 || index >= row.Count)
        {
            return null;
        }

        return row[index];
    }

    internal static bool ParseCsvBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (bool.TryParse(normalized, out var booleanValue))
        {
            return booleanValue;
        }

        return normalized switch
        {
            "1" => true,
            "0" => false,
            "yes" => true,
            "no" => false,
            "y" => true,
            "n" => false,
            _ => false
        };
    }

    private static bool TryNormalizeCountryCode(string? value, out string countryCode)
    {
        countryCode = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 2 || normalized.Any(character => !char.IsLetter(character)))
        {
            return false;
        }

        countryCode = normalized;
        return true;
    }

    private static double ComputeTorStatsConfidence(TorStatsSummary summary)
    {
        var sampleFactor = Math.Clamp(summary.SampleCount / 5000d, 0d, 1d);
        var networkFactor = Math.Clamp(summary.NetworkCount / 20d, 0d, 1d);
        var recencyFactor = TryParseDateTimeOffset(summary.LastTested, out var timestamp)
            ? ComputeRecencyFactor(timestamp)
            : 0.4d;

        var confidence = (sampleFactor * 0.45d) + (networkFactor * 0.45d) + (recencyFactor * 0.10d);
        return Math.Clamp(confidence, 0d, 1d);
    }

    private static double ComputeRecencyFactor(DateTimeOffset timestampUtc)
    {
        var age = DateTimeOffset.UtcNow - timestampUtc;
        if (age <= TimeSpan.FromDays(2))
        {
            return 1d;
        }

        if (age <= TimeSpan.FromDays(7))
        {
            return 0.9d;
        }

        if (age <= TimeSpan.FromDays(21))
        {
            return 0.75d;
        }

        if (age <= TimeSpan.FromDays(45))
        {
            return 0.55d;
        }

        if (age <= TimeSpan.FromDays(90))
        {
            return 0.35d;
        }

        return 0.2d;
    }

    private static bool TryParseDateTimeOffset(string? value, out DateTimeOffset timestampUtc)
    {
        timestampUtc = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateTimeOffset.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            timestampUtc = parsed;
            return true;
        }

        if (DateTime.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedDateTime))
        {
            timestampUtc = new DateTimeOffset(parsedDateTime.ToUniversalTime());
            return true;
        }

        return false;
    }

    private static bool TryReadCountryCode(JsonElement element, string propertyName, out string countryCode)
    {
        countryCode = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 2)
        {
            return false;
        }

        countryCode = normalized;
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt32(out var intValue) && intValue != 0,
            JsonValueKind.String => ParseCsvBoolean(property.GetString()),
            _ => false
        };
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            if (property.TryGetInt32(out var int32))
            {
                return int32;
            }

            if (property.TryGetInt64(out var int64))
            {
                return int64 > int.MaxValue
                    ? int.MaxValue
                    : int64 < int.MinValue
                        ? int.MinValue
                        : (int)int64;
            }

            if (property.TryGetDouble(out var doubleValue))
            {
                if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
                {
                    return 0;
                }

                if (doubleValue > int.MaxValue)
                {
                    return int.MaxValue;
                }

                if (doubleValue < int.MinValue)
                {
                    return int.MinValue;
                }

                return (int)Math.Round(doubleValue);
            }
        }
        else if (property.ValueKind == JsonValueKind.String &&
                 int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }
}
