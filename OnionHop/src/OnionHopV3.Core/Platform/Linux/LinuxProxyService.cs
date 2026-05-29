using System;
using System.Diagnostics;

namespace OnionHopV3.Core.Platform.Linux;

internal sealed class LinuxProxyService : IProxyService
{
    private bool _applied;
    private string? _previousMode;
    private string? _previousSocksHost;
    private string? _previousSocksPort;
    private string? _previousHttpHost;
    private string? _previousHttpPort;
    private string? _previousHttpsHost;
    private string? _previousHttpsPort;
    private bool _savedPrevious;

    public bool IsApplied => _applied;

    public void ApplyTorProxy(int socksPort, int? httpPort, Action<string> log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!_savedPrevious)
        {
            SavePreviousProxy();
            _savedPrevious = true;
        }

        if (TryApplyGsettings(socksPort, httpPort, log))
        {
            _applied = true;
            return;
        }

        if (TryApplyKdeProxy(socksPort, httpPort, log))
        {
            _applied = true;
            return;
        }

        _applied = true;
        if (httpPort.HasValue)
        {
            log($"Proxy enabled: http/https=127.0.0.1:{httpPort.Value}, socks=127.0.0.1:{socksPort} (environment/manual config required for non-GNOME/KDE desktops)");
        }
        else
        {
            log($"Proxy enabled: socks=127.0.0.1:{socksPort} (environment/manual config required for non-GNOME/KDE desktops)");
        }
    }

    public void RestorePreviousProxy(Action<string> log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!_applied)
        {
            return;
        }

        TryRestoreGsettings(log);
        _applied = false;
        log("Proxy disabled (restored previous settings).");
    }

    private static bool TryApplyGsettings(int socksPort, int? httpPort, Action<string> log)
    {
        if (!IsGsettingsAvailable())
        {
            return false;
        }

        try
        {
            RunGsettings("set org.gnome.system.proxy mode 'manual'");
            RunGsettings($"set org.gnome.system.proxy.socks host '127.0.0.1'");
            RunGsettings($"set org.gnome.system.proxy.socks port {socksPort}");

            if (httpPort.HasValue)
            {
                RunGsettings($"set org.gnome.system.proxy.http host '127.0.0.1'");
                RunGsettings($"set org.gnome.system.proxy.http port {httpPort.Value}");
                RunGsettings($"set org.gnome.system.proxy.https host '127.0.0.1'");
                RunGsettings($"set org.gnome.system.proxy.https port {httpPort.Value}");
                log($"Proxy enabled (GNOME gsettings): http/https=127.0.0.1:{httpPort.Value}, socks=127.0.0.1:{socksPort}");
            }
            else
            {
                log($"Proxy enabled (GNOME gsettings): socks=127.0.0.1:{socksPort}");
            }

            return true;
        }
        catch (Exception ex)
        {
            log($"GNOME proxy setup failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryApplyKdeProxy(int socksPort, int? httpPort, Action<string> log)
    {
        if (!IsKwriteconfig5Available())
        {
            return false;
        }

        try
        {
            RunKwriteconfig("--file kioslaverc --group 'Proxy Settings' --key ProxyType 1");
            RunKwriteconfig($"--file kioslaverc --group 'Proxy Settings' --key socksProxy 'socks://127.0.0.1:{socksPort}'");

            if (httpPort.HasValue)
            {
                RunKwriteconfig($"--file kioslaverc --group 'Proxy Settings' --key httpProxy 'http://127.0.0.1:{httpPort.Value}'");
                RunKwriteconfig($"--file kioslaverc --group 'Proxy Settings' --key httpsProxy 'http://127.0.0.1:{httpPort.Value}'");
                log($"Proxy enabled (KDE kwriteconfig): http/https=127.0.0.1:{httpPort.Value}, socks=127.0.0.1:{socksPort}");
            }
            else
            {
                log($"Proxy enabled (KDE kwriteconfig): socks=127.0.0.1:{socksPort}");
            }

            return true;
        }
        catch (Exception ex)
        {
            log($"KDE proxy setup failed: {ex.Message}");
            return false;
        }
    }

    private void SavePreviousProxy()
    {
        try
        {
            if (IsGsettingsAvailable())
            {
                _previousMode = GetGsetting("get org.gnome.system.proxy mode");
                _previousSocksHost = GetGsetting("get org.gnome.system.proxy.socks host");
                _previousSocksPort = GetGsetting("get org.gnome.system.proxy.socks port");
                _previousHttpHost = GetGsetting("get org.gnome.system.proxy.http host");
                _previousHttpPort = GetGsetting("get org.gnome.system.proxy.http port");
                _previousHttpsHost = GetGsetting("get org.gnome.system.proxy.https host");
                _previousHttpsPort = GetGsetting("get org.gnome.system.proxy.https port");
            }
        }
        catch
        {
        }
    }

    private void TryRestoreGsettings(Action<string> log)
    {
        if (!IsGsettingsAvailable())
        {
            return;
        }

        try
        {
            var mode = _previousMode ?? "'none'";
            RunGsettings($"set org.gnome.system.proxy mode {mode}");

            if (!string.IsNullOrWhiteSpace(_previousSocksHost))
            {
                RunGsettings($"set org.gnome.system.proxy.socks host {_previousSocksHost}");
            }
            if (!string.IsNullOrWhiteSpace(_previousSocksPort))
            {
                RunGsettings($"set org.gnome.system.proxy.socks port {_previousSocksPort}");
            }
            if (!string.IsNullOrWhiteSpace(_previousHttpHost))
            {
                RunGsettings($"set org.gnome.system.proxy.http host {_previousHttpHost}");
            }
            if (!string.IsNullOrWhiteSpace(_previousHttpPort))
            {
                RunGsettings($"set org.gnome.system.proxy.http port {_previousHttpPort}");
            }
            if (!string.IsNullOrWhiteSpace(_previousHttpsHost))
            {
                RunGsettings($"set org.gnome.system.proxy.https host {_previousHttpsHost}");
            }
            if (!string.IsNullOrWhiteSpace(_previousHttpsPort))
            {
                RunGsettings($"set org.gnome.system.proxy.https port {_previousHttpsPort}");
            }
        }
        catch (Exception ex)
        {
            log($"Failed to restore GNOME proxy settings: {ex.Message}");
        }
    }

    private static bool IsGsettingsAvailable()
    {
        return PlatformHelper.RunCommand("which", "gsettings") != null
            && !string.IsNullOrWhiteSpace(PlatformHelper.RunCommand("which", "gsettings"));
    }

    private static bool IsKwriteconfig5Available()
    {
        var path = PlatformHelper.RunCommand("which", "kwriteconfig5");
        if (!string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        path = PlatformHelper.RunCommand("which", "kwriteconfig6");
        return !string.IsNullOrWhiteSpace(path);
    }

    private static void RunGsettings(string args) => PlatformHelper.RunCommand("gsettings", args);
    private static string? GetGsetting(string args) => PlatformHelper.RunCommand("gsettings", args);

    private static void RunKwriteconfig(string args)
    {
        var tool = !string.IsNullOrWhiteSpace(PlatformHelper.RunCommand("which", "kwriteconfig6"))
            ? "kwriteconfig6"
            : "kwriteconfig5";
        PlatformHelper.RunCommand(tool, args);
    }
}
