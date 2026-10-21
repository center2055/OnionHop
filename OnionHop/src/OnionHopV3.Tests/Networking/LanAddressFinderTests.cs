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

    // A Raspberry Pi as a router: the uplink has the default route, the access point serves the LAN.
    private static readonly Candidate[] Router =
    [
        Nic("eth0", "10.24.7.15", gateway: true),
        Nic("wlan0", "192.168.4.1"),
        Nic("lo", "127.0.0.1", type: NetworkInterfaceType.Loopback),
        Nic("docker0", "172.17.0.1", description: "docker bridge")
    ];

    [Fact]
    public void Lists_every_side_of_a_router_not_just_the_one_with_the_default_route()
    {
        // Pick alone would answer eth0, the side facing the internet (#89).
        var listed = LanAddressFinder.ListShareable(Router);

        Assert.Equal(
            ["eth0 10.24.7.15", "wlan0 192.168.4.1"],
            listed.Select(entry => $"{entry.InterfaceName} {entry.Address}"));
    }

    [Theory]
    [InlineData("wlan0", "wlan0", "192.168.4.1")]
    [InlineData("WLAN0", "wlan0", "192.168.4.1")]
    [InlineData("192.168.4.1", "wlan0", "192.168.4.1")]
    [InlineData("docker0", "docker0", "172.17.0.1")] // named explicitly, so a container bridge is fine
    public void Resolves_an_interface_name_or_one_of_its_addresses(string wanted, string expectedName, string expectedAddress)
    {
        var resolved = LanAddressFinder.ResolveShareAddress(Router, wanted);

        Assert.NotNull(resolved);
        Assert.Equal(expectedName, resolved!.InterfaceName);
        Assert.Equal(expectedAddress, resolved.Address.ToString());
    }

    [Theory]
    [InlineData("wlan1")]          // no such interface
    [InlineData("192.168.4.99")]   // not an address of this machine
    [InlineData("lo")]             // loopback is this machine only
    [InlineData("127.0.0.1")]
    [InlineData("fe80::1")]        // IPv4 only
    public void Rejects_what_cannot_be_shared_on(string wanted)
    {
        Assert.Null(LanAddressFinder.ResolveShareAddress(Router, wanted));
    }

    [Fact]
    public void A_down_interface_cannot_be_shared_on()
    {
        Assert.Null(LanAddressFinder.ResolveShareAddress([Nic("wlan0", "192.168.4.1", up: false)], "wlan0"));
    }
}
