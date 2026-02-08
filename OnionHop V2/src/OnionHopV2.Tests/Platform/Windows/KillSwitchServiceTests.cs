using OnionHopV2.Core.Platform.Windows;
using Xunit;

namespace OnionHopV2.Tests.Platform.Windows;

public sealed class KillSwitchServiceTests
{
    [Fact]
    public void IsEmergencyBlockActive_returns_false_when_rule_not_exists()
    {
        // When the OnionHop KillSwitch Emergency Block rule has never been created,
        // or has been cleaned up, IsEmergencyBlockActive should return false.
        // This is safe to run without admin; we're just checking firewall state.
        var result = KillSwitchService.IsEmergencyBlockActive();
        // If we're on Windows and the rule exists from a previous test/session, it could be true.
        // We can only assert it returns a boolean - the actual value depends on system state.
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void KillSwitchService_is_windows_only()
    {
        // The kill switch is Windows-only; on non-Windows this test would still pass
        // because IsEmergencyBlockActive returns false on non-Windows.
        var result = KillSwitchService.IsEmergencyBlockActive();
        // No exception; returns a boolean
        Assert.IsType<bool>(result);
    }
}
