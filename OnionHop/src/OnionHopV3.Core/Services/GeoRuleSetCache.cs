using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

/// <summary>
/// On-disk cache for the sing-box geo rule-sets (issue #68 follow-up). Remote rule-sets are fetched
/// from raw.githubusercontent.com, which is blocked in exactly the countries these rules matter for -
/// and sing-box re-downloads them on every start. This cache stores the .srs files under app data
/// (like the bridge cache), lets the VPN config reference them as local rule-sets, and refreshes
/// them through Tor's SOCKS proxy once connected, so after the first successful connect the geo
/// rules work with no GitHub dependency at all.
/// </summary>
internal static class GeoRuleSetCache
{
    private const string GeositeUrlFormat = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/{0}.srs";
    private const string GeoipUrlFormat = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/{0}.srs";
    private static readonly TimeSpan RefreshAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    internal static string CacheDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnionHop", "geo");

    /// <summary>Path of the cached .srs for a rule-set tag ("geosite-category-ir"), or null.</summary>
    public static string? TryGetLocalPath(string tag)
    {
        try
        {
            var path = Path.Combine(CacheDirectory, tag + ".srs");
            var info = new FileInfo(path);
            return info.Exists && info.Length > 3 ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads missing/stale rule-sets for the given tags, optionally through Tor's SOCKS proxy.
    /// A plain geosite name that 404s upstream is retried with the "category-" prefix and cached
    /// under that name (matching GeoRuleSetResolver's aliasing). Failures are logged and skipped;
    /// this is a background best-effort job.
    /// </summary>
    public static async Task RefreshAsync(
        IEnumerable<string> tags,
        int? socksPort,
        Action<string> log,
        CancellationToken token)
    {
        var wanted = tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        await RefreshLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            HttpClient? socksClient = null;
            try
            {
                if (socksPort is > 0)
                {
                    socksClient = Socks5HttpClient.Create("127.0.0.1", socksPort.Value, FetchTimeout);
                }

                var client = socksClient ?? HttpClientFactory.Default;
                var via = socksClient != null ? $"Tor SOCKS 127.0.0.1:{socksPort}" : "direct connection";
                var fetched = 0;

                foreach (var tag in wanted)
                {
                    token.ThrowIfCancellationRequested();
                    if (IsFresh(tag))
                    {
                        continue;
                    }

                    if (await TryFetchTagAsync(client, tag, log, token).ConfigureAwait(false))
                    {
                        fetched++;
                    }
                }

                if (fetched > 0)
                {
                    log($"Geo rule-sets: cached {fetched} list(s) via {via}; the next connect uses them offline.");
                }
            }
            finally
            {
                socksClient?.Dispose();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutting down; leave whatever was cached so far.
        }
        catch (Exception ex)
        {
            log($"Geo rule-set refresh failed: {ex.Message}");
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private static bool IsFresh(string tag)
    {
        try
        {
            var info = new FileInfo(Path.Combine(CacheDirectory, tag + ".srs"));
            return info.Exists && info.Length > 3 && DateTime.UtcNow - info.LastWriteTimeUtc < RefreshAge;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryFetchTagAsync(HttpClient client, string tag, Action<string> log, CancellationToken token)
    {
        var (downloaded, notFound) = await TryDownloadAsync(client, tag, token).ConfigureAwait(false);
        if (downloaded)
        {
            return true;
        }

        // Mirror the resolver's aliasing: a plain geosite country name may only exist upstream as
        // "category-<name>". Cache it under the aliased tag - the config builder asks for that tag.
        if (notFound &&
            tag.StartsWith("geosite-", StringComparison.Ordinal) &&
            !tag.StartsWith("geosite-category-", StringComparison.Ordinal))
        {
            var aliased = "geosite-category-" + tag["geosite-".Length..];
            (downloaded, _) = await TryDownloadAsync(client, aliased, token).ConfigureAwait(false);
            if (downloaded)
            {
                log($"Geo rule-sets: '{tag}' exists upstream as '{aliased}'; cached under that name.");
                return true;
            }
        }

        return false;
    }

    /// <summary>(downloaded, definitive 404)</summary>
    private static async Task<(bool Downloaded, bool NotFound)> TryDownloadAsync(HttpClient client, string tag, CancellationToken token)
    {
        var urlFormat = tag.StartsWith("geoip-", StringComparison.Ordinal) ? GeoipUrlFormat : GeositeUrlFormat;
        var url = string.Format(CultureInfo.InvariantCulture, urlFormat, tag);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(FetchTimeout);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (false, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, false);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            // sing-box binary rule-sets start with the "SRS" magic; anything else (a block page,
            // an HTML error) must not poison the cache.
            if (bytes.Length < 4 || bytes[0] != (byte)'S' || bytes[1] != (byte)'R' || bytes[2] != (byte)'S')
            {
                return (false, false);
            }

            var finalPath = Path.Combine(CacheDirectory, tag + ".srs");
            var tempPath = finalPath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, token).ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: true);
            return (true, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (false, false);
        }
    }
}
