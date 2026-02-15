using System;
using System.Net;
using System.Net.Http;

namespace OnionHopV2.Core.Services;

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
            AutomaticDecompression = DecompressionMethods.All
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
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");
        return client;
    });

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

