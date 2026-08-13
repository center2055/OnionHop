using System;
using System.IO;
using System.Text.Json;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// When the core refuses to start because Windows would not attach an IPv6 address to the tunnel
/// adapter, the written config is retried with the IPv6 address stripped (#81).
/// </summary>
public sealed class TunIpv6StripTests
{
    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"onionhop-tun-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string[] Addresses(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new System.Collections.Generic.List<string>();
        foreach (var a in doc.RootElement.GetProperty("inbounds")[0].GetProperty("address").EnumerateArray())
        {
            list.Add(a.GetString()!);
        }

        return list.ToArray();
    }

    [Fact]
    public void Removes_the_ipv6_address_and_keeps_ipv4()
    {
        var path = WriteTemp("""
        { "inbounds": [ { "type": "tun", "address": [ "172.19.0.1/30", "fdfe:dcba:9876::1/126" ] } ] }
        """);
        try
        {
            Assert.True(VpnService.TryRemoveTunIpv6Address(path));
            Assert.Equal(new[] { "172.19.0.1/30" }, Addresses(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reports_no_change_when_there_is_no_ipv6_address()
    {
        // Nothing to strip means retrying would fail the same way, so the caller must not retry.
        var path = WriteTemp("""
        { "inbounds": [ { "type": "tun", "address": [ "172.19.0.1/30" ] } ] }
        """);
        try
        {
            Assert.False(VpnService.TryRemoveTunIpv6Address(path));
            Assert.Equal(new[] { "172.19.0.1/30" }, Addresses(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Never_strips_the_last_address()
    {
        // An IPv6-only tunnel would be left with an empty address list, which is worse than the
        // original failure, so it is left alone.
        var path = WriteTemp("""
        { "inbounds": [ { "type": "tun", "address": [ "fdfe:dcba:9876::1/126" ] } ] }
        """);
        try
        {
            Assert.False(VpnService.TryRemoveTunIpv6Address(path));
            Assert.Single(Addresses(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Leaves_the_rest_of_the_config_intact()
    {
        var path = WriteTemp("""
        {
          "log": { "level": "debug" },
          "inbounds": [ { "type": "tun", "tag": "tun-in", "address": [ "172.19.0.1/30", "fdfe::1/126" ], "auto_route": true } ],
          "outbounds": [ { "type": "socks", "tag": "tor", "server_port": 9050 } ]
        }
        """);
        try
        {
            Assert.True(VpnService.TryRemoveTunIpv6Address(path));

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var inbound = doc.RootElement.GetProperty("inbounds")[0];
            Assert.Equal("tun-in", inbound.GetProperty("tag").GetString());
            Assert.True(inbound.GetProperty("auto_route").GetBoolean());
            Assert.Equal("debug", doc.RootElement.GetProperty("log").GetProperty("level").GetString());
            Assert.Equal(9050, doc.RootElement.GetProperty("outbounds")[0].GetProperty("server_port").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Returns_false_for_an_unreadable_or_malformed_config()
    {
        Assert.False(VpnService.TryRemoveTunIpv6Address(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));

        var bad = WriteTemp("{ not json");
        try
        {
            Assert.False(VpnService.TryRemoveTunIpv6Address(bad));
        }
        finally
        {
            File.Delete(bad);
        }
    }
}
