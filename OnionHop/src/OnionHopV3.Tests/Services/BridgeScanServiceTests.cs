using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class BridgeScanServiceTests
{
    [Theory]
    [InlineData("obfs4 1.2.3.4:443 ABCD cert=xyz iat-mode=0", "obfs4", "1.2.3.4", 443)]
    [InlineData("Bridge obfs4 9.9.9.9:9001 FINGER cert=z", "obfs4", "9.9.9.9", 9001)]
    [InlineData("webtunnel 5.6.7.8:443 FP url=https://x ver=0.0.1", "webtunnel", "5.6.7.8", 443)]
    [InlineData("1.2.3.4:8080 FINGERPRINTONLY", "vanilla", "1.2.3.4", 8080)]
    public void TryParseEndpoint_parses_ipv4_lines(string line, string expectedTransport, string expectedHost, int expectedPort)
    {
        var ok = BridgeScanService.TryParseEndpoint(line, out var transport, out var host, out var port);

        Assert.True(ok);
        Assert.Equal(expectedTransport, transport);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void TryParseEndpoint_parses_ipv6_bracketed_endpoint()
    {
        var ok = BridgeScanService.TryParseEndpoint(
            "obfs4 [2001:db8::1]:443 FINGERPRINT cert=z iat-mode=0",
            out var transport,
            out var host,
            out var port);

        Assert.True(ok);
        Assert.Equal("obfs4", transport);
        Assert.Equal("2001:db8::1", host);
        Assert.Equal(443, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("snowflake fingerprint=x url=https://broker")] // no direct IP:port endpoint
    public void TryParseEndpoint_rejects_lines_without_endpoint(string line)
    {
        var ok = BridgeScanService.TryParseEndpoint(line, out _, out _, out _);
        Assert.False(ok);
    }

    [Theory]
    [InlineData("Tested & Active", "obfs4", "IPv4", "obfs4_tested.txt")]
    [InlineData("Tested & Active", "obfs4", "IPv6", "obfs4_ipv6_tested.txt")]
    [InlineData("Fresh (72h)", "webtunnel", "IPv4", "webtunnel_72h.txt")]
    [InlineData("Fresh (72h)", "vanilla", "IPv6", "vanilla_ipv6_72h.txt")]
    [InlineData("Full Archive", "obfs4", "IPv4", "obfs4.txt")]
    [InlineData("Full Archive", "obfs4", "IPv6", "obfs4_ipv6.txt")]
    public void BuildFileName_matches_collector_layout(string category, string transport, string ipVersion, string expected)
    {
        Assert.Equal(expected, BridgeSourceService.BuildFileName(category, transport, ipVersion));
    }
}
