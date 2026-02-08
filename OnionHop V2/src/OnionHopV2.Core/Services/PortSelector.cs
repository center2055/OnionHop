using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace OnionHopV2.Core.Services;

internal static class PortSelector
{
    public static int FindAvailablePort(int preferredPort, int additionalAttempts = 20)
    {
        var activePorts = GetActivePorts();
        if (!activePorts.Contains(preferredPort))
        {
            return preferredPort;
        }

        for (var offset = 1; offset <= additionalAttempts; offset++)
        {
            var candidate = preferredPort + offset;
            if (!activePorts.Contains(candidate))
            {
                return candidate;
            }
        }

        // Fallback scan over a conservative local range.
        for (var candidate = 10000; candidate <= 12000; candidate++)
        {
            if (!activePorts.Contains(candidate))
            {
                return candidate;
            }
        }

        return preferredPort;
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
