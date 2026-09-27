using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Services;

namespace OnionHopV3.Core.Networking;

internal static class IpLookupService
{
    private static readonly Uri[] TorIpEndpoints =
    [
        new("https://api.ipify.org"),
        new("https://checkip.amazonaws.com"),
        new("https://icanhazip.com")
    ];

    private static readonly Uri[] DirectIpEndpoints =
    [
        new("https://api.ipify.org"),
        new("https://checkip.amazonaws.com"),
        new("https://icanhazip.com"),
        new("https://ifconfig.me/ip")
    ];

    public static async Task<string?> TryFetchTorExitIpAsync(int socksPort, Action<string> log, CancellationToken token)
    {
        try
        {
            using var client = Socks5HttpClient.Create("127.0.0.1", socksPort, TimeSpan.FromSeconds(25));
            return await TryFetchIpFromAnyAsync(client, TorIpEndpoints, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"Tor IP lookup failed: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> TryFetchDirectIpAsync(Action<string> log, CancellationToken token)
    {
        try
        {
            return await TryFetchIpFromAnyAsync(HttpClientFactory.Default, DirectIpEndpoints, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"Direct IP lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// This machine's public IP as seen over a brand-new connection, which is how any other program
    /// would reach the internet right now. The shared client cannot be used for this: a connection it
    /// pooled before the tunnel came up stays bound to the physical interface and keeps leaving with
    /// the real IP, so reusing it would report a leak that is only an old socket. A new socket is
    /// routed the way a browser's next connection would be (#83).
    /// </summary>
    public static async Task<string?> TryFetchIpOverFreshConnectionAsync(Action<string> log, CancellationToken token)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                AutomaticDecompression = DecompressionMethods.All
            };
            using var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");
            return await TryFetchIpFromAnyAsync(client, DirectIpEndpoints, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Tunnel check lookup failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> TryFetchIpFromAnyAsync(HttpClient client, IReadOnlyList<Uri> endpoints, CancellationToken token)
    {
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var response = await client.GetAsync(endpoint, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (TryExtractIp(content, out var ip))
                {
                    return ip;
                }

                if (TryExtractIpFromJson(content, out ip))
                {
                    return ip;
                }
            }
            // Real cancellation propagates; a per-request HTTP timeout also surfaces as
            // OperationCanceledException (TaskCanceledException) and should just move on to the
            // next endpoint instead of aborting the whole lookup (#65).
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try next endpoint.
            }
        }

        return null;
    }

    private static bool TryExtractIpFromJson(string content, out string ip)
    {
        ip = string.Empty;
        var trimmed = content?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "IP", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(prop.Name, "ip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.String &&
                    TryExtractIp(prop.Value.GetString(), out ip))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryExtractIp(string? content, out string ip)
    {
        ip = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.Trim();
        if (IPAddress.TryParse(trimmed, out var parsed))
        {
            ip = parsed.ToString();
            return true;
        }

        foreach (var token in trimmed.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim().Trim(',', '"', '\'', '{', '}', '[', ']', ':');
            if (candidate.Length == 0)
            {
                continue;
            }

            if (IPAddress.TryParse(candidate, out parsed))
            {
                ip = parsed.ToString();
                return true;
            }
        }

        return false;
    }
}
