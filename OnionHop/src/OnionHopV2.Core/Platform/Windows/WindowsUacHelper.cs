using System;
using System.Diagnostics;
using OnionHopV2.Core.Platform.Windows;

namespace OnionHopV2.Core.Platform.Windows;

/// <summary>
/// Provides UAC elevation functionality for Windows.
/// </summary>
public static class WindowsUacHelper
{
    /// <summary>
    /// Attempts to relaunch the current application with administrator privileges.
    /// If already running as admin, returns true immediately.
    /// If elevation is triggered, the current process exits after launching the elevated one.
    /// </summary>
    /// <returns>True if already admin; false if elevation failed.</returns>
    public static bool TryElevate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (WindowsAdmin.IsAdministrator())
        {
            return true;
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Verb = "runas",
                UseShellExecute = true
            };

            Process.Start(startInfo);
            Environment.Exit(0);
        }
        catch
        {
            // User declined UAC or other error
            return false;
        }

        return true;
    }
}
