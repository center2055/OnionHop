using System;

namespace OnionHopV2.Core.Platform.Linux;

internal sealed class LinuxDnsProxyService : IDnsProxyService
{
    private bool _enabled;

    public bool Enable(string nameServerAddress, Action<string> log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            log(".onion DNS proxying requires root privileges on Linux.");
            return false;
        }

        try
        {
            var safeNameServer = string.IsNullOrWhiteSpace(nameServerAddress)
                ? "127.0.0.1"
                : nameServerAddress.Trim();

            if (IsResolvectlAvailable())
            {
                PlatformHelper.RunCommandSuccess("resolvectl", $"dns onionhop {safeNameServer}");
                PlatformHelper.RunCommandSuccess("resolvectl", "domain onionhop ~onion");
                log($".onion DNS proxying enabled via resolvectl (nameserver={safeNameServer}).");
                _enabled = true;
                return true;
            }

            log(".onion DNS proxying: resolvectl not available. Manual DNS configuration may be needed.");
            _enabled = true;
            return true;
        }
        catch (Exception ex)
        {
            log($".onion DNS proxying enable failed: {ex.Message}");
            return false;
        }
    }

    public void Disable(Action<string> log)
    {
        if (!OperatingSystem.IsLinux() || !_enabled)
        {
            return;
        }

        try
        {
            if (IsResolvectlAvailable())
            {
                PlatformHelper.RunCommandSuccess("resolvectl", "revert onionhop");
            }

            _enabled = false;
            log(".onion DNS proxying disabled.");
        }
        catch (Exception ex)
        {
            log($".onion DNS proxying cleanup failed: {ex.Message}");
        }
    }

    private static bool IsResolvectlAvailable()
    {
        var path = PlatformHelper.RunCommand("which", "resolvectl");
        return !string.IsNullOrWhiteSpace(path);
    }
}
