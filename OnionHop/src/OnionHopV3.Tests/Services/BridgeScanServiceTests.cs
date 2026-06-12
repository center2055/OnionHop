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

    [Fact]
    public void Dnstt_is_a_seeded_builtin_transport()
    {
        Assert.Contains("dnstt", BridgeSourceService.Transports);
        Assert.True(BridgeSourceService.BuiltInBridges.ContainsKey("dnstt"));
        Assert.NotEmpty(BridgeSourceService.BuiltInBridges["dnstt"]);
    }

    [Theory]
    [InlineData("dnstt 192.0.2.4:1 A998F319 doh=https://dns.google/dns-query pubkey=2411 domain=t.ruhnama.net", "dns.google")]
    [InlineData("dnstt 192.0.2.4:2 80EEFA4F dot=dot.example:853 pubkey=a2fb domain=t2.example.org", "dot.example")]
    // webtunnel: the real endpoint is the url= host, not the (placeholder) IP:port.
    [InlineData("webtunnel [2001:db8:1d30:ff54:bba:de27:3861:ff8c]:443 93F50F39 url=https://rabbithole2.net/4kHLbQ ver=0.0.1", "rabbithole2.net")]
    public void ExtractFrontHost_resolves_dnstt_resolver_host(string line, string expected)
    {
        Assert.Equal(expected, BridgeScanService.ExtractFrontHost(line));
    }

    [Theory]
    // RFC 3849 IPv6 documentation prefix (what every bundled webtunnel line uses) - must be treated
    // as a placeholder so the scanner probes the url= host instead of TCP-pinging a dead address.
    [InlineData("2001:db8:1d30:ff54:bba:de27:3861:ff8c", true)]
    [InlineData("2001:0DB8::1", true)] // upper-case hex must still match
    [InlineData("192.0.2.7", true)]    // RFC 5737 IPv4 documentation prefix
    [InlineData("::", true)]
    [InlineData("1.2.3.4", false)]     // a real address must NOT be treated as a placeholder
    [InlineData("2001:67c:289c::9", false)]
    public void IsPlaceholderHost_flags_documentation_addresses(string host, bool expected)
    {
        Assert.Equal(expected, BridgeScanService.IsPlaceholderHost(host));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("169.254.10.10", true)]   // link-local
    [InlineData("100.64.0.1", true)]      // CGNAT 100.64.0.0/10
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]       // multicast
    [InlineData("8.8.8.8", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("172.32.0.1", false)]     // just outside RFC1918 172.16/12
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("2001:67c:289c::9", false)]
    public void IsDisallowedProbeTarget_blocks_internal_addresses(string ip, bool expected)
    {
        Assert.Equal(expected, BridgeScanService.IsDisallowedProbeTarget(System.Net.IPAddress.Parse(ip)));
    }
}
