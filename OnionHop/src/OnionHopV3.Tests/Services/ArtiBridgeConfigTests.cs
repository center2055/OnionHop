using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// Arti supports bridges + pluggable transports natively via its TOML config. These guard the
/// generation of the [bridges] / [[bridges.transports]] section so the upstream Arti engine connects
/// through OnionHop's bridges (end-to-end bridge connectivity still needs a real censored-network test).
/// </summary>
public sealed class ArtiBridgeConfigTests
{
    [Fact]
    public void BuildBridgesSection_EmptyWhenNoBridges()
    {
        var config = new ArtiLaunchConfig { ArtiPath = "arti", SocksPort = 9050 };
        Assert.Equal(string.Empty, ArtiService.BuildBridgesSection(config));
    }

    [Fact]
    public void BuildBridgesSection_EmitsBridgesAndTransports()
    {
        var config = new ArtiLaunchConfig
        {
            ArtiPath = "arti",
            SocksPort = 9050,
            BridgeLines = new[]
            {
                "obfs4 1.2.3.4:443 ABCDEF0123456789ABCDEF0123456789ABCDEF01 cert=xyz iat-mode=0",
                "Bridge webtunnel 5.6.7.8:443 0123456789ABCDEF0123456789ABCDEF01234567 url=https://x.example/abc",
            },
            TransportPlugins = new[]
            {
                @"obfs4 exec C:\Program Files\OnionHop\tor\pluggable_transports\lyrebird.exe",
                @"webtunnel exec C:\Program Files\OnionHop\tor\pluggable_transports\webtunnel-client.exe",
            },
        };

        var toml = ArtiService.BuildBridgesSection(config);

        Assert.Contains("[bridges]", toml);
        Assert.Contains("enabled = true", toml);
        // Bridge spec lines are present; the leading "Bridge " prefix is stripped.
        Assert.Contains("\"obfs4 1.2.3.4:443 ABCDEF0123456789ABCDEF0123456789ABCDEF01 cert=xyz iat-mode=0\"", toml);
        Assert.Contains("\"webtunnel 5.6.7.8:443", toml);
        Assert.DoesNotContain("\"Bridge ", toml);
        // One transport block per protocol, pointing at the PT binary (spaces in path are fine in TOML).
        Assert.Contains("protocols = [\"obfs4\"]", toml);
        Assert.Contains("protocols = [\"webtunnel\"]", toml);
        Assert.Contains(@"lyrebird.exe", toml);
        Assert.Contains(@"webtunnel-client.exe", toml);
    }

    [Fact]
    public void BuildBridgesSection_DeduplicatesTransports()
    {
        var config = new ArtiLaunchConfig
        {
            ArtiPath = "arti",
            SocksPort = 9050,
            BridgeLines = new[] { "obfs4 1.2.3.4:443 ABCDEF0123456789ABCDEF0123456789ABCDEF01 cert=xyz iat-mode=0" },
            TransportPlugins = new[]
            {
                @"obfs4 exec C:\pt\lyrebird.exe",
                @"obfs4 exec C:\pt\lyrebird.exe",
            },
        };

        var toml = ArtiService.BuildBridgesSection(config);

        // The obfs4 transport must appear exactly once even if the plugin line is repeated.
        var first = toml.IndexOf("protocols = [\"obfs4\"]", System.StringComparison.Ordinal);
        var last = toml.LastIndexOf("protocols = [\"obfs4\"]", System.StringComparison.Ordinal);
        Assert.True(first >= 0 && first == last, "obfs4 transport should be emitted exactly once.");
    }

    [Fact]
    public void BuildBridgesSection_StripsClientTransportPluginKeywordAndSplitsMethods()
    {
        var config = new ArtiLaunchConfig
        {
            ArtiPath = "arti",
            SocksPort = 9050,
            BridgeLines = new[]
            {
                "webtunnel 5.6.7.8:443 0123456789ABCDEF0123456789ABCDEF01234567 url=https://x.example/abc",
            },
            TransportPlugins = new[]
            {
                // Real-world shape: TorBridgeManager emits the full torrc line, keyword included.
                @"ClientTransportPlugin webtunnel exec C:\pt\webtunnel-client.exe",
                @"ClientTransportPlugin obfs4,meek_lite exec C:\pt\lyrebird.exe",
            },
        };

        var toml = ArtiService.BuildBridgesSection(config);

        // The "ClientTransportPlugin" keyword must be stripped: Arti rejects the whole config if a
        // protocol id is "ClientTransportPlugin webtunnel" instead of "webtunnel".
        Assert.Contains("protocols = [\"webtunnel\"]", toml);
        // Comma-separated PT methods become a list of individual quoted protocol names.
        Assert.Contains("protocols = [\"obfs4\", \"meek_lite\"]", toml);
        Assert.DoesNotContain("ClientTransportPlugin", toml);
    }

    [Theory]
    [InlineData(@"obfs4 exec C:\pt\lyrebird.exe", "obfs4", @"C:\pt\lyrebird.exe")]
    [InlineData("snowflake exec /usr/bin/snowflake-client", "snowflake", "/usr/bin/snowflake-client")]
    [InlineData(@"ClientTransportPlugin webtunnel exec C:\pt\webtunnel-client.exe", "webtunnel", @"C:\pt\webtunnel-client.exe")]
    [InlineData("ClientTransportPlugin snowflake exec /usr/bin/snowflake-client", "snowflake", "/usr/bin/snowflake-client")]
    [InlineData("nonsense", null, null)]
    public void ParseTransportPlugin_Works(string input, string? expectedTransport, string? expectedPath)
    {
        var parsed = ArtiService.ParseTransportPlugin(input);
        if (expectedTransport is null)
        {
            Assert.Null(parsed);
        }
        else
        {
            Assert.NotNull(parsed);
            Assert.Equal(expectedTransport, parsed!.Value.Transport);
            Assert.Equal(expectedPath, parsed.Value.Path);
        }
    }
}
