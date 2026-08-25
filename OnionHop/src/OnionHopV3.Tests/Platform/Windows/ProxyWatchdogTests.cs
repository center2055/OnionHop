using OnionHopV3.Core.Platform.Windows;
using Xunit;

namespace OnionHopV3.Tests.Platform.Windows;

/// <summary>
/// Proxy Mode only protects traffic while the OS proxy still points at Tor, but "applied" was only
/// ever an in-memory flag, so a reset by Windows or another program left the toggle reading ON while
/// traffic went out direct. The periodic re-check decides what to do by comparing the live value
/// against the one this session writes, so those two must agree exactly (tester report).
/// </summary>
public sealed class ProxyWatchdogTests
{
    [Theory]
    [InlineData(9050, 9080)]
    [InlineData(9050, null)]
    [InlineData(19050, 19080)]
    [InlineData(1080, null)]
    public void What_we_write_is_always_recognised_as_ours(int socksPort, int? httpPort)
    {
        // If these ever disagree the watchdog either re-applies forever or, worse, mistakes our own
        // proxy for a foreign one and refuses to restore it.
        var written = WindowsProxyService.BuildProxyValue(socksPort, httpPort);

        Assert.True(WindowsProxyService.IsOnionHopProxyValue(written),
            $"BuildProxyValue produced '{written}', which IsOnionHopProxyValue does not accept");
    }

    [Fact]
    public void Writes_socks_only_when_there_is_no_http_port()
    {
        Assert.Equal("socks=127.0.0.1:9050", WindowsProxyService.BuildProxyValue(9050, null));
    }

    [Fact]
    public void Writes_http_https_and_socks_together()
    {
        Assert.Equal(
            "http=127.0.0.1:9080;https=127.0.0.1:9080;socks=127.0.0.1:9050",
            WindowsProxyService.BuildProxyValue(9050, 9080));
    }

    [Fact]
    public void A_different_sessions_ports_are_still_recognised_as_ours()
    {
        // Ports drift between sessions when one is busy. Such a value is ours and stale, so the
        // watchdog must be free to overwrite it rather than treating it as another program's.
        Assert.True(WindowsProxyService.IsOnionHopProxyValue(WindowsProxyService.BuildProxyValue(9051, 9081)));
    }

    [Theory]
    [InlineData("http=10.0.0.1:8080")]
    [InlineData("proxy.corp.example:3128")]
    [InlineData("socks=192.168.1.5:1080")]
    [InlineData("")]
    [InlineData(null)]
    public void Another_programs_proxy_is_never_mistaken_for_ours(string? foreign)
    {
        // The watchdog must leave these alone: overwriting would fight a setting the user may have
        // made deliberately.
        Assert.False(WindowsProxyService.IsOnionHopProxyValue(foreign));
    }
}
