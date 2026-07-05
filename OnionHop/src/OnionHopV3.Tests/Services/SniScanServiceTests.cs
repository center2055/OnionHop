using System.Collections.Generic;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public class SniScanServiceTests
{
    [Theory]
    [InlineData("https://example.com/path", "example.com")]
    [InlineData("example.com:443", "example.com")]
    [InlineData("  Example.com.  ", "Example.com")]
    [InlineData("# comment", "")]
    [InlineData("cdn.example.net", "cdn.example.net")]
    public void NormalizeSniHost_strips_scheme_path_port_and_trailing_dot(string input, string expected)
    {
        Assert.Equal(expected, SniScanService.NormalizeSniHost(input));
    }

    [Fact]
    public void TryEnumerateCidr_24_skips_network_and_broadcast()
    {
        var ok = SniScanService.TryEnumerateCidr("104.16.0.0/24", 4096, out var addresses, out var truncated);

        Assert.True(ok);
        Assert.False(truncated);
        Assert.Equal(254, addresses.Count);            // 256 minus network + broadcast
        Assert.Equal("104.16.0.1", addresses[0]);
        Assert.Equal("104.16.0.254", addresses[^1]);
        Assert.DoesNotContain("104.16.0.0", addresses);
        Assert.DoesNotContain("104.16.0.255", addresses);
    }

    [Fact]
    public void TryEnumerateCidr_32_is_the_single_host()
    {
        var ok = SniScanService.TryEnumerateCidr("1.2.3.4/32", 4096, out var addresses, out _);

        Assert.True(ok);
        Assert.Equal(["1.2.3.4"], addresses);
    }

    [Fact]
    public void TryEnumerateCidr_caps_and_flags_truncation()
    {
        var ok = SniScanService.TryEnumerateCidr("10.0.0.0/8", 100, out var addresses, out var truncated);

        Assert.True(ok);
        Assert.True(truncated);
        Assert.Equal(100, addresses.Count);
    }

    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("104.16.0.0")]        // no prefix
    [InlineData("104.16.0.0/33")]     // prefix out of range
    [InlineData("2001:db8::/32")]     // IPv6 not supported in range mode
    [InlineData("999.1.1.1/24")]      // invalid octet
    public void TryEnumerateCidr_rejects_bad_input(string cidr)
    {
        Assert.False(SniScanService.TryEnumerateCidr(cidr, 4096, out _, out _));
    }
}
