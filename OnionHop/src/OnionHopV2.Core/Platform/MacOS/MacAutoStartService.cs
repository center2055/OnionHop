using System;
using System.Diagnostics;
using System.IO;

namespace OnionHopV2.Core.Platform.MacOS;

public static class MacAutoStartService
{
    private const string LaunchAgentName = "com.onionhop.v2";

    public static void Update(bool enabled, bool startMinimized, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var launchAgentsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "LaunchAgents");
            Directory.CreateDirectory(launchAgentsDir);

            var plistPath = Path.Combine(launchAgentsDir, $"{LaunchAgentName}.plist");
            if (!enabled)
            {
                TryRunLaunchCtl($"unload \"{plistPath}\"");
                if (File.Exists(plistPath))
                {
                    File.Delete(plistPath);
                }
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                log("Auto-start registration failed: executable path unavailable.");
                return;
            }

            var args = startMinimized
                ? $"<string>{EscapeXml(exePath)}</string><string>--minimized</string>"
                : $"<string>{EscapeXml(exePath)}</string>";

            var plist = $$"""
                           <?xml version="1.0" encoding="UTF-8"?>
                           <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                           <plist version="1.0">
                           <dict>
                             <key>Label</key>
                             <string>{{LaunchAgentName}}</string>
                             <key>ProgramArguments</key>
                             <array>
                               {{args}}
                             </array>
                             <key>RunAtLoad</key>
                             <true/>
                           </dict>
                           </plist>
                           """;

            File.WriteAllText(plistPath, plist);
            TryRunLaunchCtl($"unload \"{plistPath}\"");
            if (!TryRunLaunchCtl($"load \"{plistPath}\""))
            {
                log("Auto-start registration warning: launchctl load failed. The agent file was written.");
            }
        }
        catch (Exception ex)
        {
            log($"Auto-start registration failed: {ex.Message}");
        }
    }

    private static bool TryRunLaunchCtl(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("launchctl", arguments)
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

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
