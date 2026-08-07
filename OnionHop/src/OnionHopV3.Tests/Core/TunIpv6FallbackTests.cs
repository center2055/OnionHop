using OnionHopV3.Core;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// Some Windows machines refuse to attach an IPv6 address to a freshly created tunnel adapter even
/// though IPv6 works on their normal adapters, and the tunnel treats that as fatal (#81). The tunnel
/// is recognised from its log line so it can be rebuilt IPv4-only instead of dropping the connection.
/// </summary>
public sealed class TunIpv6FallbackTests
{
    [Theory]
    [InlineData("FATAL[0002] start service: start inbound/tun[tun-in]: configure tun interface: set ipv6 address: Element not found.")]
    [InlineData("configure tun interface: set ipv6 address: Element not found.")]
    [InlineData("SET IPV6 ADDRESS: access is denied")] // matched regardless of case or trailing OS error
    public void Recognises_the_ipv6_address_failure(string line)
    {
        Assert.True(OnionHopClient.IsTunIpv6AddressFailure(line));
    }

    [Theory]
    [InlineData("INFO network: updated default interface Wi-Fi 6, index 44")]
    [InlineData("configure tun interface: Cannot create a file when that file already exists.")]
    [InlineData("set ipv4 address failed")]
    [InlineData("")]
    public void Ignores_unrelated_lines(string line)
    {
        Assert.False(OnionHopClient.IsTunIpv6AddressFailure(line));
    }
}
