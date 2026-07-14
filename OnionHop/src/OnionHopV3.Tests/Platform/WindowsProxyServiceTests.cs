using OnionHopV3.Core.Platform.Windows;
using Xunit;

namespace OnionHopV3.Tests.Platform;

public sealed class WindowsProxyServiceTests
{
    [Theory]
    // Exactly the two shapes ApplyTorProxy writes - these are ours and safe to heal.
    [InlineData("socks=127.0.0.1:9050")]
    [InlineData("socks=127.0.0.1:9150")]
    [InlineData("http=127.0.0.1:9080;https=127.0.0.1:9080;socks=127.0.0.1:9050")]
    [InlineData("HTTP=127.0.0.1:9080;HTTPS=127.0.0.1:9080;SOCKS=127.0.0.1:9051")] // case-insensitive
    [InlineData("  socks=127.0.0.1:9050  ")] // registry values may carry whitespace
    public void IsOnionHopProxyValue_matches_our_written_shapes(string value)
    {
        Assert.True(WindowsProxyService.IsOnionHopProxyValue(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("proxy.corp.example:8080")]                    // corporate proxy
    [InlineData("127.0.0.1:8080")]                             // another local tool (plain host:port)
    [InlineData("socks=10.0.0.1:9050")]                        // not loopback
    [InlineData("socks=127.0.0.1:9050;http=127.0.0.1:9080")]   // not our field order/shape
    [InlineData("http=127.0.0.1:9080;https=127.0.0.1:9081;socks=127.0.0.1:9050")] // http/https ports differ - not ours
    [InlineData("http=127.0.0.1:9080")]                        // http-only - we never write this
    [InlineData("socks=localhost:9050")]                       // we write the literal 127.0.0.1
    public void IsOnionHopProxyValue_never_matches_foreign_proxies(string? value)
    {
        Assert.False(WindowsProxyService.IsOnionHopProxyValue(value));
    }
}
