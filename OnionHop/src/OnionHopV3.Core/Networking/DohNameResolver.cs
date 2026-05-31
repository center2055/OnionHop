using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Networking;

/// <summary>
/// Resolves hostnames to IP addresses over DNS-over-HTTPS, reaching the DoH resolvers by their
/// literal IP (so the resolution itself never touches the system resolver). This lets OnionHop's own
/// HTTPS fetches (bridges, relay directory, node DB, update checks, IP lookups) keep working in
/// regions where the ISP's DNS is poisoned or blocked - the exact situation where a user can connect
/// with Orbot but OnionHop's pre-connect fetches fail with "the SSL connection could not be
/// established". Results are cached briefly; callers should fall back to system DNS if this returns
/// nothing, so a fully-blocked DoH path is no worse than before.
/// </summary>
internal static class DohNameResolver
{
    // Public DoH resolvers addressed by IP. Their TLS certificates include these IPs in the SAN list,
    // so requesting https://<ip>/dns-query validates without needing to resolve the resolver's own
    // hostname. Multiple providers give resilience if one is blocked.
    private static readonly string[] ResolverEndpoints =
    [
        "https://1.1.1.1/dns-query",       // Cloudflare
        "https://1.0.0.1/dns-query",       // Cloudflare (secondary)
        "https://8.8.8.8/dns-query",       // Google
        "https://8.8.4.4/dns-query",       // Google (secondary)
        "https://9.9.9.9/dns-query",       // Quad9
        "https://149.112.112.112/dns-query" // Quad9 (secondary)
    ];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Dedicated client that connects straight to the resolver IP (its host is already an IP, so the
    // ConnectCallback below just dials it - no recursion into DoH).
    private static readonly Lazy<HttpClient> DohClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        var client = new HttpClient(handler) { Timeout = QueryTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");
        return client;
    });

    /// <summary>
    /// Resolve <paramref name="host"/> to one or more IP addresses via DoH. Returns an empty list if
    /// DoH resolution fails for every provider (the caller should then fall back to system DNS).
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _))
        {
            return Array.Empty<IPAddress>();
        }

        if (Cache.TryGetValue(host, out var cached) && !cached.IsExpired)
        {
            return cached.Addresses;
        }

        foreach (var endpoint in ResolverEndpoints)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var addresses = await QueryProviderAsync(endpoint, host, token).ConfigureAwait(false);
                if (addresses.Count > 0)
                {
                    Cache[host] = new CacheEntry(addresses, DateTimeOffset.UtcNow + CacheTtl);
                    return addresses;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next resolver.
            }
        }

        return Array.Empty<IPAddress>();
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryProviderAsync(string endpoint, string host, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(QueryTimeout);

        var results = new List<IPAddress>();
        foreach (var queryType in new ushort[] { 1, 28 }) // A, then AAAA
        {
            var query = BuildQuery(host, queryType);
            var dnsParam = Base64Url(query);
            var uri = $"{endpoint}?dns={dnsParam}";

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept", "application/dns-message");

            using var response = await DohClient.Value
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var payload = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            results.AddRange(ParseAddresses(payload));
        }

        return results.Distinct().ToList();
    }

    internal static byte[] BuildQueryForTest(string host, ushort queryType) => BuildQuery(host, queryType);

    internal static IReadOnlyList<IPAddress> ParseAddressesForTest(byte[] response) => ParseAddresses(response).ToList();

    private static byte[] BuildQuery(string host, ushort queryType)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var nameLength = labels.Sum(label => label.Length + 1) + 1; // length byte per label + root
        var message = new byte[12 + nameLength + 4];

        // Header: fixed ID (0x0000 is fine for DoH GET, which is cacheable/stateless), RD=1.
        message[2] = 0x01; // flags high byte: RD
        message[5] = 0x01; // QDCOUNT = 1

        var offset = 12;
        foreach (var label in labels)
        {
            message[offset++] = (byte)label.Length;
            foreach (var c in label)
            {
                message[offset++] = (byte)c;
            }
        }

        message[offset++] = 0x00; // root label
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset), queryType);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset), 1); // QCLASS = IN
        return message;
    }

    private static IEnumerable<IPAddress> ParseAddresses(byte[] response)
    {
        if (response.Length < 12)
        {
            yield break;
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4));
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6));
        var offset = 12;

        // Skip the question section.
        for (var i = 0; i < qdCount; i++)
        {
            offset = SkipName(response, offset);
            offset += 4; // QTYPE + QCLASS
            if (offset > response.Length)
            {
                yield break;
            }
        }

        for (var i = 0; i < anCount; i++)
        {
            offset = SkipName(response, offset);
            if (offset + 10 > response.Length)
            {
                yield break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8));
            var rdataOffset = offset + 10;
            if (rdataOffset + rdLength > response.Length)
            {
                yield break;
            }

            if (type == 1 && rdLength == 4)
            {
                yield return new IPAddress(response.AsSpan(rdataOffset, 4).ToArray());
            }
            else if (type == 28 && rdLength == 16)
            {
                yield return new IPAddress(response.AsSpan(rdataOffset, 16).ToArray());
            }

            offset = rdataOffset + rdLength;
        }
    }

    // Advance past a DNS name, honoring compression pointers (0xC0). Returns the offset just after
    // the name in the record stream (for a pointer that's offset+2).
    private static int SkipName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            var length = data[offset];
            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2; // compression pointer ends the name
            }

            offset += length + 1;
        }

        return offset;
    }

    private static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private readonly record struct CacheEntry(IReadOnlyList<IPAddress> Addresses, DateTimeOffset ExpiresUtc)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresUtc;
    }
}
