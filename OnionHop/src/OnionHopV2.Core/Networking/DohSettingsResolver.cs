using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV2.Core.Networking;

internal readonly record struct DohSettings(string Server, int Port, string Path);
internal readonly record struct DohResolutionResult(DohSettings Settings, string RequestedProvider, string EffectiveProvider, bool UsedFallback, long ProbeLatencyMs);

internal static class DohSettingsResolver
{
    private const string DefaultPath = "/dns-query";
    private const int DefaultPort = 443;
    private const string ProbeQuery = "AAABAAABAAAAAAAAB2V4YW1wbGUDY29tAAABAAE";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient ProbeHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

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
            var bestBuiltIn = await SelectFastestHealthyCandidateAsync(GetBuiltInCandidates(), token).ConfigureAwait(false);
            if (bestBuiltIn is { } best)
            {
                log($"DoH auto-selected {best.Provider} ({best.LatencyMs} ms).");
                return new DohResolutionResult(best.Settings, requestedProvider, best.Provider, false, best.LatencyMs);
            }

            var autoFallback = ResolveProviderSettings(OnionHopConnectOptions.DnsProviderCloudflare, options);
            log("DoH auto-selection probe failed for all built-in providers. Falling back to Cloudflare.");
            return new DohResolutionResult(autoFallback, requestedProvider, OnionHopConnectOptions.DnsProviderCloudflare, true, -1);
        }

        var requestedCandidate = new DohProviderCandidate(requestedProvider, ResolveProviderSettings(requestedProvider, options));
        var requestedProbe = await ProbeCandidateAsync(requestedCandidate, token).ConfigureAwait(false);
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

        var probeTasks = candidateList
            .Select(candidate => ProbeCandidateAsync(candidate, token))
            .ToArray();

        var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);
        var healthy = results
            .Where(result => result.Healthy)
            .OrderBy(result => result.LatencyMs)
            .ToList();

        return healthy.Count > 0 ? healthy[0] : null;
    }

    private static async Task<DohProbeResult> ProbeCandidateAsync(DohProviderCandidate candidate, CancellationToken token)
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
}
