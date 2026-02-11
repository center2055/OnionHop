using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace OnionHopV2.Core.Services;

internal static class PortSelector
{
    public static int FindAvailablePort(int preferredPort, int additionalAttempts = 20, IReadOnlyCollection<int>? excludedPorts = null)
    {
        var activePorts = GetActivePorts();
        var excluded = excludedPorts == null || excludedPorts.Count == 0
            ? null
            : new HashSet<int>(excludedPorts);

        if (!activePorts.Contains(preferredPort) && (excluded == null || !excluded.Contains(preferredPort)))
        {
            return preferredPort;
        }

        for (var offset = 1; offset <= additionalAttempts; offset++)
        {
            var candidate = preferredPort + offset;
            if (!activePorts.Contains(candidate) && (excluded == null || !excluded.Contains(candidate)))
            {
                return candidate;
            }
        }

        // Fallback scan over a conservative local range.
        for (var candidate = 10000; candidate <= 12000; candidate++)
        {
            if (!activePorts.Contains(candidate) && (excluded == null || !excluded.Contains(candidate)))
            {
                return candidate;
            }
        }

        return preferredPort;
    }

    public static bool IsTcpAndUdpEndpointAvailable(IPAddress address, int port)
    {
        return CanBind(address, port, SocketType.Stream, ProtocolType.Tcp)
               && CanBind(address, port, SocketType.Dgram, ProtocolType.Udp);
    }

    private static bool CanBind(IPAddress address, int port, SocketType socketType, ProtocolType protocolType)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, socketType, protocolType)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static HashSet<int> GetActivePorts()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var active = new HashSet<int>();

        foreach (var endpoint in properties.GetActiveTcpListeners())
        {
            active.Add(endpoint.Port);
        }

        foreach (var endpoint in properties.GetActiveUdpListeners())
        {
            active.Add(endpoint.Port);
        }

        return active;
    }
}
