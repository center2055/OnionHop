using System;
using System.Diagnostics;
using System.IO;

namespace OnionHopV3.Core.Platform.MacOS;

internal static class MacSwiftHelper
{
    private const string EnvHelperPath = "ONIONHOP_MAC_HELPER_PATH";

    public static bool TryRun(string arguments, Action<string> log)
    {
        return TryRun(arguments, log, logFailures: true);
    }

    public static bool TryRun(string arguments, Action<string> log, bool logFailures)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var helperPath = ResolveHelperPath();
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(helperPath, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(12000);

            if (process.ExitCode == 0)
            {
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    log($"macOS helper: {stdout}");
                }

                return true;
            }

            if (logFailures && !string.IsNullOrWhiteSpace(stderr))
            {
                log($"macOS helper failed: {stderr}");
            }

            return false;
        }
        catch (Exception ex)
        {
            if (logFailures)
            {
                log($"macOS helper invocation failed: {ex.Message}");
            }
            return false;
        }
    }

    private static string? ResolveHelperPath()
    {
        var envPath = Environment.GetEnvironmentVariable(EnvHelperPath);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath.Trim();
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "onionhop-mac-helper"),
            Path.Combine(baseDir, "macos", "onionhop-mac-helper"),
            Path.Combine(baseDir, "macos", "OnionHopMacHelper", "onionhop-mac-helper"),
            Path.Combine(baseDir, "macos", "OnionHopMacHelper", ".build", "release", "onionhop-mac-helper")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
