using System;
using System.Collections.Generic;
using System.Text.Json;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// A machine with IPv6 switched off cannot have an IPv6 address assigned to the TUN adapter: sing-box
/// fails with "configure tun interface: set ipv6 address: Element not found" and aborts the tunnel,
/// which dropped the connection seconds after Tor came up (#81).
/// </summary>
public sealed class TunIpv6ConfigTests
{
    private static string[] TunAddresses(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var addresses = doc.RootElement
            .GetProperty("inbounds")[0]
            .GetProperty("address");
        var result = new List<string>();
        foreach (var entry in addresses.EnumerateArray())
        {
            result.Add(entry.GetString()!);
        }

        return result.ToArray();
    }

    private static string Build(bool includeIpv6) => VpnConfigBuilder.BuildJson(
        hybridRouting: false,
        secureDns: true,
        socksPort: 9050,
        torAppProcessNames: Array.Empty<string>(),
        bypassAppProcessNames: Array.Empty<string>(),
        routeAllWebTrafficThroughTor: false,
        blockQuicForTorApps: false,
        blockUdpTraffic: false,
        dohServer: "cloudflare-dns.com",
        dohServerPort: 443,
        dohPath: "/dns-query",
        tunStack: "mixed",
        tunMtu: null,
        tunStrictRoute: true,
        tunIncludeIpv6: includeIpv6);

    [Fact]
    public void Keeps_the_ipv6_address_when_the_system_supports_ipv6()
    {
        var addresses = TunAddresses(Build(includeIpv6: true));

        Assert.Equal(2, addresses.Length);
        Assert.Contains("172.19.0.1/30", addresses);
        Assert.Contains("fdfe:dcba:9876::1/126", addresses);
    }

    [Fact]
    public void Drops_the_ipv6_address_when_ipv6_is_disabled()
    {
        var addresses = TunAddresses(Build(includeIpv6: false));

        Assert.Equal(new[] { "172.19.0.1/30" }, addresses);
    }

    [Fact]
    public void Ipv6_is_included_by_default_so_existing_setups_are_unchanged()
    {
        var json = VpnConfigBuilder.BuildJson(
            hybridRouting: false, secureDns: true, socksPort: 9050,
            torAppProcessNames: Array.Empty<string>(), bypassAppProcessNames: Array.Empty<string>(),
            routeAllWebTrafficThroughTor: false, blockQuicForTorApps: false, blockUdpTraffic: false,
            dohServer: null, dohServerPort: 443, dohPath: null,
            tunStack: "mixed", tunMtu: null, tunStrictRoute: true);

        Assert.Contains("fdfe:dcba:9876::1/126", TunAddresses(json));
    }
}
