using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OnionHopV3.Core.Networking;

/// <summary>
/// Finds the address other devices on the local network use to reach this computer, so Home can
/// show the LAN sharing endpoints instead of leaving people to dig the IP out of their OS (#85).
/// </summary>
public static class LanAddressFinder
{
    /// <summary>One network adapter, reduced to what choosing a LAN address needs.</summary>
    public sealed record Candidate(
        string Name,
        string Description,
        NetworkInterfaceType Type,
        bool IsUp,
        bool HasGateway,
        IReadOnlyList<IPAddress> Addresses);

    // Adapters with private addresses that other devices on the LAN cannot reach: OnionHop's own
    // tunnel, VM and container switches.
    private static readonly string[] VirtualAdapterMarkers =
    [
        "onionhop", "wintun", "sing-box", "singbox", "xray", "vmware", "virtualbox", "hyper-v",
        "vethernet", "wsl", "docker", "vbox"
    ];

    /// <summary>The best LAN IPv4 address of this computer, or null when there is none.</summary>
    public static string? GetPrimaryLanIPv4()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Select(ToCandidate)
                .Where(candidate => candidate != null)
                .Select(candidate => candidate!)
                .ToList();
            return Pick(candidates)?.ToString();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Candidate? ToCandidate(NetworkInterface nic)
    {
        try
        {
            var properties = nic.GetIPProperties();
            var addresses = properties.UnicastAddresses
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
            return new Candidate(nic.Name, nic.Description, nic.NetworkInterfaceType,
                nic.OperationalStatus == OperationalStatus.Up, hasGateway, addresses);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prefers a private IPv4 address on an adapter that has a default gateway, which is the one the
    /// rest of the network sees. Skips adapters that are down, loopback, tunnels (OnionHop's own TUN
    /// adapter has a private address too), VM switches and link-local addresses.
    /// </summary>
    internal static IPAddress? Pick(IEnumerable<Candidate> candidates)
    {
        var usable = candidates
            .Where(candidate => candidate.IsUp
                                && candidate.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                                && !LooksVirtual(candidate))
            .SelectMany(candidate => candidate.Addresses
                .Where(IsPrivateIPv4)
                .Select(address => (candidate.HasGateway, Address: address)))
            .ToList();

        return usable.Where(entry => entry.HasGateway).Select(entry => entry.Address).FirstOrDefault()
               ?? usable.Select(entry => entry.Address).FirstOrDefault();
    }

    /// <summary>RFC 1918 ranges: 10/8, 172.16/12, 192.168/16. Link-local (169.254/16) is not private here.</summary>
    internal static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool LooksVirtual(Candidate candidate)
    {
        var text = $"{candidate.Name} {candidate.Description}";
        return VirtualAdapterMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
