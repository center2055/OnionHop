using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OnionHopV2.Core.Platform.Windows;

internal sealed class WindowsProxyService : IProxyService
{
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private string? _previousProxy;
    private int? _previousProxyEnabled;
    private bool _applied;

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

        var httpValue = httpPort.HasValue
            ? $"http=127.0.0.1:{httpPort.Value};https=127.0.0.1:{httpPort.Value};socks=127.0.0.1:{socksPort}"
            : $"socks=127.0.0.1:{socksPort}";

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

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
