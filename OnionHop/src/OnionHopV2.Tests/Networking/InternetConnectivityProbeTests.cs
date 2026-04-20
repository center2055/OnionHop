using OnionHopV2.Core.Networking;
using Xunit;

namespace OnionHopV2.Tests.Networking;

public sealed class InternetConnectivityProbeTests
{
    [Fact]
    public void CreateReport_NoActiveInterfaces_ReturnsOffline()
    {
        var report = InternetConnectivityProbe.CreateReport(
            hasActiveInterfaces: false,
            hasUsableGateway: false,
            canReachPublicEndpoint: false);

        Assert.Equal(InternetConnectivityState.Offline, report.State);
        Assert.Equal("No active network adapter detected.", report.Reason);
    }

    [Fact]
    public void CreateReport_GatewaylessLinkWithReachability_ReturnsOnline()
    {
        var report = InternetConnectivityProbe.CreateReport(
            hasActiveInterfaces: true,
            hasUsableGateway: false,
            canReachPublicEndpoint: true);

        Assert.Equal(InternetConnectivityState.Online, report.State);
        Assert.Equal("Public internet reachability probe succeeded.", report.Reason);
    }

    [Fact]
    public void CreateReport_GatewaylessLinkWithoutReachability_ReturnsUnknown()
    {
        var report = InternetConnectivityProbe.CreateReport(
            hasActiveInterfaces: true,
            hasUsableGateway: false,
            canReachPublicEndpoint: false);

        Assert.Equal(InternetConnectivityState.Unknown, report.State);
        Assert.Contains("PPPoE", report.Reason);
    }

    [Fact]
    public void CreateReport_DefaultRouteWithoutReachability_ReturnsUnknown()
    {
        var report = InternetConnectivityProbe.CreateReport(
            hasActiveInterfaces: true,
            hasUsableGateway: true,
            canReachPublicEndpoint: false);

        Assert.Equal(InternetConnectivityState.Unknown, report.State);
        Assert.Equal("A default network route exists, but quick reachability probes failed.", report.Reason);
    }
}
