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
}
