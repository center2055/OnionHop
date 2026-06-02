using OnionHopV3.Core;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// Regression coverage for the full-system DNS-over-Tor decision. The bug (v3.0.0) forced all system
/// DNS through Tor in "Local proxy only" scope, where system traffic is direct — breaking name
/// resolution for every app on the machine. These pin the rule: full DNS-over-Tor applies only when
/// the system proxy is genuinely carrying traffic through Tor.
/// </summary>
public sealed class OnionHopClientDnsScopeTests
{
    private static OnionHopConnectOptions Options(string scope, bool applyProxy, bool fullDns) =>
        new()
        {
            ProxyScopeMode = scope,
            ApplySystemProxyOnConnect = applyProxy,
            FullDnsOverTor = fullDns
        };

    [Fact]
    public void SystemScope_ProxyApplied_FullDns_RoutesAllDns()
    {
        var options = Options(OnionHopConnectOptions.ProxyScopeSystem, applyProxy: true, fullDns: true);
        Assert.True(OnionHopClient.ShouldRouteAllSystemDnsOverTor(options, isTunMode: false));
    }

    [Fact]
    public void SystemSocksScope_ProxyApplied_FullDns_RoutesAllDns()
    {
        var options = Options(OnionHopConnectOptions.ProxyScopeSystemSocks, applyProxy: true, fullDns: true);
        Assert.True(OnionHopClient.ShouldRouteAllSystemDnsOverTor(options, isTunMode: false));
    }

    [Fact]
    public void LocalOnlyScope_FullDns_DoesNotRouteAllDns()
    {
        // The regression: local-only never installs a system proxy, so system DNS must stay direct.
        var options = Options(OnionHopConnectOptions.ProxyScopeLocalOnly, applyProxy: true, fullDns: true);
        Assert.False(OnionHopClient.ShouldRouteAllSystemDnsOverTor(options, isTunMode: false));
    }

    [Fact]
    public void SystemScope_ProxyNotApplied_DoesNotRouteAllDns()
    {
        // Proxy pre-set OFF => system traffic is direct => pinning DNS to Tor would strand lookups.
        var options = Options(OnionHopConnectOptions.ProxyScopeSystem, applyProxy: false, fullDns: true);
        Assert.False(OnionHopClient.ShouldRouteAllSystemDnsOverTor(options, isTunMode: false));
    }

    [Fact]
    public void TunMode_DoesNotApplySystemWideRule()
    {
        // TUN core handles DNS inside the tunnel; the system-wide NRPT rule must not also fire.
        var options = Options(OnionHopConnectOptions.ProxyScopeSystem, applyProxy: true, fullDns: true);
        Assert.False(OnionHopClient.ShouldRouteAllSystemDnsOverTor(options, isTunMode: true));
    }

    [Fact]
    public void FullDnsOff_NeverRoutesAllDns()
    {
        var options = Options(OnionHopConnectOptions.ProxyScopeSystem, applyProxy: true, fullDns: false);
        Assert.False(OnionHopClient.ShouldRouteAllSystemDnsOverTor(options, isTunMode: false));
    }
}
