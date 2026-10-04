using System.Linq;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class CircumventionSettingsTests
{
    [Fact]
    public void Parse_keeps_tors_order_and_lines_and_merges_meek_variants()
    {
        const string json = """
        {
          "tm": { "settings": [
            { "bridges": { "type": "snowflake", "source": "bridgedb", "bridge_strings": ["snowflake 192.0.2.5:80 A ampcache=https://cdn.ampproject.org/ front=www.google.com"] } },
            { "bridges": { "type": "obfs4", "source": "bridgedb" } },
            { "bridges": { "type": "meek", "source": "bridgedb", "bridge_strings": ["meek_lite 192.0.2.20:80 url=https://a.test front=b.test"] } },
            { "bridges": { "type": "meek-azure", "source": "bridgedb", "bridge_strings": ["meek_lite 192.0.2.20:80 url=https://a.test front=b.test", "meek_lite 192.0.2.21:80 url=https://c.test front=d.test"] } }
          ] },
          "xx-invalid": { "settings": [] }
        }
        """;

        var settings = CircumventionSettings.Parse(json, "test");

        var tm = settings.ForCountry("TM");
        Assert.Equal(["snowflake", "obfs4", "meek"], tm.Select(t => t.Transport));
        Assert.Single(tm[0].BridgeLines);
        Assert.Empty(tm[1].BridgeLines);   // bridges come from Tor's bridge service instead
        Assert.Equal(2, tm[2].BridgeLines.Count);   // meek + meek-azure merged, duplicates dropped
        Assert.Empty(settings.ForCountry("de"));
        Assert.Empty(settings.ForCountry(null));
    }

    [Fact]
    public void The_bundled_snapshot_covers_the_heavily_censored_countries()
    {
        var bundled = CircumventionSettings.LoadBundled();

        Assert.Contains("bundled snapshot", bundled.Source);
        foreach (var country in new[] { "cn", "ir", "ru", "tm", "by", "mm" })
        {
            Assert.NotEmpty(bundled.ForCountry(country));
        }

        // China's Snowflake lines include the Amazon SQS rendezvous, Turkmenistan's use the AMP cache.
        var chinaSnowflake = bundled.ForCountry("cn").Single(t => t.Transport == "snowflake");
        Assert.Contains(chinaSnowflake.BridgeLines, line => line.Contains("sqsqueue="));
        var turkmenistanSnowflake = bundled.ForCountry("tm").Single(t => t.Transport == "snowflake");
        Assert.All(turkmenistanSnowflake.BridgeLines, line => Assert.Contains("ampcache=", line));
    }
}
