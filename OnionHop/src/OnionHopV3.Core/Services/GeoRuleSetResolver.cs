using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Pre-flight check for the remote sing-box geo rule-sets referenced from the VPN config (issue #68).
/// sing-box treats a rule-set it cannot download as FATAL and refuses to start, so a single mistyped
/// geosite category (e.g. "ir", which SagerNet only publishes as "category-ir") used to take the whole
/// connection down. Before the config is built, each rule-set is verified upstream: a plain-name miss
/// is retried with the "category-" prefix and silently upgraded when that exists, and entries that
/// still cannot be found are dropped with a warning instead of breaking the start. Network trouble
/// during the probe keeps the entry (sing-box may have the rule-set cached on disk already).
/// </summary>
internal static class GeoRuleSetResolver
{
    private const string GeositeUrlFormat = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-{0}.srs";
    private const string GeoipUrlFormat = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-{0}.srs";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    // Upstream availability barely changes; remember verdicts for the process lifetime so reconnects
    // do not re-probe. Geosite values hold the resolved name ("ir" -> "category-ir") or null when the
    // category is unavailable; geoip values are a plain exists flag.
    private static readonly ConcurrentDictionary<string, string?> GeositeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> GeoipCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps each requested geosite category to the name that actually exists upstream, dropping the
    /// ones that cannot be resolved. Returns the input reference untouched when there is nothing to do.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> ResolveGeositeCategoriesAsync(
        IReadOnlyList<string>? categories,
        Action<string> log,
        CancellationToken token)
    {
        var normalized = VpnConfigBuilder.NormalizeGeositeCategories(categories);
        if (normalized.Count == 0)
        {
            return categories;
        }

        var resolved = new List<string>(normalized.Count);
        foreach (var category in normalized)
        {
            token.ThrowIfCancellationRequested();
            if (!GeositeCache.TryGetValue(category, out var name))
            {
                name = await ResolveGeositeNameAsync(category, token).ConfigureAwait(false);
                GeositeCache[category] = name;
            }

            if (name == null)
            {
                log($"Geosite category '{category}' does not exist upstream and will be skipped for this connection. See https://github.com/SagerNet/sing-geosite for available categories.");
            }
            else
            {
                if (!string.Equals(name, category, StringComparison.Ordinal))
                {
                    log($"Geosite category '{category}' does not exist upstream; using '{name}' instead.");
                }

                resolved.Add(name);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Drops country codes whose geoip rule-set does not exist upstream (a typo would otherwise make
    /// the sing-box start FATAL). Returns the input reference untouched when there is nothing to do.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> FilterCountryCodesAsync(
        IReadOnlyList<string>? countryCodes,
        Action<string> log,
        CancellationToken token)
    {
        var normalized = VpnConfigBuilder.NormalizeCountryCodes(countryCodes);
        if (normalized.Count == 0)
        {
            return countryCodes;
        }

        var resolved = new List<string>(normalized.Count);
        foreach (var code in normalized)
        {
            token.ThrowIfCancellationRequested();
            if (!GeoipCache.TryGetValue(code, out var exists))
            {
                // Unknown (network trouble) counts as existing - never drop user rules just because
                // the probe could not run; a definitive 404 is what we are protecting against.
                exists = await ProbeAsync(string.Format(CultureInfo.InvariantCulture, GeoipUrlFormat, code), token).ConfigureAwait(false) != false;
                GeoipCache[code] = exists;
            }

            if (exists)
            {
                resolved.Add(code);
            }
            else
            {
                log($"Country code '{code}' has no geoip rule-set upstream and will be skipped for this connection.");
            }
        }

        return resolved;
    }

    private static async Task<string?> ResolveGeositeNameAsync(string category, CancellationToken token)
    {
        var plain = await ProbeAsync(string.Format(CultureInfo.InvariantCulture, GeositeUrlFormat, category), token).ConfigureAwait(false);
        if (plain != false)
        {
            // Exists, or could not be checked (fail open - see class remarks).
            return category;
        }

        if (category.StartsWith("category-", StringComparison.Ordinal))
        {
            return null;
        }

        // SagerNet publishes many country/topic collections only as "category-<name>" (issue #68:
        // "ir" is really "category-ir"). Only a verified hit is substituted; the plain name is a
        // confirmed 404 at this point, so an unverifiable alias is dropped rather than gambled on.
        var prefixed = "category-" + category;
        var aliasExists = await ProbeAsync(string.Format(CultureInfo.InvariantCulture, GeositeUrlFormat, prefixed), token).ConfigureAwait(false);
        return aliasExists == true ? prefixed : null;
    }

    /// <summary>true = exists, false = definitive 404, null = could not determine.</summary>
    private static async Task<bool?> ProbeAsync(string url, CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(ProbeTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await HttpClientFactory.Default
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            return response.StatusCode == HttpStatusCode.NotFound ? false : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Timeout or network error: unknown.
            return null;
        }
    }
}
