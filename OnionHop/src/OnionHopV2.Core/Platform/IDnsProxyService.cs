using System;

namespace OnionHopV2.Core.Platform;

internal interface IDnsProxyService
{
    bool Enable(string nameServerAddress, Action<string> log);
    void Disable(Action<string> log);
}
