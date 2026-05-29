using System;

namespace OnionHopV3.Core.Platform;

internal interface IKillSwitchService
{
    void EnableEmergencyBlock(Action<string> log);
    void DisableEmergencyBlock(Action<string> log);
    bool IsEmergencyBlockActive();
}
