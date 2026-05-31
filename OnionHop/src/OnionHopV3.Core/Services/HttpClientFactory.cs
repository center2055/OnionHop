using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Networking;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Provides singleton HttpClient instances to avoid socket exhaustion.
/// HttpClient is designed to be reused throughout an application's lifetime.
/// </summary>
internal static class HttpClientFactory
{
    private static readonly Lazy<HttpClient> DefaultClientLazy = new(() =>
    {
        // OnionHop may set the system proxy to a SOCKS endpoint (Tor).
        // .NET's HttpClient does not support SOCKS system proxies, so we explicitly bypass
        // system proxy settings for internal API calls (updates, IP checks, etc.).
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = DohFirstConnectAsync
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");
        return client;
    });

    private static readonly Lazy<HttpClient> LongTimeoutClientLazy = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = DohFirstConnectAsync
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");
        return client;
    });

    /// <summary>
    /// Connect to the request host, resolving its name over DoH first (reaching the DoH resolvers by
    /// IP) and falling back to system DNS. This keeps OnionHop's own fetches - bridges, relay
    /// directory, node DB, update/IP checks - working where the ISP's DNS is poisoned or blocked,
    /// which is exactly when bridges are needed. If DoH yields nothing we defer to the normal system
    /// resolver, so this never makes resolution worse than before.
    /// </summary>
    private static async ValueTask<System.IO.Stream> DohFirstConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        // Literal IPs need no resolution.
        if (!IPAddress.TryParse(host, out _))
        {
            try
            {
                var dohAddresses = await DohNameResolver.ResolveAsync(host, token).ConfigureAwait(false);
                foreach (var address in dohAddresses)
                {
                    try
                    {
                        return await OpenSocketAsync(address, port, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Try the next DoH-resolved address.
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fall through to system DNS.
            }
        }

        // System-DNS path (also handles literal-IP hosts). Connecting by host name lets the socket
        // stack resolve and try each address.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<System.IO.Stream> OpenSocketAsync(IPAddress address, int port, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets a shared HttpClient instance with default timeout (30 seconds).
    /// Use for quick API calls like update checks.
    /// </summary>
    public static HttpClient Default => DefaultClientLazy.Value;

    /// <summary>
    /// Gets a shared HttpClient instance with long timeout (5 minutes).
    /// Use for large file downloads like dependencies.
    /// </summary>
    public static HttpClient LongTimeout => LongTimeoutClientLazy.Value;

    /// <summary>
    /// Creates a new HttpClient with custom handler for special scenarios (e.g., proxy testing).
    /// The caller is responsible for disposing this client.
    /// </summary>
    public static HttpClient CreateWithHandler(HttpClientHandler handler, TimeSpan timeout)
    {
        var client = new HttpClient(handler)
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");
        return client;
    }
}

