using System.Linq;
using System.Text.Json;
using OnionHopV2.Core.Services;
using Xunit;

namespace OnionHopV2.Tests.Services;

public sealed class VpnConfigBuilderTests
{
    [Fact]
    public void HybridMode_with_bypass_apps_adds_direct_rule()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: true,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: ["firefox.exe", "chrome.exe"],
            bypassAppProcessNames: ["notepad.exe"],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            blockUdpTraffic: true,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunStack: "mixed",
            tunMtu: null,
            tunStrictRoute: true);

        var doc = JsonDocument.Parse(json);
        var route = doc.RootElement.GetProperty("route");
        var rules = route.GetProperty("rules");

        var hasBypassDirectRule = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("process_name", out var pn) &&
                rule.TryGetProperty("outbound", out var ob) &&
                ob.GetString() == "direct")
            {
                var names = pn.ValueKind == JsonValueKind.Array
                    ? pn.EnumerateArray().Select(e => e.GetString()).ToList()
                    : [pn.GetString()];
                if (names.Contains("notepad.exe"))
                {
                    hasBypassDirectRule = true;
                    break;
                }
            }
        }

        Assert.True(hasBypassDirectRule, "Config should contain process_name=notepad.exe outbound=direct for split tunneling bypass.");
    }

    [Fact]
    public void HybridMode_with_tor_apps_adds_tor_rule()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: true,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: ["firefox.exe", "chrome.exe"],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            blockUdpTraffic: true,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunStack: "mixed",
            tunMtu: null,
            tunStrictRoute: true);

        var doc = JsonDocument.Parse(json);
        var route = doc.RootElement.GetProperty("route");
        var rules = route.GetProperty("rules");

        var hasTorRule = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("process_name", out var pn) &&
                rule.TryGetProperty("outbound", out var ob) &&
                ob.GetString() == "tor")
            {
                hasTorRule = true;
                break;
            }
        }

        Assert.True(hasTorRule, "Config should contain process_name outbound=tor for split tunneling Tor apps.");
    }

    [Fact]
    public void HybridMode_final_is_direct()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: true,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            blockUdpTraffic: true,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunStack: "mixed",
            tunMtu: null,
            tunStrictRoute: true);

        var doc = JsonDocument.Parse(json);
        var final = doc.RootElement.GetProperty("route").GetProperty("final").GetString();
        Assert.Equal("direct", final);
    }

    [Fact]
    public void NonHybridMode_final_is_tor()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: false,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            blockUdpTraffic: true,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunStack: "mixed",
            tunMtu: null,
            tunStrictRoute: true);

        var doc = JsonDocument.Parse(json);
        var final = doc.RootElement.GetProperty("route").GetProperty("final").GetString();
        Assert.Equal("tor", final);
    }

    [Fact]
    public void HybridMode_with_block_quic_adds_udp_block_rules_for_tor_apps()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: true,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: ["firefox.exe"],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: true,
            blockUdpTraffic: false,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunStack: "mixed",
            tunMtu: null,
            tunStrictRoute: true);

        var doc = JsonDocument.Parse(json);
        var rules = doc.RootElement.GetProperty("route").GetProperty("rules");

        var udpBlockCount = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("network", out var net) &&
                net.GetString() == "udp" &&
                rule.TryGetProperty("outbound", out var ob) &&
                ob.GetString() == "block")
            {
                udpBlockCount++;
            }
        }

        Assert.True(udpBlockCount >= 1, "Config should contain UDP block rules for Tor apps when BlockQuicForTorApps is enabled.");
    }

    [Fact]
    public void Route_all_web_traffic_adds_tcp_80_443_tor_rule()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: true,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: true,
            blockQuicForTorApps: false,
            blockUdpTraffic: true,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunStack: "mixed",
            tunMtu: null,
            tunStrictRoute: true);

        var doc = JsonDocument.Parse(json);
        var rules = doc.RootElement.GetProperty("route").GetProperty("rules");

        var hasWebTrafficRule = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("network", out var net) &&
                net.GetString() == "tcp" &&
                rule.TryGetProperty("port", out var port) &&
                rule.TryGetProperty("outbound", out var ob) &&
                ob.GetString() == "tor")
            {
                hasWebTrafficRule = true;
                break;
            }
        }

        Assert.True(hasWebTrafficRule, "Config should route TCP 80/443 through Tor when RouteAllWebTrafficThroughTor is enabled.");
    }

    [Fact]
    public void TunOptions_are_applied_to_tun_inbound()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: false,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            blockUdpTraffic: true,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunStack: "system",
            tunMtu: 1420,
            tunStrictRoute: false);

        var doc = JsonDocument.Parse(json);
        var tunInbound = doc.RootElement.GetProperty("inbounds")[0];
        Assert.Equal("system", tunInbound.GetProperty("stack").GetString());
        Assert.Equal(1420, tunInbound.GetProperty("mtu").GetInt32());
        Assert.False(tunInbound.GetProperty("strict_route").GetBoolean());
    }

    [Fact]
    public void Invalid_tun_options_fallback_to_safe_defaults()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: false,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: [],
            bypassAppProcessNames: [],
            routeAllWebTrafficThroughTor: false,
            blockQuicForTorApps: false,
            blockUdpTraffic: true,
            dohServer: null,
            dohServerPort: 443,
            dohPath: null,
            tunStack: "invalid",
            tunMtu: 120,
            tunStrictRoute: true);

        var doc = JsonDocument.Parse(json);
        var tunInbound = doc.RootElement.GetProperty("inbounds")[0];
        Assert.Equal("mixed", tunInbound.GetProperty("stack").GetString());
        Assert.True(tunInbound.GetProperty("strict_route").GetBoolean());
        Assert.False(tunInbound.TryGetProperty("mtu", out _));
    }
}
