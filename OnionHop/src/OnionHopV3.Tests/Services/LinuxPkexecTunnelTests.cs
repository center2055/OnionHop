using System.Diagnostics;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// TUN mode on Linux without root: the tunnel core runs as root through pkexec, from a runner script
/// in a private directory, like the macOS administrator prompt (#90).
/// </summary>
public sealed class LinuxPkexecTunnelTests
{
    private static string Runner(bool linux, string tunInterface = "", bool manageOnionResolver = false, int appPid = 0) =>
        VpnService.BuildPrivilegedRunnerScript(
            sessionDir: "/run/user/1000/onionhop-tun",
            workDir: "/home/jane/.local/share/OnionHop/vpn",
            vpnCorePath: "/home/jane/.local/share/OnionHop/vpn/sing-box",
            configPath: "/run/user/1000/onionhop-tun/sing-box.json",
            logPath: "/run/user/1000/onionhop-tun/sing-box.log",
            supervisorPidPath: "/run/user/1000/onionhop-tun/supervisor.pid",
            corePidPath: "/run/user/1000/onionhop-tun/core.pid",
            exitCodePath: "/run/user/1000/onionhop-tun/exit.code",
            stopMarkerPath: "/run/user/1000/onionhop-tun/stop.marker",
            startedMarkerPath: "/run/user/1000/onionhop-tun/started.marker",
            socksPort: 9050,
            tunInterface: tunInterface,
            startupDelayMs: 2000,
            manageOnionResolver: manageOnionResolver,
            onionDnsNameServer: null,
            linux: linux,
            appPid: appPid);

    [Fact]
    public void The_linux_runner_uses_linux_routes_and_none_of_the_macos_parts()
    {
        var script = Runner(linux: true, tunInterface: "xray0", manageOnionResolver: true, appPid: 4242);

        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("ip route add 0.0.0.0/1 dev 'xray0'", script);
        Assert.Contains("ip route del 128.0.0.0/1 dev 'xray0'", script);
        Assert.DoesNotContain("/sbin/route", script);
        Assert.DoesNotContain("/etc/resolver", script);
        Assert.DoesNotContain("/usr/bin/nc", script);
    }

    [Fact]
    public void The_linux_runner_stops_the_core_when_the_app_is_gone()
    {
        // A crashed OnionHop must not leave a root tunnel up that nothing can stop without a password.
        var script = Runner(linux: true, appPid: 4242);

        Assert.Contains("kill -0 4242", script);
        Assert.Contains(": > '/run/user/1000/onionhop-tun/stop.marker'", script);
    }

    [Fact]
    public void The_macos_runner_is_unchanged()
    {
        var script = Runner(linux: false, tunInterface: "utun9", manageOnionResolver: true);

        Assert.Contains("/sbin/route add -net 0.0.0.0/1 -interface 'utun9'", script);
        Assert.Contains("/etc/resolver/onion", script);
        Assert.Contains("/usr/bin/nc -z 127.0.0.1 9050", script);
        Assert.DoesNotContain("kill -0 0", script);
    }

    [Fact]
    public void The_session_directory_is_private_to_the_user()
    {
        // Root runs the script and reads the config from here, so never the shared /tmp.
        Assert.Equal(Path.Combine("/run/user/1000", "onionhop-tun"), VpnService.ResolveLinuxSessionDir("/run/user/1000", "/home/jane"));
        Assert.Equal(Path.Combine("/home/jane", ".cache", "onionhop-tun"), VpnService.ResolveLinuxSessionDir(null, "/home/jane"));
        Assert.Equal(Path.Combine("/home/jane", ".cache", "onionhop-tun"), VpnService.ResolveLinuxSessionDir("relative/dir", "/home/jane"));
    }

    [Fact]
    public void A_dismissed_password_dialog_is_reported_as_such()
    {
        Assert.Equal(
            "Root access for the tunnel was not granted (the password dialog was closed).",
            VpnService.DescribePkexecFailure(126, null));
        Assert.Equal(
            "pkexec could not start the tunnel (exit code 127): Error executing command as another user: No authentication agent found.",
            VpnService.DescribePkexecFailure(127, "Error executing command as another user: No authentication agent found.\n"));
    }

    [Fact]
    public void The_generated_scripts_are_valid_shell()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no POSIX shell to ask; CI runs this on Linux
        }

        foreach (var script in new[]
                 {
                     Runner(linux: true, tunInterface: "xray0", appPid: 4242),
                     Runner(linux: false, tunInterface: "utun9", manageOnionResolver: true),
                     VpnService.BuildPrivilegedStopScript("/s/supervisor.pid", "/s/core.pid", "/s/exit.code", "xray", false, "xray0", linux: true)
                 })
        {
            var path = Path.Combine(Path.GetTempPath(), $"onionhop-runner-{Guid.NewGuid():N}.sh");
            File.WriteAllText(path, script);
            try
            {
                using var check = Process.Start(new ProcessStartInfo("sh", $"-n {path}") { RedirectStandardError = true })!;
                var errors = check.StandardError.ReadToEnd();
                check.WaitForExit();
                Assert.True(check.ExitCode == 0, errors);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
