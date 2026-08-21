using System;
using System.Linq;
using System.Text.Json;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// In hybrid (split tunnelling) mode the dns block used to carry no rules at all, so every process
/// resolved through the direct resolver. The traffic of a Tor-routed app took Tor, but the lookup
/// that preceded it went straight out, handing the ISP and the direct resolver every domain that app
/// visited. DNS has to follow the traffic, or split tunnelling leaks exactly what the user was
/// trying to protect.
/// </summary>
public sealed class HybridDnsLeakTests
{
    private static JsonElement Dns(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("dns").Clone();
    }

    private static string Build(
        bool hybrid,
        bool secureDns = true,
        string[]? torApps = null,
        string[]? bypassApps = null,
        bool routeAllWeb = false) =>
        VpnConfigBuilder.BuildJson(
            hybridRouting: hybrid,
            secureDns: secureDns,
            socksPort: 9050,
            torAppProcessNames: torApps ?? new[] { "firefox.exe" },
            bypassAppProcessNames: bypassApps ?? Array.Empty<string>(),
            routeAllWebTrafficThroughTor: routeAllWeb,
            blockQuicForTorApps: false,
            blockUdpTraffic: false,
            dohServer: "cloudflare-dns.com",
            dohServerPort: 443,
            dohPath: "/dns-query",
            tunStack: "mixed",
            tunMtu: null,
            tunStrictRoute: true);

    private static JsonElement Server(JsonElement dns, string tag) =>
        dns.GetProperty("servers").EnumerateArray()
            .Single(s => s.GetProperty("tag").GetString() == tag);

    private static bool HasServer(JsonElement dns, string tag) =>
        dns.GetProperty("servers").EnumerateArray()
            .Any(s => s.GetProperty("tag").GetString() == tag);

    [Fact]
    public void Tor_routed_apps_resolve_over_tor_in_hybrid_mode()
    {
        var dns = Dns(Build(hybrid: true, torApps: new[] { "firefox.exe" }));

        var rule = dns.GetProperty("rules").EnumerateArray().Single(r =>
            r.GetProperty("process_name").EnumerateArray().Any(p => p.GetString() == "firefox.exe"));

        Assert.Equal("remote-tor", rule.GetProperty("server").GetString());
        Assert.Equal("tor", Server(dns, "remote-tor").GetProperty("detour").GetString());
    }

    [Fact]
    public void The_tor_resolver_does_not_leak_its_own_bootstrap_lookup()
    {
        // Resolving cloudflare-dns.com directly would leak the fact and timing of every Tor-side
        // lookup, so the bootstrap for the Tor resolver has to go through Tor too.
        var dns = Dns(Build(hybrid: true));

        Assert.Equal("bootstrap-tor", Server(dns, "remote-tor").GetProperty("domain_resolver").GetString());
        Assert.Equal("tor", Server(dns, "bootstrap-tor").GetProperty("detour").GetString());
    }

    [Fact]
    public void Plain_dns_over_tor_uses_tcp_because_tor_carries_no_udp()
    {
        var dns = Dns(Build(hybrid: true, secureDns: false));
        var torServer = Server(dns, "remote-tor");

        Assert.Equal("tcp", torServer.GetProperty("type").GetString());
        Assert.Equal("tor", torServer.GetProperty("detour").GetString());
    }

    [Fact]
    public void Apps_left_direct_keep_the_direct_resolver()
    {
        // They never touch Tor, so routing their lookups through it would only be slow and would
        // contradict the user's explicit bypass choice.
        var dns = Dns(Build(hybrid: true, torApps: new[] { "firefox.exe" }, bypassApps: new[] { "slack.exe" }));

        var rules = dns.GetProperty("rules").EnumerateArray().ToArray();
        var bypassIndex = Array.FindIndex(rules, r =>
            r.GetProperty("process_name").EnumerateArray().Any(p => p.GetString() == "slack.exe"));
        var torIndex = Array.FindIndex(rules, r =>
            r.GetProperty("process_name").EnumerateArray().Any(p => p.GetString() == "firefox.exe"));

        Assert.True(bypassIndex >= 0, "the bypassed app should have a dns rule");
        Assert.Equal("remote", rules[bypassIndex].GetProperty("server").GetString());
        Assert.True(bypassIndex < torIndex, "bypass must be matched before the Tor rule");
    }

    [Fact]
    public void Routing_all_web_traffic_through_tor_also_routes_the_lookups()
    {
        // Otherwise every domain leaks ahead of the connection it belongs to.
        var dns = Dns(Build(hybrid: true, routeAllWeb: true));

        Assert.Equal("remote-tor", dns.GetProperty("final").GetString());
    }

    [Fact]
    public void Hybrid_without_tor_apps_is_unchanged()
    {
        // Nothing is routed through Tor, so there is nothing to protect and no Tor resolver to add.
        var dns = Dns(Build(hybrid: true, torApps: Array.Empty<string>()));

        Assert.False(HasServer(dns, "remote-tor"));
        Assert.Equal("remote", dns.GetProperty("final").GetString());
    }

    [Fact]
    public void Full_tunnel_still_sends_every_lookup_through_tor()
    {
        // It already did this via the remote server's own detour; adding hybrid rules must not have
        // changed it.
        var dns = Dns(Build(hybrid: false));

        Assert.Equal("tor", Server(dns, "remote").GetProperty("detour").GetString());
        Assert.Equal("remote", dns.GetProperty("final").GetString());
    }

    [Fact]
    public void Never_points_final_at_a_server_that_was_not_defined()
    {
        // A dangling dns.final would make sing-box refuse to start, taking the whole tunnel with it.
        foreach (var json in new[]
                 {
                     Build(hybrid: true, routeAllWeb: true, torApps: Array.Empty<string>()),
                     Build(hybrid: true, routeAllWeb: false, torApps: Array.Empty<string>()),
                     Build(hybrid: true, routeAllWeb: true),
                     Build(hybrid: false),
                 })
        {
            var dns = Dns(json);
            Assert.True(HasServer(dns, dns.GetProperty("final").GetString()!),
                $"dns.final points at an undefined server in: {json}");
        }
    }
}
