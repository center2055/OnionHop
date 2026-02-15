using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV2.Core.Tor;

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
    private const string WebTunnelBridgeType = "webtunnel";
    private const string SnowflakeBridgeType = "snowflake";
    private const string Obfs4BridgeType = "obfs4";
    private const string ConjureBridgeType = "conjure";
    private static readonly string[] AutomaticBridgeFallbackChain = [WebTunnelBridgeType, SnowflakeBridgeType, Obfs4BridgeType];
    private const string WebTunnelClientFileName = "webtunnel-client.exe";
    private const string BundledBridgeFilePrefix = "bridges-";
    private const string CommunityBridgeFilePrefix = "bridges-community-";
    private const string BundledBridgeFileExtension = ".txt";
    private static readonly TimeSpan BridgeCacheTtl = TimeSpan.FromHours(12);
    private const string MoatBuiltinUrl = "https://bridges.torproject.org/moat/circumvention/builtin";
    private const string MoatSettingsUrl = "https://bridges.torproject.org/moat/circumvention/settings";
    private const string MoatDefaultsUrl = "https://bridges.torproject.org/moat/circumvention/defaults";
    private enum BridgeSourcePreference
    {
        Auto,
        BridgeDbOnly,
        OfflineOnly
    }

    private readonly string _baseDir;
    private readonly string _bridgeCachePath;
    private readonly SemaphoreSlim _bridgeFetchLock = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<string>> _runtimeFetchedBridges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fetchAttempted = new(StringComparer.OrdinalIgnoreCase);
    private BridgeFetchCacheStore? _cacheStore;
    private bool _cacheLoaded;

    public TorBridgeManager(string baseDir)
    {
        _baseDir = baseDir;
        _bridgeCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OnionHop V2",
            "bridge-cache.json");
    }

    public string? BridgeValidationMessage { get; private set; }

    public static bool IsPlaceholderBridgeLine(string line)
    {
        return IsExplicitPlaceholderBridgeLine(line);
    }

    public static bool IsAutomaticBridgeType(string? bridgeType)
    {
        return string.Equals(bridgeType, AutomaticBridgeType, StringComparison.OrdinalIgnoreCase);
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
        if (string.Equals(mode, OnionHopConnectOptions.BridgeSourceBridgeDbOnly, StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourcePreference.BridgeDbOnly;
        }

        if (string.Equals(mode, OnionHopConnectOptions.BridgeSourceOfflineOnly, StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourcePreference.OfflineOnly;
        }

        return BridgeSourcePreference.Auto;
    }

    public static IReadOnlyList<string> GetBridgeTypeKeys(PluggableTransportConfig? config)
    {
        var bridgeKeys = config?.Bridges?.Keys?
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        if (!bridgeKeys.Any(key => string.Equals(key, AutomaticBridgeType, StringComparison.OrdinalIgnoreCase)))
        {
            bridgeKeys.Add(AutomaticBridgeType);
        }

        if (bridgeKeys.Count == 0)
        {
            bridgeKeys.AddRange([AutomaticBridgeType, Obfs4BridgeType, SnowflakeBridgeType, ConjureBridgeType, "meek-azure"]);
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

        var preferredOrder = new[] { AutomaticBridgeType, WebTunnelBridgeType, SnowflakeBridgeType, Obfs4BridgeType, ConjureBridgeType, "meek-azure", "meek" };
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
        var allowBridgeDbFetch = sourcePreference != BridgeSourcePreference.OfflineOnly;
        var allowOfflineFallback = sourcePreference != BridgeSourcePreference.BridgeDbOnly;
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
            if (allowBridgeDbFetch)
            {
                var fetched = await TryFetchBridgeLinesAsync(selectedBridgeType, log, token).ConfigureAwait(false);
                if (fetched.Count > 0)
                {
                    selected = fetched;
                }
            }

            if (selected.Count == 0 && allowOfflineFallback)
            {
                if (allowBridgeDbFetch && sourcePreference == BridgeSourcePreference.Auto)
                {
                    log($"BridgeDB fetch for {selectedBridgeType} was unavailable. Falling back to offline bridge lists.");
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
                    // When using bundled BridgeDB entries, shuffle the order so we don't get stuck retrying
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

        // If custom bridges were provided but none are usable, fall back to bundled BridgeDB entries.
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

        if (selected.Count == 0 && string.IsNullOrWhiteSpace(BridgeValidationMessage))
        {
            BridgeValidationMessage = sourcePreference switch
            {
                BridgeSourcePreference.BridgeDbOnly => "No usable bridge lines were fetched from BridgeDB.",
                BridgeSourcePreference.OfflineOnly => "No usable offline bridge lines were found.",
                _ => "No usable bridge lines were found (BridgeDB and offline fallback both failed)."
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

    private async Task<IReadOnlyList<string>> TryFetchBridgeLinesAsync(string bridgeType, Action<string> log, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(bridgeType) || string.Equals(bridgeType, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        if (_runtimeFetchedBridges.TryGetValue(bridgeType, out var runtimeCached) && runtimeCached.Count > 0)
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

        await _bridgeFetchLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_runtimeFetchedBridges.TryGetValue(bridgeType, out runtimeCached) && runtimeCached.Count > 0)
            {
                return runtimeCached;
            }

            if (_fetchAttempted.Contains(bridgeType))
            {
                return Array.Empty<string>();
            }

            _fetchAttempted.Add(bridgeType);

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHopV2/2.0");

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
                log($"Loaded {lines.Count} {bridgeType} bridges from BridgeDB.");
                return lines;
            }

            log($"Bridge auto-fetch for {bridgeType} returned no usable lines (BridgeDB may require CAPTCHA).");
            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            log($"Bridge auto-fetch for {bridgeType} failed: {ex.Message}");
            return Array.Empty<string>();
        }
        finally
        {
            _bridgeFetchLock.Release();
        }
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
        var ptRelativePath = "pluggable_transports";
        var ptRelativePathWithSlash = ptRelativePath + Path.DirectorySeparatorChar;

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in bridgeLines)
        {
            var transport = ExtractBridgeTransport(line);
            if (!string.IsNullOrWhiteSpace(transport))
            {
                needed.Add(transport);
            }
        }

        if (needed.Count == 0)
        {
            BridgeValidationMessage = "Bridge lines are missing the transport type (expected e.g. 'snowflake ...', 'obfs4 ...').";
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

                pluginLines.Add($"ClientTransportPlugin {transport} exec {Path.Combine(ptRelativePath, "lyrebird.exe")}");
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
        if (pluginLine.Contains("snowflake-client.exe", StringComparison.OrdinalIgnoreCase))
        {
            return pluginLine;
        }

        // Some bundled pt_config.json files incorrectly map snowflake to lyrebird.exe. Override safely.
        return $"ClientTransportPlugin snowflake exec {Path.Combine(ptRelativePath, "snowflake-client.exe")}";
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
        return pluginLine.Trim();
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
                plugins.Add($"ClientTransportPlugin snowflake exec {Path.Combine(ptRelativePath, "snowflake-client.exe")}");
                continue;
            }

            plugins.Add($"ClientTransportPlugin {transport} exec {Path.Combine(ptRelativePath, "lyrebird.exe")}");
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
            var found = FindWebTunnelClientInTorBrowser();
            if (!string.IsNullOrWhiteSpace(found))
            {
                try
                {
                    File.Copy(found, clientPath, true);
                    log($"Copied {WebTunnelClientFileName} from Tor Browser.");
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
            BridgeValidationMessage = $"Webtunnel client is missing ({WebTunnelClientFileName}). Install Tor Browser and copy it into tor\\pluggable_transports.";
            return false;
        }

        pluginLine = $"ClientTransportPlugin webtunnel exec {Path.Combine("pluggable_transports", WebTunnelClientFileName)}";
        return true;
    }

    private static string? FindWebTunnelClientInTorBrowser()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

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

    private IReadOnlyList<string> GetCachedBridgeLines(string bridgeType)
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
        if (age > BridgeCacheTtl)
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

        return true;
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
}
