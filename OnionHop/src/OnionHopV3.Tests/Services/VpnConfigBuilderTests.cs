using System.Linq;
using System.Text.Json;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

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

    [Fact]
    public void Custom_interface_name_is_used_for_tun_inbound()
    {
        // The VPN service hands each connect a unique adapter name (e.g. "OnionHop3f2a") so a
        // leftover Wintun adapter from a racing teardown can never collide with the new one. The
        // builder must put exactly that name on the TUN inbound.
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
            tunStrictRoute: true,
            interfaceName: "OnionHop3f2a");

        var doc = JsonDocument.Parse(json);
        var tunInbound = doc.RootElement.GetProperty("inbounds")[0];
        Assert.Equal("OnionHop3f2a", tunInbound.GetProperty("interface_name").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_interface_name_falls_back_to_default(string? interfaceName)
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
            tunStrictRoute: true,
            interfaceName: interfaceName);

        var doc = JsonDocument.Parse(json);
        var tunInbound = doc.RootElement.GetProperty("inbounds")[0];
        var expected = System.OperatingSystem.IsMacOS() ? "utun99" : "OnionHop";
        Assert.Equal(expected, tunInbound.GetProperty("interface_name").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PluggableTransport_direct_rule_precedes_dns_hijack(bool hybridRouting)
    {
        // A transport's own DNS lookups (snowflake's broker/front domain, webtunnel's url host) must
        // bypass the Tor-detoured resolver, otherwise they can't resolve until Tor is up - which the
        // transport is trying to bootstrap. So the pluggable-transport "direct" rule must come BEFORE
        // the dns hijack rule in the route table.
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: hybridRouting,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: ["firefox.exe"],
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

        var rules = JsonDocument.Parse(json).RootElement.GetProperty("route").GetProperty("rules");
        var ruleList = rules.EnumerateArray().ToList();

        int hijackIndex = -1;
        int ptDirectIndex = -1;
        for (var i = 0; i < ruleList.Count; i++)
        {
            var rule = ruleList[i];
            if (hijackIndex < 0 &&
                rule.TryGetProperty("action", out var action) &&
                action.GetString() == "hijack-dns")
            {
                hijackIndex = i;
            }

            // The first process_name + outbound=direct rule is the pluggable-transport bypass rule
            // (its names are platform-specific, e.g. "tor" vs "tor.exe", so match structurally).
            if (ptDirectIndex < 0 &&
                rule.TryGetProperty("process_name", out var pn) &&
                pn.ValueKind == JsonValueKind.Array &&
                pn.EnumerateArray().Any(e => (e.GetString() ?? string.Empty).StartsWith("tor", System.StringComparison.OrdinalIgnoreCase)) &&
                rule.TryGetProperty("outbound", out var ob) &&
                ob.GetString() == "direct")
            {
                ptDirectIndex = i;
            }
        }

        Assert.True(ptDirectIndex >= 0, "Config should route pluggable-transport processes direct.");
        Assert.True(hijackIndex >= 0, "Config should hijack DNS.");
        Assert.True(ptDirectIndex < hijackIndex,
            $"PT-direct rule (index {ptDirectIndex}) must precede the dns hijack rule (index {hijackIndex}).");
    }

    [Fact]
    public void Arti_engines_and_transports_bypass_the_tunnel()
    {
        // Regression: arti.exe / artihop.exe were missing from the tunnel-bypass list, so an Arti or
        // ArtiHop TUN connection routed the engine's own guard/bridge traffic back into the tunnel and
        // deadlocked. The bundled pluggable transports (incl. dnstt-client) must bypass too.
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

        var rules = JsonDocument.Parse(json).RootElement.GetProperty("route").GetProperty("rules");
        var bypassNames = new System.Collections.Generic.List<string?>();
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("process_name", out var pn) &&
                pn.ValueKind == JsonValueKind.Array &&
                rule.TryGetProperty("outbound", out var ob) &&
                ob.GetString() == "direct")
            {
                bypassNames.AddRange(pn.EnumerateArray().Select(e => e.GetString()));
            }
        }

        // Names are platform-specific (.exe on Windows), so match either form.
        Assert.Contains(bypassNames, n => n is "arti.exe" or "arti");
        Assert.Contains(bypassNames, n => n is "artihop.exe" or "artihop");
        Assert.Contains(bypassNames, n => n is "webtunnel-client.exe" or "webtunnel-client");
        Assert.Contains(bypassNames, n => n is "dnstt-client.exe" or "dnstt-client");
    }
}
