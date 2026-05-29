using System;

namespace OnionHopV3.Core.Platform.MacOS;

internal sealed class MacOSDnsProxyService : IDnsProxyService
{
    private bool _enabled;

    public bool Enable(string nameServerAddress, bool routeAllDns, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            log(".onion DNS proxying requires root privileges on macOS.");
            return false;
        }

        if (routeAllDns)
        {
            // Full system-wide DNS-over-Tor leak protection is currently implemented on Windows
            // only. On macOS, prefer TUN/VPN Mode, which forces all DNS through Tor at the tunnel.
            log("Full DNS-over-Tor leak protection is Windows-only for now; routing .onion only. Use TUN/VPN Mode for leak-free DNS on macOS.");
        }

        try
        {
            var safeNameServer = string.IsNullOrWhiteSpace(nameServerAddress)
                ? "127.0.0.1"
                : nameServerAddress.Trim();

            var resolverDir = "/etc/resolver";
            System.IO.Directory.CreateDirectory(resolverDir);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(resolverDir, "onion"),
                $"nameserver {safeNameServer}\n");

            log($".onion DNS proxying enabled via /etc/resolver/onion (nameserver={safeNameServer}).");
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
        if (!OperatingSystem.IsMacOS() || !_enabled)
        {
            return;
        }

        try
        {
            var resolverFile = "/etc/resolver/onion";
            if (System.IO.File.Exists(resolverFile))
            {
                System.IO.File.Delete(resolverFile);
            }

            _enabled = false;
            log(".onion DNS proxying disabled.");
        }
        catch (Exception ex)
        {
            log($".onion DNS proxying cleanup failed: {ex.Message}");
        }
    }
}
