using System.Text.Json;
using OnionHopV2.Core.Services;
using Xunit;

namespace OnionHopV2.Tests.Services;

public sealed class XrayConfigBuilderTests
{
    [Fact]
    public void NonHybridMode_contains_tor_catchall_rule_and_tor_first_outbound()
    {
        var json = XrayConfigBuilder.BuildJson(
            hybridRouting: false,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunMtu: null);

        var doc = JsonDocument.Parse(json);
        var outbounds = doc.RootElement.GetProperty("outbounds");
        Assert.Equal("tor", outbounds[0].GetProperty("tag").GetString());

        var rules = doc.RootElement.GetProperty("routing").GetProperty("rules");
        var hasCatchAllTorRule = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("network", out var network) &&
                network.GetString() == "tcp,udp" &&
                rule.TryGetProperty("outboundTag", out var outboundTag) &&
                outboundTag.GetString() == "tor")
            {
                hasCatchAllTorRule = true;
                break;
            }
        }

        Assert.True(hasCatchAllTorRule);
    }

    [Fact]
    public void HybridMode_route_all_web_adds_tcp_80_443_tor_rule()
    {
        var json = XrayConfigBuilder.BuildJson(
            hybridRouting: true,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: true,
            blockQuicForTorApps: false,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunMtu: null);

        var doc = JsonDocument.Parse(json);
        var outbounds = doc.RootElement.GetProperty("outbounds");
        Assert.Equal("direct", outbounds[0].GetProperty("tag").GetString());

        var rules = doc.RootElement.GetProperty("routing").GetProperty("rules");
        var hasWebTrafficTorRule = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("network", out var network) &&
                network.GetString() == "tcp" &&
                rule.TryGetProperty("port", out var port) &&
                port.GetString() == "80,443" &&
                rule.TryGetProperty("outboundTag", out var outboundTag) &&
                outboundTag.GetString() == "tor")
            {
                hasWebTrafficTorRule = true;
                break;
            }
        }

        Assert.True(hasWebTrafficTorRule);
    }

    [Fact]
    public void TunMtu_is_applied_only_when_in_valid_range()
    {
        var withMtuJson = XrayConfigBuilder.BuildJson(
            hybridRouting: false,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunMtu: 1420);
        var withMtuDoc = JsonDocument.Parse(withMtuJson);
        var withMtuSettings = withMtuDoc.RootElement.GetProperty("inbounds")[0].GetProperty("settings");
        Assert.Equal(1420, withMtuSettings.GetProperty("mtu").GetInt32());

        var invalidMtuJson = XrayConfigBuilder.BuildJson(
            hybridRouting: false,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunMtu: 120);
        var invalidMtuDoc = JsonDocument.Parse(invalidMtuJson);
        var invalidMtuSettings = invalidMtuDoc.RootElement.GetProperty("inbounds")[0].GetProperty("settings");
        Assert.False(invalidMtuSettings.TryGetProperty("mtu", out _));
    }
}
