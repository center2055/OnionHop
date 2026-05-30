using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OnionHopV3.App.Services;

/// <summary>A user-facing running application: its image name (e.g. "browser.exe") and a friendly display name (e.g. "Yandex Browser").</summary>
public sealed record RunningAppInfo(string ExecutableName, string DisplayName);

/// <summary>
/// Enumerates currently running, user-facing applications so the split-tunnel picker can offer a
/// pick-from-a-list experience instead of forcing users to guess executable names. Windows only
/// (TUN/Hybrid mode is Windows); returns an empty list on other platforms.
/// </summary>
public static class RunningAppsService
{
    // Our own process and common shell/host surfaces that are never useful split-tunnel targets.
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnionHopV3.exe", "OnionHopV2.exe", "explorer.exe", "ApplicationFrameHost.exe",
        "TextInputHost.exe", "SystemSettings.exe", "ShellExperienceHost.exe", "SearchHost.exe",
        "StartMenuExperienceHost.exe", "LockApp.exe", "dwm.exe"
    };

    public static IReadOnlyList<RunningAppInfo> GetRunningApps()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<RunningAppInfo>();
        }

        var apps = new Dictionary<string, RunningAppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                // Only processes that own a visible top-level window are real user apps.
                if (process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                var name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
                if (Excluded.Contains(exe) || apps.ContainsKey(exe))
                {
                    continue;
                }

                var display = name;
                try
                {
                    var description = process.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        display = description.Trim();
                    }
                }
                catch
                {
                    // Protected/elevated/cross-bitness process: fall back to the image name.
                }

                apps[exe] = new RunningAppInfo(exe, display);
            }
            catch
            {
                // Process exited or is otherwise inaccessible; skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return apps.Values
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
