using System;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class AdminHelperClientTests
{
    // Starting the tunnel took longer than 30 seconds on a reporter's machine, and the app gave up
    // ("Unable to start elevated VPN helper: Unknown error") while the helper was still at it.
    [Fact]
    public void Commands_that_change_the_system_get_more_time_than_status_polling()
    {
        Assert.True(AdminHelperClient.CommandTimeoutFor("StartVpn") >= TimeSpan.FromMinutes(2));
        Assert.True(AdminHelperClient.CommandTimeoutFor("EnableOnionDnsProxy") >= TimeSpan.FromSeconds(90));
        Assert.True(AdminHelperClient.CommandTimeoutFor("EnableTorOnlyMode") >= TimeSpan.FromMinutes(10));
        Assert.True(AdminHelperClient.CommandTimeoutFor("DisableTorOnlyMode") >= TimeSpan.FromMinutes(10));

        // The VPN monitor polls these every few seconds; a dead helper must still be noticed quickly.
        Assert.Equal(TimeSpan.FromSeconds(30), AdminHelperClient.CommandTimeoutFor("GetStatus"));
        Assert.Equal(TimeSpan.FromSeconds(30), AdminHelperClient.CommandTimeoutFor("DrainLogs"));
    }
}
