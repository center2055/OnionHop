using OnionHopV3.Core;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// The Arti family (Arti + ArtiHop) now supports bridges + pluggable transports natively, so a censored
/// / bridged connection NO LONGER forces the classic Tor engine. The only capability the Arti family
/// still lacks is upstream-proxy routing, which must drop to classic Tor. These cover that decision.
/// </summary>
public sealed class ArtiHopEngineFallbackTests
{
    [Fact]
    public void Direct_connection_stays_on_arti_family()
    {
        var options = new OnionHopConnectOptions
        {
            UseTorBridges = false,
            UseCensoredMode = false,
            CustomBridges = null
        };

        Assert.False(OnionHopClient.RequiresClassicTorEngine(options));
    }

    [Fact]
    public void Bridges_no_longer_force_the_classic_engine()
    {
        // Arti/ArtiHop apply bridges themselves now, so bridged connections keep the fast engine.
        var options = new OnionHopConnectOptions
        {
            UseTorBridges = true,
            SelectedBridgeType = "obfs4"
        };

        Assert.False(OnionHopClient.RequiresClassicTorEngine(options));
    }

    [Fact]
    public void Censored_mode_no_longer_forces_the_classic_engine()
    {
        var options = new OnionHopConnectOptions { UseCensoredMode = true };
        Assert.False(OnionHopClient.RequiresClassicTorEngine(options));
    }

    [Fact]
    public void Custom_bridges_no_longer_force_the_classic_engine()
    {
        var options = new OnionHopConnectOptions
        {
            CustomBridges = "obfs4 1.2.3.4:443 ABCD cert=x iat-mode=0"
        };

        Assert.False(OnionHopClient.RequiresClassicTorEngine(options));
    }

    [Fact]
    public void Upstream_proxy_forces_the_classic_engine()
    {
        // Upstream-proxy routing is the one thing the Arti family can't do, so it must use classic Tor.
        var options = new OnionHopConnectOptions
        {
            UpstreamProxyEnabled = true,
            UpstreamProxyHost = "127.0.0.1"
        };

        Assert.True(OnionHopClient.RequiresClassicTorEngine(options));
    }

    [Fact]
    public void Upstream_proxy_enabled_without_host_does_not_force_classic()
    {
        var options = new OnionHopConnectOptions
        {
            UpstreamProxyEnabled = true,
            UpstreamProxyHost = null
        };

        Assert.False(OnionHopClient.RequiresClassicTorEngine(options));
    }
}
