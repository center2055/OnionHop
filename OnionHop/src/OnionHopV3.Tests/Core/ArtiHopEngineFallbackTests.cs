using OnionHopV3.Core;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// ArtiHop is a bridge-less 2-hop SOCKS runtime. When a connection needs bridges or pluggable
/// transports (a censored network, or Smart Connect falling back to bridge strategies), the engine
/// must drop to the classic Tor engine - otherwise ArtiHop just fails to bootstrap and the "bridge
/// fallback" retries the same bridge-less engine forever. These cover that decision.
/// </summary>
public sealed class ArtiHopEngineFallbackTests
{
    [Fact]
    public void Direct_connection_stays_on_ArtiHop()
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
    public void Bridges_force_the_classic_engine()
    {
        var options = new OnionHopConnectOptions
        {
            UseTorBridges = true,
            SelectedBridgeType = "obfs4"
        };

        Assert.True(OnionHopClient.RequiresClassicTorEngine(options));
    }

    [Fact]
    public void Censored_mode_forces_the_classic_engine()
    {
        var options = new OnionHopConnectOptions { UseCensoredMode = true };
        Assert.True(OnionHopClient.RequiresClassicTorEngine(options));
    }

    [Fact]
    public void Custom_bridges_force_the_classic_engine()
    {
        var options = new OnionHopConnectOptions
        {
            CustomBridges = "obfs4 1.2.3.4:443 ABCD cert=x iat-mode=0"
        };

        Assert.True(OnionHopClient.RequiresClassicTorEngine(options));
    }

    [Fact]
    public void Country_pinning_alone_keeps_ArtiHop()
    {
        // ArtiHop ignores exit pinning but can still connect, so we keep the fast 2-hop path.
        var options = new OnionHopConnectOptions
        {
            SelectedLocation = "us",
            UseTorBridges = false,
            UseCensoredMode = false
        };

        Assert.False(OnionHopClient.RequiresClassicTorEngine(options));
    }
}
