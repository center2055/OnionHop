using System;
using Microsoft.Win32;

namespace OnionHopV3.Core.Platform.Windows;

public static class WindowsAutoStartService
{
    private const string AutoStartRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "OnionHop";

    public static void Update(bool enabled, bool startMinimized, Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(AutoStartRegistryKey);
            if (key == null)
            {
                log("Startup registration failed: registry key not found.");
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(AutoStartValueName, false);
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                log("Startup registration failed: executable path unavailable.");
                return;
            }

            var command = $"\"{exePath}\"";
            if (startMinimized)
            {
                command = $"{command} --minimized";
            }

            key.SetValue(AutoStartValueName, command);
        }
        catch (Exception ex)
        {
            log($"Startup registration failed: {ex.Message}");
        }
    }
}
