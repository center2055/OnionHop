using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OnionHopV2.Core.Platform.Windows;

internal static class KillSwitchService
{
    private static string RuleName => "OnionHop KillSwitch Emergency Block";
    private static string CleanupTaskName => "OnionHop KillSwitch Cleanup";

    public static void EnableEmergencyBlock(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block profile=any enable=yes");
            EnableFailsafe(log);
            log("Kill switch engaged: outbound traffic blocked.");
        }
        catch (Exception ex)
        {
            log($"Kill switch enable failed: {ex.Message}");
        }
    }

    public static void DisableEmergencyBlock(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
            DisableFailsafe(log);
            log("Kill switch cleared.");
        }
        catch (Exception ex)
        {
            log($"Kill switch disable failed: {ex.Message}");
        }
    }

    public static bool IsEmergencyBlockActive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var output = RunNetshWithOutput($"advfirewall firewall show rule name=\"{RuleName}\"");
            return !string.IsNullOrWhiteSpace(output)
                   && output.Contains(RuleName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void EnableFailsafe(Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || !WindowsAdmin.IsAdministrator())
        {
            return;
        }

        try
        {
            var action = $"cmd /c netsh advfirewall firewall delete rule name=\\\"{RuleName}\\\"";
            RunSchTasks($"/Create /TN \"{CleanupTaskName}\" /TR \"{action}\" /SC ONSTART /RL HIGHEST /RU SYSTEM /F");
        }
        catch (Exception ex)
        {
            log($"Kill switch failsafe setup failed: {ex.Message}");
        }
    }

    private static void DisableFailsafe(Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || !WindowsAdmin.IsAdministrator())
        {
            return;
        }

        try
        {
            RunSchTasks($"/Delete /TN \"{CleanupTaskName}\" /F");
        }
        catch (Exception ex)
        {
            log($"Kill switch failsafe cleanup failed: {ex.Message}");
        }
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return;
        }

        proc.WaitForExit(8000);
    }

    private static string RunNetshWithOutput(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return string.Empty;
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(8000);
        return string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static void RunSchTasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return;
        }

        proc.WaitForExit(8000);
    }
}
