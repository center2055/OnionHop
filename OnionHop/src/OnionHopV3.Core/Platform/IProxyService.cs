using System;

namespace OnionHopV3.Core.Platform;

internal interface IProxyService
{
    bool IsApplied { get; }
    void ApplyTorProxy(int socksPort, int? httpPort, Action<string> log);
    void RestorePreviousProxy(Action<string> log);

    /// <summary>
    /// Clears a system proxy that matches OnionHop's own written shape when it was NOT applied by
    /// this session - i.e. a leftover from a crashed/killed earlier session. Because proxy ports are
    /// picked per session (and drift when busy), such a leftover points browsers at a dead port and
    /// breaks all browsing until cleared ("connected but no websites load", #tester-reports).
    /// Never touches a proxy that does not match OnionHop's exact format. Returns true if cleared.
    /// </summary>
    bool ClearStaleTorProxy(Action<string> log);

    /// <summary>The currently enabled system proxy value, or null when none is enabled (or the
    /// platform does not expose one). Used to hint when a foreign proxy may break TUN-mode browsing.</summary>
    string? GetEnabledSystemProxy();

    /// <summary>
    /// Re-applies the system proxy if something reset it while this session had it applied. Proxy
    /// Mode only protects traffic for as long as the OS proxy actually points at Tor, but Windows
    /// itself, another VPN, a cleanup tool or a browser can clear those settings underneath us. The
    /// applied flag is our own in-memory state, so nothing noticed: the toggle still read ON while
    /// traffic went out direct with the user's real IP, and the only known fix was turning the toggle
    /// off and on again (tester report). Returns true when it had been lost and was restored. A proxy
    /// belonging to some other program is never overwritten.
    /// </summary>
    bool ReapplyIfLost(int socksPort, int? httpPort, Action<string> log);
}
