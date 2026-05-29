using System;

namespace OnionHopV3.Core.Platform.Windows;

internal sealed class WindowsKillSwitchServiceAdapter : IKillSwitchService
{
    public void EnableEmergencyBlock(Action<string> log) => KillSwitchService.EnableEmergencyBlock(log);
    public void DisableEmergencyBlock(Action<string> log) => KillSwitchService.DisableEmergencyBlock(log);
    public bool IsEmergencyBlockActive() => KillSwitchService.IsEmergencyBlockActive();
}
