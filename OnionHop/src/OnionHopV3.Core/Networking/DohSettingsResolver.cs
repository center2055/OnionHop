using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Networking;

internal readonly record struct DohSettings(string Server, int Port, string Path);
internal readonly record struct DohResolutionResult(DohSettings Settings, string RequestedProvider, string EffectiveProvider, bool UsedFallback, long ProbeLatencyMs);

internal static class DohSettingsResolver
{
    private const string DefaultPath = "/dns-query";
    private const int DefaultPort = 443;
    private const string ProbeQuery = "AAABAAABAAAAAAAAB2V4YW1wbGUDY29tAAABAAE";
    private const int ProbeSamplesPerCandidate = 2;
    private static readonly TimeSpan AutoProviderCacheTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan AutoProviderRefreshThrottle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AutoProviderBackgroundRefreshTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient ProbeHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };
    private static readonly object AutoProviderCacheLock = new();
    private static readonly string AutoProviderCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OnionHop",
        "doh-auto-cache.json");

    private static bool _autoProviderCacheLoaded;
    private static DohAutoProviderCacheSnapshot? _autoProviderCache;
    private static DateTimeOffset _lastAutoProviderRefreshUtc = DateTimeOffset.MinValue;
    private static int _autoProviderRefreshInFlight;

    public static DohSettings Resolve(OnionHopConnectOptions options)
    {
        var provider = NormalizeProvider(options.SelectedDnsProvider);
        return ResolveProviderSettings(provider, options);
    }

    public static async Task<DohResolutionResult> ResolveWithHealthFallbackAsync(
        OnionHopConnectOptions options,
        Action<string> log,
        CancellationToken token)
    {
        var requestedProvider = NormalizeProvider(options.SelectedDnsProvider);

        if (string.Equals(requestedProvider, OnionHopConnectOptions.DnsProviderAuto, StringComparison.Ordinal))
        {
            if (TryGetCachedAutoProvider(requireFresh: true, out var cachedAutoProvider))
            {
                TriggerBackgroundAutoProviderRefresh(log);
                log($"DoH auto-selected cached provider {cachedAutoProvider.Provider} ({cachedAutoProvider.LatencyMs} ms).");
                var cachedSettings = ResolveProviderSettings(cachedAutoProvider.Provider, options);
                return new DohResolutionResult(cachedSettings, requestedProvider, cachedAutoProvider.Provider, false, cachedAutoProvider.LatencyMs);
            }

            var bestBuiltIn = await SelectFastestHealthyCandidateAsync(GetBuiltInCandidates(), token).ConfigureAwait(false);
            if (bestBuiltIn is { } best)
            {
                UpdateAutoProviderCache(best.Provider, best.LatencyMs);
                log($"DoH auto-selected {best.Provider} ({best.LatencyMs} ms).");
                return new DohResolutionResult(best.Settings, requestedProvider, best.Provider, false, best.LatencyMs);
            }

            if (TryGetCachedAutoProvider(requireFresh: false, out var staleAutoProvider))
            {
                var staleSettings = ResolveProviderSettings(staleAutoProvider.Provider, options);
                log($"DoH auto-selection probe failed for all built-in providers. Reusing cached provider {staleAutoProvider.Provider} ({DescribeAge(staleAutoProvider.UpdatedUtc)} old).");
                return new DohResolutionResult(staleSettings, requestedProvider, staleAutoProvider.Provider, true, staleAutoProvider.LatencyMs);
            }

            var autoFallback = ResolveProviderSettings(OnionHopConnectOptions.DnsProviderCloudflare, options);
            log("DoH auto-selection probe failed for all built-in providers. Falling back to Cloudflare.");
            return new DohResolutionResult(autoFallback, requestedProvider, OnionHopConnectOptions.DnsProviderCloudflare, true, -1);
        }

        var requestedCandidate = new DohProviderCandidate(requestedProvider, ResolveProviderSettings(requestedProvider, options));
        var requestedProbe = await ProbeCandidateAsync(requestedCandidate, ProbeSamplesPerCandidate, token).ConfigureAwait(false);
        if (requestedProbe.Healthy)
        {
            return new DohResolutionResult(requestedCandidate.Settings, requestedProvider, requestedProvider, false, requestedProbe.LatencyMs);
        }

        var fallbackCandidates = string.Equals(requestedProvider, OnionHopConnectOptions.DnsProviderCustom, StringComparison.Ordinal)
            ? GetBuiltInCandidates()
            : GetBuiltInCandidates()
                .Where(candidate => !string.Equals(candidate.Provider, requestedProvider, StringComparison.Ordinal))
                .ToList();

        var fallbackProbe = await SelectFastestHealthyCandidateAsync(fallbackCandidates, token).ConfigureAwait(false);
        if (fallbackProbe is { } bestFallback)
        {
            UpdateAutoProviderCache(bestFallback.Provider, bestFallback.LatencyMs);
            var reason = string.IsNullOrWhiteSpace(requestedProbe.Error) ? "probe failed" : requestedProbe.Error;
            log($"DoH provider '{requestedProvider}' {reason}. Falling back to {bestFallback.Provider} ({bestFallback.LatencyMs} ms).");
            return new DohResolutionResult(bestFallback.Settings, requestedProvider, bestFallback.Provider, true, bestFallback.LatencyMs);
        }

        log($"DoH provider '{requestedProvider}' probe failed and no fallback provider responded. Using requested provider anyway.");
        return new DohResolutionResult(requestedCandidate.Settings, requestedProvider, requestedProvider, false, -1);
    }

    private static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return OnionHopConnectOptions.DnsProviderAuto;
        }

        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderCloudflare, StringComparison.OrdinalIgnoreCase))
        {
            return OnionHopConnectOptions.DnsProviderCloudflare;
        }

        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderGoogle, StringComparison.OrdinalIgnoreCase))
        {
            return OnionHopConnectOptions.DnsProviderGoogle;
        }

        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderQuad9, StringComparison.OrdinalIgnoreCase))
        {
            return OnionHopConnectOptions.DnsProviderQuad9;
        }

        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderAdGuard, StringComparison.OrdinalIgnoreCase))
        {
            return OnionHopConnectOptions.DnsProviderAdGuard;
        }

        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderMullvad, StringComparison.OrdinalIgnoreCase))
        {
            return OnionHopConnectOptions.DnsProviderMullvad;
        }

        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderOpenDns, StringComparison.OrdinalIgnoreCase))
        {
            return OnionHopConnectOptions.DnsProviderOpenDns;
        }

        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderCustom, StringComparison.OrdinalIgnoreCase))
        {
            return OnionHopConnectOptions.DnsProviderCustom;
        }

        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderAuto, StringComparison.OrdinalIgnoreCase))
        {
            return OnionHopConnectOptions.DnsProviderAuto;
        }

        return OnionHopConnectOptions.DnsProviderAuto;
    }

    private static DohSettings ResolveProviderSettings(string provider, OnionHopConnectOptions? options)
    {
        if (string.Equals(provider, OnionHopConnectOptions.DnsProviderCustom, StringComparison.Ordinal))
        {
            var normalizedHost = NormalizeDohHost(options?.CustomDohHost);
            return new DohSettings(normalizedHost.Server, normalizedHost.Port, NormalizeDohPath(options?.CustomDohPath));
        }

        var server = provider switch
        {
            OnionHopConnectOptions.DnsProviderGoogle => "dns.google",
            OnionHopConnectOptions.DnsProviderQuad9 => "dns.quad9.net",
            OnionHopConnectOptions.DnsProviderAdGuard => "dns.adguard.com",
            OnionHopConnectOptions.DnsProviderMullvad => "dns.mullvad.net",
            OnionHopConnectOptions.DnsProviderOpenDns => "doh.opendns.com",
            _ => "cloudflare-dns.com"
        };

        return new DohSettings(server, DefaultPort, DefaultPath);
    }

    private static IReadOnlyList<DohProviderCandidate> GetBuiltInCandidates()
    {
        return
        [
            new(OnionHopConnectOptions.DnsProviderCloudflare, ResolveProviderSettings(OnionHopConnectOptions.DnsProviderCloudflare, null)),
            new(OnionHopConnectOptions.DnsProviderQuad9, ResolveProviderSettings(OnionHopConnectOptions.DnsProviderQuad9, null)),
            new(OnionHopConnectOptions.DnsProviderAdGuard, ResolveProviderSettings(OnionHopConnectOptions.DnsProviderAdGuard, null)),
            new(OnionHopConnectOptions.DnsProviderMullvad, ResolveProviderSettings(OnionHopConnectOptions.DnsProviderMullvad, null)),
            new(OnionHopConnectOptions.DnsProviderOpenDns, ResolveProviderSettings(OnionHopConnectOptions.DnsProviderOpenDns, null)),
            new(OnionHopConnectOptions.DnsProviderGoogle, ResolveProviderSettings(OnionHopConnectOptions.DnsProviderGoogle, null))
        ];
    }

    private static async Task<DohProbeResult?> SelectFastestHealthyCandidateAsync(IEnumerable<DohProviderCandidate> candidates, CancellationToken token)
    {
        var candidateList = candidates.ToList();
        if (candidateList.Count == 0)
        {
            return null;
        }

        var probeTasks = candidateList.Select(candidate => ProbeCandidateAsync(candidate, ProbeSamplesPerCandidate, token)).ToArray();

        var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);
        var healthy = results
            .Where(result => result.Healthy)
            .OrderBy(result => result.LatencyMs)
            .ToList();

        return healthy.Count > 0 ? healthy[0] : null;
    }

    private static async Task<DohProbeResult> ProbeCandidateAsync(DohProviderCandidate candidate, int sampleCount, CancellationToken token)
    {
        var latencies = new List<long>(Math.Max(sampleCount, 1));
        string? lastError = null;

        for (var attempt = 0; attempt < Math.Max(sampleCount, 1); attempt++)
        {
            var probe = await ProbeCandidateOnceAsync(candidate, token).ConfigureAwait(false);
            if (probe.Healthy && probe.LatencyMs >= 0)
            {
                latencies.Add(probe.LatencyMs);
                continue;
            }

            lastError = probe.Error;
        }

        if (latencies.Count == 0)
        {
            return new DohProbeResult(candidate.Provider, candidate.Settings, false, -1, lastError ?? "unreachable");
        }

        return new DohProbeResult(candidate.Provider, candidate.Settings, true, ComputeMedianLatency(latencies), null);
    }

    private static async Task<DohProbeResult> ProbeCandidateOnceAsync(DohProviderCandidate candidate, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var sw = Stopwatch.StartNew();
            var path = NormalizeDohPath(candidate.Settings.Path);
            var uriBuilder = new UriBuilder(Uri.UriSchemeHttps, candidate.Settings.Server, candidate.Settings.Port, path)
            {
                Query = $"dns={ProbeQuery}"
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            request.Headers.TryAddWithoutValidation("Accept", "application/dns-message");

            using var response = await ProbeHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                return new DohProbeResult(candidate.Provider, candidate.Settings, true, sw.ElapsedMilliseconds, null);
            }

            return new DohProbeResult(candidate.Provider, candidate.Settings, false, sw.ElapsedMilliseconds, $"returned HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            return new DohProbeResult(candidate.Provider, candidate.Settings, false, -1, "timed out");
        }
        catch (Exception ex)
        {
            return new DohProbeResult(candidate.Provider, candidate.Settings, false, -1, ex.Message);
        }
    }

    private static long ComputeMedianLatency(IReadOnlyList<long> latencies)
    {
        if (latencies.Count == 0)
        {
            return -1;
        }

        var ordered = latencies.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        if (ordered.Length % 2 == 1)
        {
            return ordered[middle];
        }

        return (ordered[middle - 1] + ordered[middle]) / 2;
    }

    private static bool TryGetCachedAutoProvider(bool requireFresh, out DohAutoProviderCacheSnapshot snapshot)
    {
        lock (AutoProviderCacheLock)
        {
            EnsureAutoProviderCacheLoadedLocked();
            if (_autoProviderCache is not { } cache)
            {
                snapshot = default;
                return false;
            }

            if (!IsKnownBuiltInProvider(cache.Provider))
            {
                snapshot = default;
                return false;
            }

            if (requireFresh && DateTimeOffset.UtcNow - cache.UpdatedUtc > AutoProviderCacheTtl)
            {
                snapshot = default;
                return false;
            }

            snapshot = cache;
            return true;
        }
    }

    private static void UpdateAutoProviderCache(string provider, long latencyMs)
    {
        if (!IsKnownBuiltInProvider(provider))
        {
            return;
        }

        lock (AutoProviderCacheLock)
        {
            _autoProviderCache = new DohAutoProviderCacheSnapshot(provider, latencyMs, DateTimeOffset.UtcNow);
            _autoProviderCacheLoaded = true;
            PersistAutoProviderCacheLocked();
        }
    }

    private static void TriggerBackgroundAutoProviderRefresh(Action<string> log)
    {
        if (Interlocked.CompareExchange(ref _autoProviderRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        if (!TryMarkAutoProviderRefreshStart())
        {
            Interlocked.Exchange(ref _autoProviderRefreshInFlight, 0);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var refreshCts = new CancellationTokenSource(AutoProviderBackgroundRefreshTimeout);
                var best = await SelectFastestHealthyCandidateAsync(GetBuiltInCandidates(), refreshCts.Token).ConfigureAwait(false);
                if (best is not { } refreshedBest)
                {
                    return;
                }

                TryGetCachedAutoProvider(requireFresh: false, out var previousCache);
                UpdateAutoProviderCache(refreshedBest.Provider, refreshedBest.LatencyMs);

                if (string.IsNullOrWhiteSpace(previousCache.Provider) ||
                    !string.Equals(previousCache.Provider, refreshedBest.Provider, StringComparison.Ordinal) ||
                    Math.Abs(previousCache.LatencyMs - refreshedBest.LatencyMs) >= 150)
                {
                    log($"DoH auto background refresh selected {refreshedBest.Provider} ({refreshedBest.LatencyMs} ms).");
                }
            }
            catch
            {
                // background refresh is best-effort only
            }
            finally
            {
                Interlocked.Exchange(ref _autoProviderRefreshInFlight, 0);
            }
        });
    }

    private static bool TryMarkAutoProviderRefreshStart()
    {
        lock (AutoProviderCacheLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastAutoProviderRefreshUtc < AutoProviderRefreshThrottle)
            {
                return false;
            }

            _lastAutoProviderRefreshUtc = now;
            return true;
        }
    }

    private static void EnsureAutoProviderCacheLoadedLocked()
    {
        if (_autoProviderCacheLoaded)
        {
            return;
        }

        _autoProviderCacheLoaded = true;

        try
        {
            if (!File.Exists(AutoProviderCachePath))
            {
                return;
            }

            var json = File.ReadAllText(AutoProviderCachePath);
            var persisted = JsonSerializer.Deserialize<PersistedDohAutoProviderCache>(json);
            if (persisted is null ||
                string.IsNullOrWhiteSpace(persisted.Provider) ||
                !IsKnownBuiltInProvider(persisted.Provider))
            {
                return;
            }

            _autoProviderCache = new DohAutoProviderCacheSnapshot(
                persisted.Provider,
                persisted.LatencyMs,
                persisted.UpdatedUtc);
        }
        catch
        {
            // ignore invalid cache files
        }
    }

    private static void PersistAutoProviderCacheLocked()
    {
        if (_autoProviderCache is not { } cache)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(AutoProviderCachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new PersistedDohAutoProviderCache
            {
                Provider = cache.Provider,
                LatencyMs = cache.LatencyMs,
                UpdatedUtc = cache.UpdatedUtc
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(AutoProviderCachePath, json);
        }
        catch
        {
            // cache persistence is best-effort only
        }
    }

    private static bool IsKnownBuiltInProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        return string.Equals(provider, OnionHopConnectOptions.DnsProviderCloudflare, StringComparison.Ordinal)
               || string.Equals(provider, OnionHopConnectOptions.DnsProviderGoogle, StringComparison.Ordinal)
               || string.Equals(provider, OnionHopConnectOptions.DnsProviderQuad9, StringComparison.Ordinal)
               || string.Equals(provider, OnionHopConnectOptions.DnsProviderAdGuard, StringComparison.Ordinal)
               || string.Equals(provider, OnionHopConnectOptions.DnsProviderMullvad, StringComparison.Ordinal)
               || string.Equals(provider, OnionHopConnectOptions.DnsProviderOpenDns, StringComparison.Ordinal);
    }

    private static string DescribeAge(DateTimeOffset updatedUtc)
    {
        var age = DateTimeOffset.UtcNow - updatedUtc;
        if (age.TotalMinutes < 1)
        {
            return "<1m";
        }

        if (age.TotalHours < 1)
        {
            return $"{Math.Floor(age.TotalMinutes)}m";
        }

        return $"{Math.Floor(age.TotalHours)}h";
    }

    private static DohSettings NormalizeDohHost(string? rawHost)
    {
        var host = (rawHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return new DohSettings("cloudflare-dns.com", DefaultPort, DefaultPath);
        }

        // Allow users to paste a full URL or host[:port].
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return new DohSettings(uri.Host, uri.IsDefaultPort ? DefaultPort : uri.Port, DefaultPath);
        }

        if (Uri.TryCreate($"https://{host}", UriKind.Absolute, out uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return new DohSettings(uri.Host, uri.IsDefaultPort ? DefaultPort : uri.Port, DefaultPath);
        }

        return new DohSettings(host, DefaultPort, DefaultPath);
    }

    private static string NormalizeDohPath(string? rawPath)
    {
        var path = (rawPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultPath;
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        return path;
    }

    private readonly record struct DohProviderCandidate(string Provider, DohSettings Settings);
    private readonly record struct DohProbeResult(string Provider, DohSettings Settings, bool Healthy, long LatencyMs, string? Error);
    private readonly record struct DohAutoProviderCacheSnapshot(string Provider, long LatencyMs, DateTimeOffset UpdatedUtc);

    private sealed class PersistedDohAutoProviderCache
    {
        public string Provider { get; set; } = string.Empty;
        public long LatencyMs { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}
