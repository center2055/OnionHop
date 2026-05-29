using System;
using System.IO;

namespace OnionHopV3.Core.Platform.Linux;

public static class LinuxAutoStartService
{
    private const string DesktopFileName = "onionhop.desktop";

    public static void Update(bool enabled, bool startMinimized, Action<string> log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var autostartDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "autostart");

            var desktopFilePath = Path.Combine(autostartDir, DesktopFileName);

            if (!enabled)
            {
                if (File.Exists(desktopFilePath))
                {
                    File.Delete(desktopFilePath);
                }
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                log("Auto-start registration failed: executable path unavailable.");
                return;
            }

            Directory.CreateDirectory(autostartDir);

            var execLine = startMinimized ? $"{exePath} --minimized" : exePath;
            var desktopEntry = $"""
                [Desktop Entry]
                Type=Application
                Name=OnionHop
                Exec={execLine}
                X-GNOME-Autostart-enabled=true
                Hidden=false
                NoDisplay=false
                Comment=Route traffic through Tor
                """;

            File.WriteAllText(desktopFilePath, desktopEntry);
        }
        catch (Exception ex)
        {
            log($"Auto-start registration failed: {ex.Message}");
        }
    }
}
