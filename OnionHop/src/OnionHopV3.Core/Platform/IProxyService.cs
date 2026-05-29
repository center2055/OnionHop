using System;

namespace OnionHopV3.Core.Platform;

internal interface IProxyService
{
    bool IsApplied { get; }
    void ApplyTorProxy(int socksPort, int? httpPort, Action<string> log);
    void RestorePreviousProxy(Action<string> log);
}
