using System;
using System.Collections.Generic;
using System.Linq;
using OnionHopV3.Core;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// Traffic to the upstream proxy must never be routed through the tunnel: Tor dials that proxy, so
/// tunnelling it sends the proxy's own connection back through Tor and the two deadlock (#80).
/// </summary>
public sealed class UpstreamProxyBypassTests
{
    private static OnionHopConnectOptions Options(
        bool enabled, string? host, bool tun = false, bool hybrid = false) => new()
    {
        UpstreamProxyEnabled = enabled,
        UpstreamProxyHost = host,
        UpstreamProxyPort = 10808,
        SelectedConnectionMode = tun ? "TUN/VPN Mode (Admin)" : "Proxy Mode (Recommended)",
        UseHybridRouting = hybrid
    };

    [Fact]
    public void Adds_the_proxy_endpoint_to_the_bypass_list()
    {
        var result = OnionHopClient.WithUpstreamProxyBypass(
            new List<string> { "example.com" }, Options(enabled: true, host: "10.0.0.5"));

        Assert.Contains("10.0.0.5", result);
        Assert.Contains("example.com", result);
    }

    [Fact]
    public void Keeps_a_hostname_proxy_too()
    {
        var result = OnionHopClient.WithUpstreamProxyBypass(
            Array.Empty<string>(), Options(enabled: true, host: "proxy.corp.example"));

        Assert.Equal(new[] { "proxy.corp.example" }, result);
    }

    [Fact]
    public void Does_not_duplicate_an_entry_the_user_already_added()
    {
        var result = OnionHopClient.WithUpstreamProxyBypass(
            new List<string> { " 10.0.0.5 " }, Options(enabled: true, host: "10.0.0.5"));

        Assert.Single(result);
    }

    [Theory]
    [InlineData(false, "10.0.0.5")] // proxy disabled
    [InlineData(true, null)]        // no host configured
    [InlineData(true, "   ")]
    public void Leaves_the_list_untouched_when_there_is_no_proxy(bool enabled, string? host)
    {
        var entries = new List<string> { "example.com" };
        var result = OnionHopClient.WithUpstreamProxyBypass(entries, Options(enabled, host));

        Assert.Equal(entries, result);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.5", false)]              // a LAN proxy is reachable from inside the tunnel
    [InlineData("proxy.corp.example", false)]
    public void Warns_only_for_a_local_proxy_in_full_tunnel_mode(string host, bool expected)
    {
        Assert.Equal(expected, OnionHopClient.IsLoopbackUpstreamProxyInFullTunnel(
            Options(enabled: true, host: host, tun: true)));
    }

    [Fact]
    public void Does_not_warn_in_proxy_mode_or_hybrid_routing()
    {
        // Proxy Mode has no tunnel to capture the local proxy, and hybrid routing can bypass it.
        Assert.False(OnionHopClient.IsLoopbackUpstreamProxyInFullTunnel(
            Options(enabled: true, host: "127.0.0.1")));
        Assert.False(OnionHopClient.IsLoopbackUpstreamProxyInFullTunnel(
            Options(enabled: true, host: "127.0.0.1", tun: true, hybrid: true)));
    }
}
