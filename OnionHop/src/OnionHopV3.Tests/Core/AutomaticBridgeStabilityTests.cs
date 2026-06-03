using OnionHopV3.Core;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// Smart Connect automatic-bridge "is this transport unstable?" decision. Proxy-handshake warnings are
/// per dead bridge, so a large bridge set in a partially censored network throws dozens of them even
/// while one bridge works — the heuristic must not abort a transport that is actually bootstrapping
/// (issue #44).
/// </summary>
public sealed class AutomaticBridgeStabilityTests
{
    [Fact]
    public void FewFailures_NoProgress_DoesNotAbort()
    {
        Assert.False(OnionHopClient.ShouldAbortAutomaticBridgeTransportAsUnstable(5, 0.0));
    }

    [Fact]
    public void ManyFailures_NoProgress_Aborts()
    {
        // A genuinely dead transport: lots of handshake failures and Tor is stuck at the start.
        Assert.True(OnionHopClient.ShouldAbortAutomaticBridgeTransportAsUnstable(87, 0.0));
    }

    [Fact]
    public void ManyFailures_WithBootstrapProgress_DoesNotAbort()
    {
        // The #44 case: a big bridge set throws many failures from the dead bridges, but one is working
        // and Tor is bootstrapping — must not be declared unstable.
        Assert.False(OnionHopClient.ShouldAbortAutomaticBridgeTransportAsUnstable(87, 0.40));
    }

    [Fact]
    public void ManyFailures_JustBelowProgressFloor_Aborts()
    {
        Assert.True(OnionHopClient.ShouldAbortAutomaticBridgeTransportAsUnstable(40, 0.05));
    }
}
