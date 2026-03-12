using System;
using System.Runtime.InteropServices;

namespace OnionHopV2.Core.Platform;

public static class PlatformHelper
{
    private static readonly bool IsWin = OperatingSystem.IsWindows();
    private static readonly bool IsLinux = OperatingSystem.IsLinux();
    private static readonly bool IsMac = OperatingSystem.IsMacOS();

    public static string TorBinaryName => IsWin ? "tor.exe" : "tor";
    public static string TorGenCertBinaryName => IsWin ? "tor-gencert.exe" : "tor-gencert";
    public static string SingBoxBinaryName => IsWin ? "sing-box.exe" : "sing-box";
    public static string XrayBinaryName => IsWin ? "xray.exe" : "xray";
    public static string LyrebirdBinaryName => IsWin ? "lyrebird.exe" : "lyrebird";
    public static string SnowflakeClientBinaryName => IsWin ? "snowflake-client.exe" : "snowflake-client";
    public static string Obfs4ProxyBinaryName => IsWin ? "obfs4proxy.exe" : "obfs4proxy";
    public static string WebTunnelClientBinaryName => IsWin ? "webtunnel-client.exe" : "webtunnel-client";
    public static string WintunLibraryName => IsWin ? "wintun.dll" : string.Empty;

    public static bool NeedsWintun => IsWin;
    public static bool IsMacOS => IsMac;
    public static bool IsLinuxOS => IsLinux;
    public static bool IsWindowsOS => IsWin;

    public static bool IsAdministrator()
    {
        if (IsWin)
        {
            return Windows.WindowsAdmin.IsAdministrator();
        }

        return IsUnixRoot();
    }

    internal static IProxyService CreateProxyService()
    {
        if (IsWin)
        {
            return new Windows.WindowsProxyService();
        }

        if (IsMac)
        {
            return new MacOS.MacProxyService();
        }

        return new Linux.LinuxProxyService();
    }

    internal static IDnsProxyService CreateDnsProxyService()
    {
        if (IsWin)
        {
            return new Windows.WindowsOnionDnsProxyService();
        }

        if (IsMac)
        {
            return new MacOS.MacDnsProxyService();
        }

        return new Linux.LinuxDnsProxyService();
    }

    internal static IKillSwitchService CreateKillSwitchService()
    {
        if (IsWin)
        {
            return new Windows.WindowsKillSwitchServiceAdapter();
        }

        if (IsMac)
        {
            return new MacOS.MacKillSwitchService();
        }

        return new Linux.LinuxKillSwitchService();
    }

    public static string[] DefaultBrowserProcessNames =>
        IsWin
            ? ["firefox.exe", "chrome.exe", "msedge.exe"]
            : IsMac
                ? ["Safari", "Google Chrome", "Firefox", "firefox", "Brave Browser", "Microsoft Edge"]
            : ["firefox", "chrome", "chromium", "brave"];

    public static string TorExpertBundlePlatformSuffix
    {
        get
        {
            if (IsWin)
            {
                return "windows-x86_64";
            }

            var arch = RuntimeInformation.OSArchitecture;
            var archSuffix = arch switch
            {
                Architecture.Arm64 => "aarch64",
                _ => "x86_64"
            };

            if (IsMac)
            {
                return $"macos-{archSuffix}";
            }

            return $"linux-{archSuffix}";
        }
    }

    public static string SingBoxPlatformAssetFilter
    {
        get
        {
            if (IsWin)
            {
                return "windows-amd64.zip";
            }

            var arch = RuntimeInformation.OSArchitecture;
            var goArch = arch switch
            {
                Architecture.Arm64 => "arm64",
                _ => "amd64"
            };

            if (IsMac)
            {
                return $"darwin-{goArch}.tar.gz";
            }

            return $"linux-{goArch}.tar.gz";
        }
    }

    public static string[] XrayAssetNameHints
    {
        get
        {
            if (IsWin)
            {
                return ["windows", "64", ".zip"];
            }

            var archHint = RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "arm64"
                : "64";

            if (IsMac)
            {
                return ["macos", archHint, ".zip"];
            }

            return ["linux", archHint, ".zip"];
        }
    }

    private static bool IsUnixRoot()
    {
        try
        {
            return NativeMethods.geteuid() == 0;
        }
        catch
        {
            try
            {
                var uid = RunCommand("id", "-u");
                return uid?.Trim() == "0";
            }
            catch
            {
                return false;
            }
        }
    }

    internal static string? RunCommand(string fileName, string arguments, int timeoutMs = 5000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(timeoutMs);
            return output;
        }
        catch
        {
            return null;
        }
    }

    internal static bool RunCommandSuccess(string fileName, string arguments, int timeoutMs = 8000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void RemoveQuarantineOnMacOS(string path)
    {
        if (!IsMac)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                RunCommandSuccess("xattr", $"-d com.apple.quarantine \"{path}\"");
            }
            else if (Directory.Exists(path))
            {
                RunCommandSuccess("xattr", $"-cr \"{path}\"");
            }
        }
        catch
        {
            // Best-effort
        }
    }

    private static class NativeMethods
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern uint geteuid();
    }
}
