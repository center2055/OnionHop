using System;
using System.Diagnostics;
using System.Linq;
using OnionHopV2.Core.Platform;

namespace OnionHopV2.Core.Platform.MacOS;

internal sealed class MacKillSwitchService : IKillSwitchService
{
    private const string AnchorName = "com.onionhop.killswitch";
    private const string AnchorRules = "block drop out quick all\npass out quick on lo0 all\n";

    public void EnableEmergencyBlock(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (MacSwiftHelper.TryRun("killswitch enable", log))
        {
            return;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            var result = MacAuthorization.RunScript($$"""
                #!/bin/sh
                set -eu
                /sbin/pfctl -E >/dev/null 2>&1 || true
                printf 'block drop out quick all\npass out quick on lo0 all\n' | /sbin/pfctl -a {{MacAuthorization.QuoteShellArgument(AnchorName)}} -f -
                """, requireAdministrator: true);
            if (result.Success)
            {
                log("Kill switch engaged on macOS (pf anchor rules loaded).");
            }
            else
            {
                log($"Kill switch enable failed: {result.FailureMessage}");
            }
            return;
        }

        try
        {
            PlatformHelper.RunCommandSuccess("pfctl", "-E");
            if (!RunPfctlWithInput($"-a {AnchorName} -f -", AnchorRules))
            {
                throw new InvalidOperationException("pfctl failed to load onionhop anchor rules.");
            }

            log("Kill switch engaged on macOS (pf anchor rules loaded).");
        }
        catch (Exception ex)
        {
            log($"Kill switch enable failed: {ex.Message}");
        }
    }

    public void DisableEmergencyBlock(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (MacSwiftHelper.TryRun("killswitch disable", log))
        {
            return;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            var result = MacAuthorization.RunScript($$"""
                #!/bin/sh
                set -eu
                /sbin/pfctl -a {{MacAuthorization.QuoteShellArgument(AnchorName)}} -F all
                """, requireAdministrator: true);
            if (result.Success)
            {
                log("Kill switch cleared on macOS.");
            }
            else
            {
                log($"Kill switch disable failed: {result.FailureMessage}");
            }
            return;
        }

        try
        {
            PlatformHelper.RunCommandSuccess("pfctl", $"-a {AnchorName} -F all");
            log("Kill switch cleared on macOS.");
        }
        catch (Exception ex)
        {
            log($"Kill switch disable failed: {ex.Message}");
        }
    }

    public bool IsEmergencyBlockActive()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var output = PlatformHelper.RunCommand("pfctl", $"-a {AnchorName} -s rules");
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line => line.Contains("block drop out", StringComparison.OrdinalIgnoreCase));
    }

    private static bool RunPfctlWithInput(string args, string input)
    {
        try
        {
            var psi = new ProcessStartInfo("pfctl", args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            process.StandardInput.Write(input);
            process.StandardInput.Close();
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(8000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
