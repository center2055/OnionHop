using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

public sealed record BridgeFetchResult(IReadOnlyList<string> Lines, string SourceUrl);

/// <summary>
/// Fetches curated bridge lists from the OnionHop bridge collector using a prioritized list of
/// mirrors. GitHub raw is tried first, then GitHub Pages, then any self-hosted website endpoint.
/// Using several independent mirrors is deliberate: GitHub's raw host is intermittently blocked in
/// censored regions, so a downstream client should always have a fallback. The first mirror that
/// returns a non-empty list wins.
/// </summary>
public static class BridgeSourceService
{
    /// <summary>
    /// Mirror base URLs (must end with a slash) pointing at the directory that holds the bridge
    /// files. Order = priority. Add a self-hosted endpoint as an extra censorship-resistant
    /// source — ideally behind a CDN, fetched over HTTPS. Empty/whitespace entries are skipped.
    /// </summary>
    public static readonly IReadOnlyList<string> MirrorBases = new[]
    {
        "https://raw.githubusercontent.com/center2055/OnionHop-Bridges-Collector/main/bridge/",
        "https://center2055.github.io/OnionHop-Bridges-Collector/bridge/",
        // Self-hosted website endpoint (recommended extra source for blocked regions). Example:
        // "https://your-onionhop-domain.example/bridges/"
        ""
    };

    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "Tested & Active",
        "Fresh (72h)",
        "Full Archive"
    };

    /// <summary>
    /// Transport choices offered in the scanner. The collector hosts curated lists for
    /// <see cref="CollectorTransports"/>; snowflake/meek/conjure use built-in default bridge lines
    /// (they have no per-region pool to curate). "All" aggregates every source.
    /// </summary>
    public static readonly IReadOnlyList<string> Transports = new[]
    {
        "All",
        "obfs4",
        "webtunnel",
        "vanilla",
        "snowflake",
        "meek-azure",
        "conjure",
        "dnstt"
    };

    /// <summary>Transports the collector publishes curated, region-tested files for.</summary>
    public static readonly IReadOnlyList<string> CollectorTransports = new[]
    {
        "obfs4",
        "webtunnel",
        "vanilla"
    };

    /// <summary>
    /// Built-in default bridge lines for transports the collector does not host. These mirror the
    /// bridges shipped with Tor Browser; the listed IP is a placeholder (the transport reaches Tor
    /// via a broker / domain fronting). The scanner probes the broker/front host for these.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BuiltInBridges =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["snowflake"] = new[]
            {
                "snowflake 192.0.2.3:80 2B280B23E1107BB62ABFC40DDCC8824814F80A72 fingerprint=2B280B23E1107BB62ABFC40DDCC8824814F80A72 url=https://1098762253.rsc.cdn77.org/ fronts=www.cdn77.com,www.phpmyadmin.net ice=stun:stun.l.google.com:19302,stun:stun.antisip.com:3478,stun:stun.bluesip.net:3478,stun:stun.dus.net:3478,stun:stun.epygi.com:3478 utls-imitate=hellorandomizedalpn",
                "snowflake 192.0.2.4:80 8838024498816A039FCBBAB14E6F40A0843051FA fingerprint=8838024498816A039FCBBAB14E6F40A0843051FA url=https://1098762253.rsc.cdn77.org/ fronts=www.cdn77.com,www.phpmyadmin.net ice=stun:stun.l.google.com:19302,stun:stun.antisip.com:3478,stun:stun.bluesip.net:3478,stun:stun.dus.net:3478,stun:stun.epygi.com:3478 utls-imitate=hellorandomizedalpn"
            },
            ["meek-azure"] = new[]
            {
                "meek_lite 192.0.2.20:80 97700DFE9F483596DDA6264C4D7DF7641E1E39CE url=https://meek.azureedge.net/ front=ajax.aspnetcdn.com"
            },
            ["conjure"] = new[]
            {
                "conjure 192.0.2.3:80 2B280B23E1107BB62ABFC40DDCC8824814F80A72 url=https://registration.refraction.network/api fronts=cdn.sstatic.net,assets.cloud.censys.io transport=min"
            },
            // dnstt tunnels Tor over DNS (DoH/DoT); it gets through where everything but DNS is
            // blocked. Placeholder IP (reached via the DoH resolver + tunnel domain). Public bridges
            // distributed for Iran via the Tor forum.
            ["dnstt"] = new[]
            {
                "dnstt 192.0.2.4:1 A998F319ADB60EE344540EC4B21524CC484F96BE doh=https://dns.google/dns-query pubkey=241169008830694749fe96bb070c4855c5bb5b9c47b3833ed7d88521ba30a43f domain=t.ruhnama.net",
                "dnstt 192.0.2.4:2 80EEFA4F4875ED2B7B5A86DF2D7588AD32E29F15 doh=https://dns.google/dns-query pubkey=a2fb71077eeaa54a02cda7a90be306af5d299ab21822a8b277d4eacbc9168631 domain=t2.bypasscensorship.org",
                "dnstt 192.0.2.4:3 74D409BED3E2F881F365543A72C8F079CB84FFEB doh=https://dns.google/dns-query pubkey=c596c458fc3453dc40903ab235f5854a2609831075640c4c5584f76de05b8271 domain=t.arkadag.org"
            }
        };

    public static readonly IReadOnlyList<string> IpVersions = new[]
    {
        "IPv4",
        "IPv6"
    };

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHopV3-BridgeSource/1.0");
        return client;
    });

    /// <summary>
    /// Build the collector file name for a category/transport/IP-version combination. Matches the
    /// collector repo's published layout, e.g. obfs4.txt, obfs4_ipv6.txt, obfs4_72h.txt,
    /// obfs4_ipv6_72h.txt, obfs4_tested.txt, obfs4_ipv6_tested.txt.
    /// </summary>
    public static string BuildFileName(string category, string transport, string ipVersion)
    {
        var t = (transport ?? "obfs4").Trim().ToLowerInvariant();
        var ipv6 = string.Equals(ipVersion?.Trim(), "IPv6", StringComparison.OrdinalIgnoreCase) ? "_ipv6" : string.Empty;
        var catSuffix = category?.Trim() switch
        {
            "Tested & Active" => "_tested",
            "Fresh (72h)" => "_72h",
            _ => string.Empty // Full Archive
        };

        return $"{t}{ipv6}{catSuffix}.txt";
    }

    /// <summary>
    /// Try each mirror in order; return the lines from the first that responds with a non-empty
    /// list. Returns null when every mirror fails (offline / all blocked).
    /// </summary>
    public static async Task<BridgeFetchResult?> FetchAsync(
        string category,
        string transport,
        string ipVersion,
        HttpClient? httpClient,
        Action<string>? log,
        CancellationToken token)
    {
        var normalized = (transport ?? "obfs4").Trim();

        // "All": aggregate every collector list for the chosen category/IP plus the built-in
        // fronted-transport defaults, so a single scan covers every supported bridge type.
        if (string.Equals(normalized, "All", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchAllAsync(category, ipVersion, httpClient, log, token).ConfigureAwait(false);
        }

        // Transports the collector does not host (snowflake/meek/conjure): use built-in defaults.
        if (BuiltInBridges.TryGetValue(normalized, out var builtIn) &&
            !CollectorTransports.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            log?.Invoke($"Using {builtIn.Count} built-in {normalized} bridge line(s) (no collector pool for this transport).");
            return new BridgeFetchResult(builtIn.ToList(), $"built-in:{normalized}");
        }

        var fileName = BuildFileName(category, normalized, ipVersion);
        var client = httpClient ?? SharedClient.Value;

        foreach (var rawBase in MirrorBases)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawBase))
            {
                continue;
            }

            var baseUrl = rawBase.EndsWith("/", StringComparison.Ordinal) ? rawBase : rawBase + "/";
            var url = baseUrl + fileName;
            try
            {
                using var response = await client.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    log?.Invoke($"Bridge source {url} returned HTTP {(int)response.StatusCode}.");
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var lines = ParseLines(content);
                if (lines.Count == 0)
                {
                    log?.Invoke($"Bridge source {url} returned no usable lines.");
                    continue;
                }

                log?.Invoke($"Fetched {lines.Count} bridge line(s) from {url}.");
                // Persist the fetched list so it survives an app restart and is available offline
                // (the next scan can fall back to it when every mirror is unreachable).
                TryWriteCache(fileName, content);
                return new BridgeFetchResult(lines, url);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Bridge source {url} failed: {ex.Message}");
            }
        }

        // Every mirror failed (offline / all blocked): fall back to the last fetched list saved on disk.
        var cached = TryReadCache(fileName);
        if (cached != null && cached.Count > 0)
        {
            log?.Invoke($"All bridge mirrors unreachable; using {cached.Count} cached {normalized} bridge(s) from the last update.");
            return new BridgeFetchResult(cached, $"cache:{fileName}");
        }

        return null;
    }

    // Saved bridge lists (per category/transport/IP file) so scans work offline and persist across
    // restarts. Kept separate from the bundled curated lists so it is never overwritten on app update.
    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnionHop", "bridge-scan-cache");

    private static void TryWriteCache(string fileName, string content)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(Path.Combine(CacheDir, fileName), content);
        }
        catch
        {
            // Caching is best-effort; never let it break a successful fetch.
        }
    }

    private static IReadOnlyList<string>? TryReadCache(string fileName)
    {
        try
        {
            var path = Path.Combine(CacheDir, fileName);
            return File.Exists(path) ? ParseLines(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Aggregate every supported source: each collector transport for the chosen category/IP version,
    /// plus all built-in fronted-transport defaults. Returns null only when nothing at all resolved.
    /// </summary>
    private static async Task<BridgeFetchResult?> FetchAllAsync(
        string category,
        string ipVersion,
        HttpClient? httpClient,
        Action<string>? log,
        CancellationToken token)
    {
        var aggregate = new List<string>();
        var sources = new List<string>();

        foreach (var transport in CollectorTransports)
        {
            token.ThrowIfCancellationRequested();
            var result = await FetchAsync(category, transport, ipVersion, httpClient, log, token).ConfigureAwait(false);
            if (result != null && result.Lines.Count > 0)
            {
                aggregate.AddRange(result.Lines);
                sources.Add(transport);
            }
        }

        foreach (var builtIn in BuiltInBridges.Values)
        {
            aggregate.AddRange(builtIn);
        }
        sources.AddRange(BuiltInBridges.Keys);

        var deduped = aggregate.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (deduped.Count == 0)
        {
            return null;
        }

        log?.Invoke($"Aggregated {deduped.Count} bridge line(s) across: {string.Join(", ", sources)}.");
        return new BridgeFetchResult(deduped, $"all:{string.Join("+", sources)}");
    }

    private static IReadOnlyList<string> ParseLines(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
