using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class DnsttForwarderServiceTests
{
    [Fact]
    public void TryParse_parses_a_full_doh_line()
    {
        var bridge = DnsttForwarderService.TryParse(
            "dnstt 192.0.2.4:1 A998F319ADB60EE344540EC4B21524CC484F96BE doh=https://dns.google/dns-query pubkey=241169008830694749fe96bb070c4855c5bb5b9c47b3833ed7d88521ba30a43f domain=t.ruhnama.net");

        Assert.NotNull(bridge);
        Assert.Equal("A998F319ADB60EE344540EC4B21524CC484F96BE", bridge!.Fingerprint);
        Assert.Equal("https://dns.google/dns-query", bridge.Doh);
        Assert.Null(bridge.Dot);
        Assert.Equal("241169008830694749fe96bb070c4855c5bb5b9c47b3833ed7d88521ba30a43f", bridge.Pubkey);
        Assert.Equal("t.ruhnama.net", bridge.Domain);
    }

    [Fact]
    public void TryParse_handles_Bridge_prefix_and_dot()
    {
        var bridge = DnsttForwarderService.TryParse(
            "Bridge dnstt 192.0.2.4:2 80EEFA4F4875ED2B7B5A86DF2D7588AD32E29F15 dot=dot.example:853 pubkey=a2fb domain=t2.example.org");

        Assert.NotNull(bridge);
        Assert.Equal("dot.example:853", bridge!.Dot);
        Assert.Null(bridge.Doh);
        Assert.Equal("t2.example.org", bridge.Domain);
    }

    [Theory]
    [InlineData("obfs4 1.2.3.4:443 ABCD cert=x")]                                                              // not a dnstt line
    [InlineData("dnstt 192.0.2.4:1 A998F319ADB60EE344540EC4B21524CC484F96BE pubkey=abc domain=t.example")]     // no doh/dot
    [InlineData("dnstt 192.0.2.4:1 A998F319ADB60EE344540EC4B21524CC484F96BE doh=https://d/q domain=t.example")]// no pubkey
    [InlineData("dnstt 192.0.2.4:1 NOT_A_FINGERPRINT doh=https://d/q pubkey=abc domain=t.example")]            // bad fingerprint
    [InlineData("")]
    public void TryParse_rejects_invalid_lines(string line)
    {
        Assert.Null(DnsttForwarderService.TryParse(line));
    }
}
