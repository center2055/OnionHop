using System.Collections.Generic;
using System.Linq;
using OnionHopV3.Core.Tor;
using Xunit;

namespace OnionHopV3.Tests.Tor;

// #93: one bridge line with more settings than Tor accepts made Tor refuse to start at all.
public sealed class BridgeArgumentLimitTests
{
    // Tor's own Snowflake line from the circumvention map.
    private const string SnowflakeLine =
        "snowflake 192.0.2.3:80 2B280B23E1107BB62ABFC40DDCC8824814F80A72 fingerprint=2B280B23E1107BB62ABFC40DDCC8824814F80A72 url=https://1098762253.rsc.cdn77.org/ fronts=app.datapacket.com,www.datapacket.com ice=stun:stun.epygi.com:3478,stun:stun.uls.co.za:3478,stun:stun.telnyx.com:3478,stun:stun.hot-chilli.net:3478,stun:stun.fitauto.ru:3478,stun:stun.m-online.net:3478 utls-imitate=hellorandomizedalpn";

    private const string Obfs4Line = "obfs4 192.0.2.1:443 0123456789ABCDEF0123456789ABCDEF01234567 cert=abc iat-mode=0";

    private static string[] FrontsOf(string line) =>
        line.Split(' ').Single(token => token.StartsWith("fronts=")).Substring("fronts=".Length).Split(',');

    [Fact]
    public void Settings_are_counted_the_way_tor_counts_them()
    {
        Assert.Equal("cert=abc;iat-mode=0".Length, TorBridgeManager.GetTorTransportArgumentBytes(Obfs4Line));
        // ';' and '\' inside a value are escaped with a backslash.
        Assert.Equal(@"k=a\;b;x=c\\d".Length, TorBridgeManager.GetTorTransportArgumentBytes(@"obfs4 192.0.2.1:443 k=a;b x=c\d"));
        Assert.Equal(0, TorBridgeManager.GetTorTransportArgumentBytes("192.0.2.1:443 0123456789ABCDEF0123456789ABCDEF01234567"));
    }

    [Fact]
    public void A_long_sni_list_gives_each_snowflake_line_only_the_fronts_that_fit()
    {
        // As many hosts as "Request SNI" fetches for Iran.
        var hosts = Enumerable.Range(1, 56).Select(i => $"front{i:00}.example-cdn.com").ToList();

        var lines = TorBridgeManager.ApplyCustomSniHosts([SnowflakeLine, SnowflakeLine], hosts);

        Assert.Equal(2, lines.Count);
        foreach (var line in lines)
        {
            Assert.InRange(TorBridgeManager.GetTorTransportArgumentBytes(line), 1, TorBridgeManager.MaxTorTransportArgumentBytes);
            var fronts = FrontsOf(line);
            Assert.NotEmpty(fronts);
            Assert.All(fronts, front => Assert.Contains(front, hosts));
            Assert.DoesNotContain("datapacket.com", line);
        }
    }

    [Fact]
    public void A_short_sni_list_is_used_in_full()
    {
        var lines = TorBridgeManager.ApplyCustomSniHosts([SnowflakeLine], ["a.example", "b.example"]);

        Assert.Equal(["a.example", "b.example"], FrontsOf(lines[0]).OrderBy(front => front));
    }

    [Fact]
    public void A_line_over_the_limit_is_dropped_and_the_others_kept()
    {
        var tooLong = SnowflakeLine.Replace(
            "fronts=app.datapacket.com,www.datapacket.com",
            "fronts=" + string.Join(",", Enumerable.Range(1, 60).Select(i => $"front{i}.example.com")));
        var log = new List<string>();

        var kept = TorBridgeManager.DropLinesOverTorArgumentLimit([Obfs4Line, tooLong, SnowflakeLine], log.Add);

        Assert.Equal([Obfs4Line, SnowflakeLine], kept);
        var message = Assert.Single(log);
        Assert.Contains("snowflake", message);
        Assert.Contains("192.0.2.3:80", message);
    }

    [Fact]
    public void Lines_within_the_limit_come_back_unchanged()
    {
        string[] lines = [Obfs4Line, SnowflakeLine];

        Assert.Same(lines, TorBridgeManager.DropLinesOverTorArgumentLimit(lines, _ => Assert.Fail("nothing should be logged")));
    }
}
