using System;
using System.IO;

namespace OnionHopV3.Core.Platform.MacOS;

public static class MacOSAutoStartService
{
    private const string PlistFileName = "com.onionhop.autostart.plist";

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
                "Library", "LaunchAgents");

            var plistPath = Path.Combine(launchAgentsDir, PlistFileName);

            if (!enabled)
            {
                if (File.Exists(plistPath))
                {
                    PlatformHelper.RunCommand("launchctl", $"unload \"{plistPath}\"");
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

            Directory.CreateDirectory(launchAgentsDir);

            var args = startMinimized
                ? $"    <array>\n      <string>{exePath}</string>\n      <string>--minimized</string>\n    </array>"
                : $"    <array>\n      <string>{exePath}</string>\n    </array>";

            var plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.onionhop.autostart</string>
                    <key>ProgramArguments</key>
                {args}
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """;

            File.WriteAllText(plistPath, plist);
            PlatformHelper.RunCommand("launchctl", $"load \"{plistPath}\"");
        }
        catch (Exception ex)
        {
            log($"Auto-start registration failed: {ex.Message}");
        }
    }
}
