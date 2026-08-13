using System;
using System.Linq;
using System.Text.Json;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// In hybrid (split tunnelling) mode the UDP block exists so an app routed through Tor cannot leak
/// via UDP, which Tor does not carry. It must not hit apps the user deliberately left direct: those
/// never touch Tor, and blocking their UDP just kills QUIC (HTTP/3), which is what broke YouTube for
/// a user who routed their torrent client through Tor and kept the browser direct.
/// </summary>
public sealed class HybridUdpScopeTests
{
    private static JsonElement[] Rules(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
            .Select(r => r.Clone()).ToArray();
    }

    private static bool IsBlanketUdpBlock(JsonElement rule) =>
        rule.TryGetProperty("network", out var n) && n.GetString() == "udp"
        && rule.TryGetProperty("outbound", out var o) && o.GetString() == "block"
        && !rule.TryGetProperty("process_name", out _)
        && !rule.TryGetProperty("port", out _);

    private static bool IsTorAppUdpBlock(JsonElement rule) =>
        rule.TryGetProperty("network", out var n) && n.GetString() == "udp"
        && rule.TryGetProperty("outbound", out var o) && o.GetString() == "block"
        && rule.TryGetProperty("process_name", out _);

    private static bool IsQuicPortBlock(JsonElement rule) =>
        rule.TryGetProperty("network", out var n) && n.GetString() == "udp"
        && rule.TryGetProperty("outbound", out var o) && o.GetString() == "block"
        && rule.TryGetProperty("port", out var p) && p.GetInt32() == 443;

    private static string Build(
        bool hybrid, bool blockUdp, bool blockQuicForTorApps = false, bool routeAllWeb = false) =>
        VpnConfigBuilder.BuildJson(
            hybridRouting: hybrid,
            secureDns: false,
            socksPort: 9050,
            torAppProcessNames: new[] { "qbittorrent.exe" },
            bypassAppProcessNames: Array.Empty<string>(),
            routeAllWebTrafficThroughTor: routeAllWeb,
            blockQuicForTorApps: blockQuicForTorApps,
            blockUdpTraffic: blockUdp,
            dohServer: null, dohServerPort: 443, dohPath: null,
            tunStack: "mixed", tunMtu: null, tunStrictRoute: true);

    [Fact]
    public void Hybrid_does_not_block_udp_for_apps_left_direct()
    {
        var rules = Rules(Build(hybrid: true, blockUdp: true));

        Assert.DoesNotContain(rules, IsBlanketUdpBlock);
    }

    [Fact]
    public void Hybrid_still_blocks_udp_for_tor_routed_apps()
    {
        var rules = Rules(Build(hybrid: true, blockUdp: true));

        Assert.Contains(rules, IsTorAppUdpBlock);
    }

    [Fact]
    public void Hybrid_blocks_quic_when_all_web_traffic_is_forced_through_tor()
    {
        // Otherwise a browser just uses HTTP/3 over UDP 443 and routes around the Tor rule.
        var rules = Rules(Build(hybrid: true, blockUdp: true, routeAllWeb: true));

        Assert.Contains(rules, IsQuicPortBlock);
    }

    [Fact]
    public void Hybrid_quic_only_option_still_blocks_udp_for_tor_apps()
    {
        var rules = Rules(Build(hybrid: true, blockUdp: false, blockQuicForTorApps: true));

        Assert.Contains(rules, IsTorAppUdpBlock);
        Assert.DoesNotContain(rules, IsBlanketUdpBlock);
    }

    [Fact]
    public void Hybrid_leaves_udp_alone_when_both_options_are_off()
    {
        var rules = Rules(Build(hybrid: true, blockUdp: false));

        Assert.DoesNotContain(rules, IsBlanketUdpBlock);
        Assert.DoesNotContain(rules, IsTorAppUdpBlock);
    }

    [Fact]
    public void Explicitly_bypassed_apps_are_matched_before_any_udp_block()
    {
        // Rules are first-match-wins, so the bypass rule has to come first or a bypassed app would
        // still lose QUIC when all web traffic is forced through Tor.
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: true, secureDns: false, socksPort: 9050,
            torAppProcessNames: new[] { "qbittorrent.exe" },
            bypassAppProcessNames: new[] { "firefox.exe" },
            routeAllWebTrafficThroughTor: true,
            blockQuicForTorApps: false, blockUdpTraffic: true,
            dohServer: null, dohServerPort: 443, dohPath: null,
            tunStack: "mixed", tunMtu: null, tunStrictRoute: true);

        var rules = Rules(json);
        var bypassIndex = Array.FindIndex(rules, r =>
            r.TryGetProperty("outbound", out var o) && o.GetString() == "direct"
            && r.TryGetProperty("process_name", out var p)
            && p.EnumerateArray().Any(v => v.GetString() == "firefox.exe"));
        var firstUdpBlock = Array.FindIndex(rules, r =>
            r.TryGetProperty("network", out var n) && n.GetString() == "udp"
            && r.TryGetProperty("outbound", out var o) && o.GetString() == "block");

        Assert.True(bypassIndex >= 0, "the bypassed app should have a direct rule");
        Assert.True(firstUdpBlock >= 0, "a UDP block is expected in this configuration");
        Assert.True(bypassIndex < firstUdpBlock,
            $"bypass rule (index {bypassIndex}) must precede the UDP block (index {firstUdpBlock})");
    }

    [Fact]
    public void Full_tunnel_still_blocks_all_udp()
    {
        // Everything goes through Tor there, so a blanket block is correct and must stay.
        var rules = Rules(Build(hybrid: false, blockUdp: true));

        Assert.Contains(rules, IsBlanketUdpBlock);
    }
}
