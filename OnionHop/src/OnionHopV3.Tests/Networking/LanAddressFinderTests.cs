using System.Net;
using System.Net.NetworkInformation;
using OnionHopV3.Core.Networking;
using Xunit;
using Candidate = OnionHopV3.Core.Networking.LanAddressFinder.Candidate;

namespace OnionHopV3.Tests.Networking;

/// <summary>
/// The LAN sharing endpoints on Home (#85) are only useful if they show the address other devices
/// can actually reach, not OnionHop's own tunnel adapter or a VM switch.
/// </summary>
public sealed class LanAddressFinderTests
{
    private static Candidate Nic(string name, string ip, bool gateway = false, bool up = true,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet, string description = "") =>
        new(name, description, type, up, gateway, [IPAddress.Parse(ip)]);

    [Fact]
    public void Prefers_the_adapter_with_a_default_gateway()
    {
        var pick = LanAddressFinder.Pick([
            Nic("Ethernet 2", "10.0.0.5"),
            Nic("Wi-Fi", "192.168.30.34", gateway: true)
        ]);
        Assert.Equal("192.168.30.34", pick?.ToString());
    }

    [Fact]
    public void Skips_onionhops_own_tunnel_adapter()
    {
        // The TUN adapter has a private 172.19.0.1 address, but no device on the LAN can reach it.
        var pick = LanAddressFinder.Pick([
            Nic("OnionHop", "172.19.0.1", gateway: true, type: NetworkInterfaceType.Unknown, description: "WireGuard Tunnel / wintun"),
            Nic("Wi-Fi", "192.168.1.20")
        ]);
        Assert.Equal("192.168.1.20", pick?.ToString());
    }

    [Theory]
    [InlineData("vEthernet (WSL)", "")]
    [InlineData("Ethernet 3", "VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("VMware Network Adapter VMnet8", "")]
    public void Skips_vm_and_container_switches(string name, string description)
    {
        var pick = LanAddressFinder.Pick([
            Nic(name, "172.28.16.1", gateway: true, description: description),
            Nic("Ethernet", "192.168.0.10", gateway: true)
        ]);
        Assert.Equal("192.168.0.10", pick?.ToString());
    }

    [Fact]
    public void Skips_down_loopback_and_tunnel_adapters()
    {
        Assert.Null(LanAddressFinder.Pick([
            Nic("Ethernet", "192.168.0.10", gateway: true, up: false),
            Nic("Loopback", "127.0.0.1", type: NetworkInterfaceType.Loopback),
            Nic("Teredo", "10.1.1.1", type: NetworkInterfaceType.Tunnel)
        ]));
    }

    [Theory]
    [InlineData("10.4.5.6", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.10.20", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Only_rfc1918_addresses_count_as_lan(string ip, bool expected)
    {
        Assert.Equal(expected, LanAddressFinder.IsPrivateIPv4(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Public_or_link_local_only_means_no_lan_address()
    {
        Assert.Null(LanAddressFinder.Pick([
            Nic("Ethernet", "169.254.3.4"),
            Nic("Mobile", "100.70.1.2", gateway: true)
        ]));
    }
}
