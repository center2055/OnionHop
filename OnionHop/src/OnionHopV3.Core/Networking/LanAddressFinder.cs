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

    /// <summary>One of this computer's IPv4 addresses with the adapter it belongs to.</summary>
    public sealed record LanAddress(string InterfaceName, IPAddress Address);

    /// <summary>The best LAN IPv4 address of this computer, or null when there is none.</summary>
    public static string? GetPrimaryLanIPv4() => Pick(ReadCandidates())?.ToString();

    /// <summary>
    /// Every private IPv4 address other devices could use, with its adapter. A router has one per
    /// side, and the one with the default gateway, which <see cref="GetPrimaryLanIPv4"/> prefers, is
    /// the side facing the internet (#89).
    /// </summary>
    public static IReadOnlyList<LanAddress> GetLanIPv4Addresses() => ListShareable(ReadCandidates());

    /// <summary>
    /// The address to share the proxy on for <paramref name="nameOrAddress"/>: an adapter name such
    /// as eth0 or wlan0 gives its IPv4 address, private ones first; an IPv4 address must belong to
    /// one of this computer's adapters. Null when neither matches.
    /// </summary>
    public static LanAddress? ResolveShareAddress(string nameOrAddress) =>
        ResolveShareAddress(ReadCandidates(), nameOrAddress);

    /// <summary>
    /// The IPv4 default gateway of this computer's real network adapter, the router it reaches the
    /// internet through, or null. Tells networks apart where the public address cannot be looked up.
    /// </summary>
    public static string? GetDefaultGatewayIPv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var candidate = ToCandidate(nic);
                if (candidate == null || !IsShareable(candidate))
                {
                    continue;
                }

                var gateway = nic.GetIPProperties().GatewayAddresses
                    .Select(entry => entry.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
                if (gateway != null)
                {
                    return gateway.ToString();
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
        }

        return null;
    }

    private static IReadOnlyList<Candidate> ReadCandidates()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Select(ToCandidate)
                .Where(candidate => candidate != null)
                .Select(candidate => candidate!)
                .ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return [];
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
            .Where(IsShareable)
            .SelectMany(candidate => candidate.Addresses
                .Where(IsPrivateIPv4)
                .Select(address => (candidate.HasGateway, Address: address)))
            .ToList();

        return usable.Where(entry => entry.HasGateway).Select(entry => entry.Address).FirstOrDefault()
               ?? usable.Select(entry => entry.Address).FirstOrDefault();
    }

    internal static IReadOnlyList<LanAddress> ListShareable(IEnumerable<Candidate> candidates) =>
        candidates
            .Where(IsShareable)
            .SelectMany(candidate => candidate.Addresses
                .Where(IsPrivateIPv4)
                .Select(address => new LanAddress(candidate.Name, address)))
            .ToList();

    internal static LanAddress? ResolveShareAddress(IEnumerable<Candidate> candidates, string nameOrAddress)
    {
        var wanted = nameOrAddress.Trim();
        var up = candidates.Where(candidate => candidate.IsUp).ToList();

        if (IPAddress.TryParse(wanted, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(literal))
            {
                return null;
            }

            var owner = up.FirstOrDefault(candidate => candidate.Addresses.Contains(literal));
            return owner == null ? null : new LanAddress(owner.Name, literal);
        }

        // Named explicitly, so VM and container adapters count too; only loopback is never a LAN.
        var named = up.FirstOrDefault(candidate =>
            candidate.Type != NetworkInterfaceType.Loopback &&
            string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase));
        var address = named?.Addresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .OrderBy(address => IsPrivateIPv4(address) ? 0 : 1)
            .FirstOrDefault();
        return address == null ? null : new LanAddress(named!.Name, address);
    }

    private static bool IsShareable(Candidate candidate) =>
        candidate.IsUp
        && candidate.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        && !LooksVirtual(candidate);

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
