using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Networking;

internal enum InternetConnectivityState
{
    Online,
    Offline,
    Unknown
}

internal readonly record struct InternetConnectivityReport(InternetConnectivityState State, string Reason);

internal static class InternetConnectivityProbe
{
    private static readonly (IPAddress Address, int Port)[] ProbeEndpoints =
    [
        (IPAddress.Parse("1.1.1.1"), 443),
        (IPAddress.Parse("8.8.8.8"), 443),
        (IPAddress.Parse("9.9.9.9"), 443)
    ];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1400);

    public static async Task<InternetConnectivityReport> CheckAsync(CancellationToken token = default)
    {
        try
        {
            var activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsCandidateInterface)
                .ToList();
            if (activeInterfaces.Count == 0)
            {
                return CreateReport(
                    hasActiveInterfaces: false,
                    hasUsableGateway: false,
                    canReachPublicEndpoint: false);
            }

            var hasUsableGateway = activeInterfaces.Any(HasUsableGateway);
            var canReachPublicEndpoint = await CanReachAnyPublicEndpointAsync(token).ConfigureAwait(false);

            return CreateReport(
                hasActiveInterfaces: true,
                hasUsableGateway,
                canReachPublicEndpoint);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new InternetConnectivityReport(
                InternetConnectivityState.Unknown,
                "Connectivity probe canceled.");
        }
        catch (Exception ex)
        {
            return new InternetConnectivityReport(
                InternetConnectivityState.Unknown,
                $"Connectivity probe unavailable: {ex.Message}");
        }
    }

    internal static InternetConnectivityReport CreateReport(
        bool hasActiveInterfaces,
        bool hasUsableGateway,
        bool canReachPublicEndpoint)
    {
        if (!hasActiveInterfaces)
        {
            return new InternetConnectivityReport(
                InternetConnectivityState.Offline,
                "No active network adapter detected.");
        }

        if (canReachPublicEndpoint)
        {
            return new InternetConnectivityReport(
                InternetConnectivityState.Online,
                "Public internet reachability probe succeeded.");
        }

        if (hasUsableGateway)
        {
            return new InternetConnectivityReport(
                InternetConnectivityState.Unknown,
                "A default network route exists, but quick reachability probes failed.");
        }

        return new InternetConnectivityReport(
            InternetConnectivityState.Unknown,
            "Active network adapter detected, but no default gateway was exposed (common on PPP/PPPoE or route-managed links).");
    }

    private static bool IsCandidateInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        return networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback
               && networkInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel;
    }

    private static bool HasUsableGateway(NetworkInterface networkInterface)
    {
        try
        {
            var properties = networkInterface.GetIPProperties();
            return properties.GatewayAddresses.Any(gateway => IsUsableGatewayAddress(gateway.Address));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUsableGatewayAddress(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.None);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !address.Equals(IPAddress.IPv6None);
        }

        return false;
    }

    private static async Task<bool> CanReachAnyPublicEndpointAsync(CancellationToken token)
    {
        var probeTasks = new List<Task<bool>>(ProbeEndpoints.Length);
        foreach (var endpoint in ProbeEndpoints)
        {
            probeTasks.Add(ProbeEndpointAsync(endpoint.Address, endpoint.Port, token));
        }

        while (probeTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(probeTasks).ConfigureAwait(false);
            probeTasks.Remove(completedTask);
            if (await completedTask.ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> ProbeEndpointAsync(IPAddress address, int port, CancellationToken token)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(ProbeTimeout);

            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
