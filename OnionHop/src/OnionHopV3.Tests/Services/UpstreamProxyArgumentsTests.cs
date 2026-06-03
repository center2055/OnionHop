using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// Verifies the Tor command-line tokens that route Tor through an upstream proxy (lets OnionHop run
/// behind another SOCKS5/HTTPS proxy — #51 fmogaddam / Proxifier chaining).
/// </summary>
public sealed class UpstreamProxyArgumentsTests
{
    [Fact]
    public void Socks5_NoAuth()
    {
        var args = TorService.BuildUpstreamProxyArguments("socks5", "127.0.0.1", 1080, null, null);
        Assert.Equal(new[] { "--Socks5Proxy", "127.0.0.1:1080" }, args);
    }

    [Fact]
    public void Socks5_WithAuth()
    {
        var args = TorService.BuildUpstreamProxyArguments("socks5", "10.0.0.5", 1080, "user", "pass");
        Assert.Equal(
            new[] { "--Socks5Proxy", "10.0.0.5:1080", "--Socks5ProxyUsername", "user", "--Socks5ProxyPassword", "pass" },
            args);
    }

    [Fact]
    public void Https_WithAuth()
    {
        var args = TorService.BuildUpstreamProxyArguments("https", "proxy.local", 8080, "u", "p");
        Assert.Equal(new[] { "--HTTPSProxy", "proxy.local:8080", "--HTTPSProxyAuthenticator", "u:p" }, args);
    }

    [Fact]
    public void DefaultsToSocks5_WhenKindMissing()
    {
        var args = TorService.BuildUpstreamProxyArguments(null, "h", 9050, null, null);
        Assert.Equal(new[] { "--Socks5Proxy", "h:9050" }, args);
    }

    [Theory]
    [InlineData(null, 1080)]
    [InlineData("", 1080)]
    [InlineData("host", 0)]
    [InlineData("host", 70000)]
    public void Empty_WhenHostOrPortInvalid(string? host, int port)
    {
        Assert.Empty(TorService.BuildUpstreamProxyArguments("socks5", host, port, null, null));
    }
}
