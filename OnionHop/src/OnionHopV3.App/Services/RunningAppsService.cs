using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OnionHopV3.App.Services;

/// <summary>A user-facing running application: its image name (e.g. "browser.exe") and a friendly display name (e.g. "Yandex Browser").</summary>
public sealed record RunningAppInfo(string ExecutableName, string DisplayName);

/// <summary>
/// Enumerates currently running, user-facing applications so the split-tunnel picker can offer a
/// pick-from-a-list experience instead of forcing users to guess executable names. Supported on
/// Windows, Linux and macOS (TUN/Hybrid split tunneling matches sing-box <c>process_name</c> rules,
/// which work on all three). Returns an empty list on unsupported platforms.
/// </summary>
public static class RunningAppsService
{
    // Our own process and common shell/host surfaces that are never useful split-tunnel targets.
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnionHopV3.exe", "OnionHopV2.exe", "explorer.exe", "ApplicationFrameHost.exe",
        "TextInputHost.exe", "SystemSettings.exe", "ShellExperienceHost.exe", "SearchHost.exe",
        "StartMenuExperienceHost.exe", "LockApp.exe", "dwm.exe", "svchost.exe",
        "RuntimeBroker.exe", "SearchApp.exe", "WidgetService.exe", "fontdrvhost.exe",
        "csrss.exe", "winlogon.exe", "sihost.exe", "ctfmon.exe"
    };

    // Daemons, shells, coreutils and desktop plumbing that are running under the user's session but
    // are not things anyone would route per-app. Linux process names from /proc are truncated to 15
    // chars (TASK_COMM_LEN), so a few entries are pre-truncated to match. macOS names are not truncated.
    private static readonly HashSet<string> UnixExcluded = new(StringComparer.OrdinalIgnoreCase)
    {
        // Our own binaries / helpers
        "OnionHopV3", "OnionHopV3.App", "OnionHop", "tor", "lyrebird", "snowflake-client",
        "conjure-client", "webtunnel-client", "dnstt-client", "sing-box", "xray",
        // init / session / bus
        "systemd", "systemd-logind", "systemd-userwor", "systemd-journal", "systemd-resolve",
        "systemd-timesyn", "systemd-udevd", "systemd-oomd", "dbus-daemon", "dbus-broker",
        "dbus-broker-lau", "(sd-pam)", "init", "launchd",
        // audio / portals / keyring / accessibility
        "pipewire", "pipewire-pulse", "pipewire-media-", "wireplumber", "pulseaudio",
        "gnome-keyring-d", "ssh-agent", "gpg-agent", "at-spi2-registr", "at-spi-bus-laun",
        "xdg-desktop-por", "xdg-document-po", "xdg-permission-", "xdg-desktop-portal",
        // gvfs / trackers / indexers
        "gvfsd", "gvfsd-fuse", "gvfsd-trash", "gvfs-udisks2-vo", "gvfs-afc-volume",
        "gvfs-goa-volume", "gvfs-gphoto2-vo", "gvfs-mtp-volume", "tracker-miner-f", "tracker-extract",
        // shells / coreutils / scripting
        "bash", "sh", "zsh", "fish", "dash", "tcsh", "ksh", "sleep", "cat", "grep", "sed", "awk",
        "tail", "head", "less", "more", "tmux", "screen", "ssh", "scp", "node", "python", "python3",
        // display / compositor plumbing (not per-app routable)
        "Xorg", "Xwayland", "gnome-shell-cal", "gsd-print-notif", "gsd-power", "gsd-media-keys",
        "gsd-color", "gsd-housekeepin", "gsd-keyboard", "gsd-sound", "gsd-xsettings",
        "ibus-daemon", "ibus-x11", "ibus-portal", "ibus-engine-sim", "fcitx5", "obexd",
        // macOS background agents
        "loginwindow", "WindowServer", "Dock", "SystemUIServer", "Spotlight",
        "coreaudiod", "cfprefsd", "distnoted", "secd", "trustd", "nsurlsessiond",
        "mds", "mds_stores", "mdworker", "mdworker_shared", "syspolicyd"
    };

    public static IReadOnlyList<RunningAppInfo> GetRunningApps()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsRunningApps();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return GetUnixRunningApps();
        }

        return Array.Empty<RunningAppInfo>();
    }

    private static IReadOnlyList<RunningAppInfo> GetWindowsRunningApps()
    {
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

    // Linux/macOS have no Win32 MainWindowHandle, so we approximate "user app" as: a process owned by
    // the current user (its executable path under /proc is readable — kernel threads and other users'
    // processes throw and are skipped) whose binary name is not obvious system plumbing. sing-box's
    // process_name routing on Linux matches the executable basename, so that is exactly what we expose.
    private static IReadOnlyList<RunningAppInfo> GetUnixRunningApps()
    {
        var apps = new Dictionary<string, RunningAppInfo>(StringComparer.OrdinalIgnoreCase);
        var selfId = -1;
        try { selfId = Environment.ProcessId; } catch { /* best effort */ }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == selfId)
                {
                    continue;
                }

                // Prefer the real executable name from the /proc/<pid>/exe (Linux) or the process
                // image (macOS). MainModule throws for inaccessible (other-user / kernel) processes,
                // which conveniently filters out everything we cannot route anyway.
                string? exePath = null;
                try
                {
                    exePath = process.MainModule?.FileName;
                }
                catch
                {
                    exePath = null;
                }

                string baseName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    baseName = Path.GetFileName(exePath!);
                }
                else
                {
                    // No readable exe path (common on macOS for sandboxed apps): fall back to the
                    // process name, which macOS reports in full.
                    baseName = process.ProcessName;
                }

                if (string.IsNullOrWhiteSpace(baseName) ||
                    baseName.StartsWith('(') ||            // kernel thread, e.g. (sd-pam)
                    baseName.StartsWith('[') ||
                    UnixExcluded.Contains(baseName) ||
                    apps.ContainsKey(baseName))
                {
                    continue;
                }

                apps[baseName] = new RunningAppInfo(baseName, Prettify(baseName));
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

    // "google-chrome-stable" -> "Google Chrome Stable"; leaves already-mixed-case names (e.g. "Telegram")
    // mostly intact so well-known apps still read naturally.
    private static string Prettify(string name)
    {
        var stem = name;
        var dot = stem.LastIndexOf('.');
        if (dot > 0 && (stem.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                        stem.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)))
        {
            stem = stem[..dot];
        }

        var parts = stem.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return name;
        }

        var words = parts.Select(p =>
            p.Length > 1 && p.Any(char.IsUpper)
                ? p // preserve intentional CamelCase / brand casing
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p.ToLowerInvariant()));
        return string.Join(' ', words);
    }
}
