using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OnionHopV3.Core.Platform.Windows;

internal sealed class WindowsProxyService : IProxyService
{
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private string? _previousProxy;
    private int? _previousProxyEnabled;
    private bool _applied;
    // Last foreign proxy we warned about, so the periodic check does not repeat itself every tick.
    private string? _foreignProxyWarned;

    public bool IsApplied => _applied;

    public void ApplyTorProxy(int socksPort, int? httpPort, Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
        if (key == null)
        {
            log("Proxy update failed: registry key not found.");
            return;
        }

        _previousProxy ??= key.GetValue("ProxyServer") as string;
        if (_previousProxyEnabled == null && key.GetValue("ProxyEnable") is int enabledValue)
        {
            _previousProxyEnabled = enabledValue;
        }

        var httpValue = BuildProxyValue(socksPort, httpPort);

        key.SetValue("ProxyServer", httpValue, RegistryValueKind.String);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);

        _applied = true;
        if (httpPort.HasValue)
        {
            log($"Proxy enabled: http/https=127.0.0.1:{httpPort.Value}, socks=127.0.0.1:{socksPort}");
        }
        else
        {
            log($"Proxy enabled: socks=127.0.0.1:{socksPort}");
        }

        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    public void RestorePreviousProxy(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!_applied)
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
        if (key == null)
        {
            log("Proxy update failed: registry key not found.");
            return;
        }

        key.SetValue("ProxyEnable", _previousProxyEnabled ?? 0, RegistryValueKind.DWord);
        if (_previousProxy is not null)
        {
            key.SetValue("ProxyServer", _previousProxy, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyServer", false);
        }

        _applied = false;
        log("Proxy disabled (restored previous settings).");

        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    public string? GetEnabledSystemProxy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: false);
            if (key?.GetValue("ProxyEnable") is int enabled && enabled != 0)
            {
                return key.GetValue("ProxyServer") as string;
            }
        }
        catch
        {
            // Best-effort read; a hint must never break a connect.
        }

        return null;
    }

    /// <summary>The exact ProxyServer string this session writes, and therefore the only value that
    /// counts as "still ours" when checking whether the proxy survived.</summary>
    internal static string BuildProxyValue(int socksPort, int? httpPort) =>
        httpPort.HasValue
            ? $"http=127.0.0.1:{httpPort.Value};https=127.0.0.1:{httpPort.Value};socks=127.0.0.1:{socksPort}"
            : $"socks=127.0.0.1:{socksPort}";

    public bool ReapplyIfLost(int socksPort, int? httpPort, Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || !_applied)
        {
            // Not applied by us means there is nothing of ours to lose.
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
            if (key == null)
            {
                return false;
            }

            var expected = BuildProxyValue(socksPort, httpPort);
            var enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;
            var server = key.GetValue("ProxyServer") as string;

            if (enabled && string.Equals(server, expected, StringComparison.OrdinalIgnoreCase))
            {
                _foreignProxyWarned = null;
                return false;
            }

            if (enabled && !IsOnionHopProxyValue(server))
            {
                // Another program now owns the system proxy. Overwriting it would be us fighting a
                // setting the user may have made on purpose, so say what happened instead: their
                // traffic is no longer going through Tor and only they can decide which wins.
                if (!string.Equals(_foreignProxyWarned, server, StringComparison.OrdinalIgnoreCase))
                {
                    _foreignProxyWarned = server;
                    log($"System proxy is no longer OnionHop's: it now points at {server}. Traffic is NOT going through Tor. " +
                        "Another program changed it, so OnionHop will not overwrite it. Disconnect and reconnect once that program is closed.");
                }

                return false;
            }

            // Either it was switched off under us, or it is our own shape with a previous session's
            // ports. Both mean traffic is leaving direct right now, so put it back.
            key.SetValue("ProxyServer", expected, RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            _foreignProxyWarned = null;
            log("System proxy had been reset while connected; OnionHop put it back. " +
                "Traffic was going out directly until now, so re-check anything sensitive you did in the meantime.");
            return true;
        }
        catch (Exception ex)
        {
            log($"System proxy re-check failed: {ex.Message}");
            return false;
        }
    }

    public bool ClearStaleTorProxy(Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || _applied)
        {
            // When applied by this session the proxy is live and owned; nothing is stale.
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
            if (key == null)
            {
                return false;
            }

            if (key.GetValue("ProxyEnable") is not int enabled || enabled == 0)
            {
                return false;
            }

            var server = key.GetValue("ProxyServer") as string;
            if (!IsOnionHopProxyValue(server))
            {
                // Somebody else's proxy (corporate, AV, a local tool) - never touch it.
                return false;
            }

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            log($"Cleared a stale system proxy left by a previous OnionHop session ({server}). " +
                "It pointed browsers at a port from that session and would have broken browsing.");
            return true;
        }
        catch (Exception ex)
        {
            log($"Stale proxy check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when a ProxyServer value matches exactly the shapes OnionHop itself writes in
    /// <see cref="ApplyTorProxy"/>: "socks=127.0.0.1:PORT" or
    /// "http=127.0.0.1:PORT;https=127.0.0.1:PORT;socks=127.0.0.1:PORT". Anything else (a corporate
    /// proxy, another local tool on 127.0.0.1, a plain host:port) is not ours and is never cleared.
    /// </summary>
    internal static bool IsOnionHopProxyValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(
                   trimmed, @"^socks=127\.0\.0\.1:\d{1,5}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
               || System.Text.RegularExpressions.Regex.IsMatch(
                   trimmed,
                   @"^http=127\.0\.0\.1:(\d{1,5});https=127\.0\.0\.1:\1;socks=127\.0\.0\.1:\d{1,5}$",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
