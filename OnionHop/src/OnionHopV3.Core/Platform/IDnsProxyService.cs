using System;

namespace OnionHopV3.Core.Platform;

internal interface IDnsProxyService
{
    // When routeAllDns is true, every DNS query on the system is routed to the Tor DNS
    // resolver (full DNS-over-Tor leak protection); otherwise only the .onion namespace is.
    bool Enable(string nameServerAddress, bool routeAllDns, Action<string> log);
    void Disable(Action<string> log);
}
