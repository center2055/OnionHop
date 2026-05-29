using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV2.Core;
using OnionHopV2.Core.Platform;
using OnionHopV2.Core.Platform.MacOS;

namespace OnionHopV2.Core.Services;

internal sealed class VpnService : IDisposable
{
    private const string TunInterfaceName = "OnionHop";
    private const string RouteHalfDefaultA = "0.0.0.0";
    private const string RouteHalfDefaultB = "128.0.0.0";
    private const string XrayPreferredTunAddress = "172.19.0.1";
    private const string XrayPreferredTunMask = "255.255.255.252";
    private const int XrayRouteMetric = 1;
    private readonly Action<string> _log;
    private readonly object _processLock = new();
    private Process? _process;
    private bool _xrayRoutesApplied;
    private int? _lastExitCode;
    private bool _disposed;
    private string? _xrayTunInterface;
    private CancellationTokenSource? _macPrivilegedMonitorCts;
    private string? _macPrivilegedSessionDir;
    private string? _macPrivilegedRunnerScriptPath;
    private string? _macPrivilegedLogPath;
    private string? _macPrivilegedSupervisorPidPath;
    private string? _macPrivilegedCorePidPath;
    private string? _macPrivilegedExitCodePath;
    private string? _macPrivilegedCoreMode;
    private string? _macPrivilegedTunInterface;
    private bool _macPrivilegedManagesOnionResolver;
    private string? _macPrivilegedStopMarkerPath;
    private string? _macPrivilegedStartedMarkerPath;
    private string? _macPrivilegedConfigKey;
    private long _macPrivilegedLogOffset;
    private Process? _macPrivilegedOsascriptProcess;

    public VpnService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event DataReceivedEventHandler? OutputReceived;
    public event Action<string>? OutputLineReceived;
    public event EventHandler? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_processLock)
            {
                if (_process != null)
                {
                    return !_process.HasExited;
                }
            }

            return IsMacPrivilegedTunnelRunning();
        }
    }

    public int? ExitCode { get { lock (_processLock) { return _lastExitCode ?? (_process?.HasExited == true ? _process.ExitCode : null); } } }

    public async Task StartAsync(VpnLaunchConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VpnService));
        }

        token.ThrowIfCancellationRequested();
        var launch = await PrepareLaunchArtifactsAsync(config, token).ConfigureAwait(false);

        if (OperatingSystem.IsMacOS() && !PlatformHelper.IsAdministrator())
        {
            _lastExitCode = null;
            if (!CanReuseMacPrivilegedTunnel(launch.ConfigKey))
            {
                Stop();
                await LaunchMacPrivilegedTunnelAsync(launch, config, token).ConfigureAwait(false);
            }

            await WaitForMacPrivilegedTunnelReadyAsync(launch.VpnCoreLabel, launch.StartupDelayMs, token).ConfigureAwait(false);
            return;
        }

        Stop();
        _lastExitCode = null;

        if (OperatingSystem.IsWindows())
        {
            KillStaleVpnCores();
        }

        var psi = new ProcessStartInfo(launch.VpnCorePath, $"run -c \"{launch.ConfigPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkDir
        };

        // Capture startup output for crash diagnostics.
        var startupLines = new List<string>();
        void CaptureStartupOutput(object s, DataReceivedEventArgs args)
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                lock (startupLines)
                {
                    startupLines.Add(args.Data);
                }
            }
        }

        _process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _process.Exited += HandleExited;
        _process.OutputDataReceived += HandleOutput;
        _process.OutputDataReceived += CaptureStartupOutput;
        _process.ErrorDataReceived += HandleOutput;
        _process.ErrorDataReceived += CaptureStartupOutput;

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Unable to launch {launch.VpnCoreLabel}.");
        }

        config.ProcessStarted?.Invoke(_process);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(launch.StartupDelayMs, token);

        // Detach startup capture.
        try
        {
            _process.OutputDataReceived -= CaptureStartupOutput;
            _process.ErrorDataReceived -= CaptureStartupOutput;
        }
        catch
        {
        }

        if (_process.HasExited)
        {
            string crashDetail;
            lock (startupLines)
            {
                crashDetail = startupLines.Count > 0
                    ? string.Join("\n", startupLines)
                    : "(no output captured)";
            }

            _log($"{launch.VpnCoreLabel} crash output:\n{crashDetail}");
            throw new InvalidOperationException(
                $"{launch.VpnCoreLabel} exited unexpectedly during startup (exit code {_process.ExitCode}).\n{crashDetail}");
        }

        // Xray creates the TUN interface but does NOT set up system routes (unlike sing-box auto_route).
        // We must add routes manually so traffic actually flows through the tunnel.
        if (string.Equals(launch.VpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal))
        {
            if (OperatingSystem.IsWindows())
            {
                await EnsureXrayTunRoutesAsync(token).ConfigureAwait(false);
            }
            else
            {
                var tunName = OperatingSystem.IsMacOS() ? "utun99" : "OnionHop";
                SetupXrayRoutes(tunName);
            }
        }
    }

    public async Task PrepareMacPrivilegedTunnelAsync(VpnLaunchConfig config, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS() || PlatformHelper.IsAdministrator())
        {
            return;
        }

        token.ThrowIfCancellationRequested();
        var launch = await PrepareLaunchArtifactsAsync(config, token).ConfigureAwait(false);
        if (CanReuseMacPrivilegedTunnel(launch.ConfigKey))
        {
            _log("Reusing prepared macOS administrator tunnel session.");
            return;
        }

        Stop();
        _lastExitCode = null;
        await LaunchMacPrivilegedTunnelAsync(launch, config, token).ConfigureAwait(false);
    }

    public void Stop()
    {
        var stoppedMacPrivilegedTunnel = StopMacPrivilegedTunnelIfNeeded();
        RemoveXrayRoutes();

        Process? proc;
        lock (_processLock)
        {
            proc = _process;
            _process = null;
        }

        if (proc == null)
        {
            if (!stoppedMacPrivilegedTunnel)
            {
                return;
            }

            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.CloseMainWindow();
                    proc.WaitForExit(1500);
                }
                catch
                {
                }

                proc.Kill(true);
                proc.WaitForExit(5000);
            }

            _lastExitCode = proc.HasExited ? proc.ExitCode : null;
        }
        catch (Exception ex)
        {
            _log($"Failed to stop VPN core: {ex.Message}");
        }
        finally
        {
            proc.OutputDataReceived -= HandleOutput;
            proc.ErrorDataReceived -= HandleOutput;
            proc.Exited -= HandleExited;
            proc.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void HandleExited(object? sender, EventArgs e)
    {
        Exited?.Invoke(sender, e);
    }

    /// <summary>
    /// Kill any orphaned sing-box/xray processes before starting a new one. A core that didn't exit
    /// cleanly (crash, killed app, leftover from a previous session) keeps the "OnionHop" Wintun
    /// adapter open, so the next TUN start fails with "Cannot create a file when that file already
    /// exists." Killing the holder releases the adapter. Stop() already cleared our tracked process,
    /// so anything still running here is orphaned.
    /// </summary>
    private void KillStaleVpnCores()
    {
        var killedAny = false;
        foreach (var name in new[] { "sing-box", "xray" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    killedAny = true;
                    _log($"Released TUN adapter: stopped orphaned {name} process (pid {process.Id}).");
                }
                catch
                {
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }

        if (killedAny)
        {
            // Give Wintun a moment to tear down the adapter before we recreate it.
            try { Thread.Sleep(500); } catch { }
        }
    }

    private void HandleOutput(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            OutputLineReceived?.Invoke(e.Data);
        }

        OutputReceived?.Invoke(sender, e);
    }

    private void SetupXrayRoutes(string interfaceName)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                PlatformHelper.RunCommandSuccess("route", $"add -net 0.0.0.0/1 -interface {interfaceName}");
                PlatformHelper.RunCommandSuccess("route", $"add -net 128.0.0.0/1 -interface {interfaceName}");
            }
            else if (OperatingSystem.IsLinux())
            {
                PlatformHelper.RunCommandSuccess("ip", $"route add 0.0.0.0/1 dev {interfaceName}");
                PlatformHelper.RunCommandSuccess("ip", $"route add 128.0.0.0/1 dev {interfaceName}");
            }

            _xrayTunInterface = interfaceName;
            _log($"Added system routes via {interfaceName}");
        }
        catch (Exception ex)
        {
            _log($"Warning: failed to add system routes for xray TUN: {ex.Message}");
        }
    }

    private void RemoveXrayRoutes()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_xrayRoutesApplied)
            {
                RemoveXrayTunRoutes();
            }

            return;
        }

        var iface = _xrayTunInterface;
        if (iface == null)
        {
            return;
        }

        _xrayTunInterface = null;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                PlatformHelper.RunCommandSuccess("route", $"delete -net 0.0.0.0/1 -interface {iface}");
                PlatformHelper.RunCommandSuccess("route", $"delete -net 128.0.0.0/1 -interface {iface}");
            }
            else if (OperatingSystem.IsLinux())
            {
                PlatformHelper.RunCommandSuccess("ip", $"route del 0.0.0.0/1 dev {iface}");
                PlatformHelper.RunCommandSuccess("ip", $"route del 128.0.0.0/1 dev {iface}");
            }

            _log($"Removed system routes for {iface}");
        }
        catch
        {
            // Best effort — interface may already be gone.
        }
    }

    private async Task<VpnLaunchArtifacts> PrepareLaunchArtifactsAsync(VpnLaunchConfig config, CancellationToken token)
    {
        var vpnCoreMode = NormalizeTunCoreMode(config.VpnCoreMode);
        var vpnCorePath = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? config.XrayPath
            : config.SingBoxPath;
        var vpnCoreLabel = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? "xray"
            : "sing-box";

        if (!File.Exists(vpnCorePath))
        {
            throw new FileNotFoundException($"VPN component missing: vpn/{Path.GetFileName(vpnCorePath)}", vpnCorePath);
        }

        if (PlatformHelper.NeedsWintun && (string.IsNullOrWhiteSpace(config.WintunPath) || !File.Exists(config.WintunPath)))
        {
            throw new FileNotFoundException("VPN component missing: vpn/wintun.dll", config.WintunPath);
        }

        var workDir = Path.GetDirectoryName(vpnCorePath) ?? AppContext.BaseDirectory;
        var configDir = Path.Combine(Path.GetTempPath(), "OnionHop", vpnCoreLabel);
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, $"{vpnCoreLabel}.json");

        var configJson = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? XrayConfigBuilder.BuildJson(
                config.HybridRouting,
                config.SecureDns,
                config.SocksPort,
                config.TorAppProcessNames,
                config.BypassAppProcessNames,
                config.RouteAllWebTrafficThroughTor,
                config.BlockQuicForTorApps,
                config.BlockUdpTraffic,
                config.DohServer,
                config.DohServerPort,
                config.DohPath,
                config.TunMtu,
                ResolvePrimaryIpv4Address())
            : VpnConfigBuilder.BuildJson(
                config.HybridRouting,
                config.SecureDns,
                config.SocksPort,
                config.TorAppProcessNames,
                config.BypassAppProcessNames,
                config.RouteAllWebTrafficThroughTor,
                config.BlockQuicForTorApps,
                config.BlockUdpTraffic,
                config.DohServer,
                config.DohServerPort,
                config.DohPath,
                config.TunStack,
                config.TunMtu,
                config.TunStrictRoute);
        await File.WriteAllTextAsync(configPath, configJson, token).ConfigureAwait(false);

        _log($"Starting {vpnCoreLabel} with config: {configPath}");
        _log($"{vpnCoreLabel} config:\n{configJson}");

        var startupDelayMs = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? 2000
            : 750;
        var configKey = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{vpnCoreMode}\n{config.ManageOnionResolver}\n{config.OnionDnsNameServer ?? string.Empty}\n{configJson}")));

        return new VpnLaunchArtifacts(
            VpnCoreMode: vpnCoreMode,
            VpnCorePath: vpnCorePath,
            VpnCoreLabel: vpnCoreLabel,
            WorkDir: workDir,
            ConfigPath: configPath,
            StartupDelayMs: startupDelayMs,
            ConfigKey: configKey);
    }

    private async Task LaunchMacPrivilegedTunnelAsync(VpnLaunchArtifacts launch, VpnLaunchConfig config, CancellationToken token)
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "OnionHop", "mac-vpn");
        Directory.CreateDirectory(sessionDir);

        var logPath = Path.Combine(sessionDir, $"{launch.VpnCoreLabel}.log");
        var supervisorPidPath = Path.Combine(sessionDir, "supervisor.pid");
        var corePidPath = Path.Combine(sessionDir, "core.pid");
        var exitCodePath = Path.Combine(sessionDir, "exit.code");
        var stopMarkerPath = Path.Combine(sessionDir, "stop.marker");
        var startedMarkerPath = Path.Combine(sessionDir, "started.marker");
        var runnerScriptPath = Path.Combine(sessionDir, $"{launch.VpnCoreLabel}-runner.sh");
        var tunInterface = string.Equals(launch.VpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? (OperatingSystem.IsMacOS() ? "utun99" : "OnionHop")
            : string.Empty;

        // Clean up old state files.
        foreach (var f in new[] { logPath, supervisorPidPath, corePidPath, exitCodePath, stopMarkerPath, startedMarkerPath })
        {
            try { File.Delete(f); } catch { }
        }

        File.WriteAllText(logPath, string.Empty);

        // Build the combined runner script (includes setup + SOCKS wait + VPN start + monitor).
        // This script runs SYNCHRONOUSLY inside the osascript admin context,
        // keeping the osascript process alive for the lifetime of the VPN session.
        File.WriteAllText(
            runnerScriptPath,
            BuildMacPrivilegedRunnerScript(
                sessionDir,
                launch.WorkDir,
                launch.VpnCorePath,
                launch.ConfigPath,
                logPath,
                supervisorPidPath,
                corePidPath,
                exitCodePath,
                stopMarkerPath,
                startedMarkerPath,
                config.SocksPort,
                tunInterface,
                launch.StartupDelayMs,
                config.ManageOnionResolver,
                config.OnionDnsNameServer));
        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                runnerScriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        token.ThrowIfCancellationRequested();

        // Launch osascript as a non-blocking Process. The runner script runs directly
        // inside the admin context (no backgrounding with nohup), so the osascript process
        // stays alive for the entire VPN session. This avoids the issue where backgrounded
        // processes get killed when the Authorization Services context exits.
        var appleScript = $"do shell script \"/bin/sh \" & quoted form of \"{MacAuthorization.EscapeAppleScriptString(runnerScriptPath)}\" with administrator privileges";
        var psi = new ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(appleScript);

        var osascriptStderr = new List<string>();
        var osascriptProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        osascriptProcess.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                lock (osascriptStderr) { osascriptStderr.Add(args.Data); }
            }
        };
        osascriptProcess.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _log($"[osascript] {args.Data}");
            }
        };
        if (!osascriptProcess.Start())
        {
            throw new InvalidOperationException("Failed to launch macOS authorization prompt.");
        }

        // Read output asynchronously to prevent buffer deadlocks.
        osascriptProcess.BeginOutputReadLine();
        osascriptProcess.BeginErrorReadLine();

        // Wait for the "started" marker (confirms admin auth succeeded and script is running)
        // or for the osascript process to exit (user canceled the password dialog).
        var authWait = Stopwatch.StartNew();
        while (authWait.ElapsedMilliseconds < 180_000)
        {
            token.ThrowIfCancellationRequested();

            if (File.Exists(startedMarkerPath))
            {
                break;
            }

            if (osascriptProcess.HasExited)
            {
                osascriptProcess.Dispose();
                throw new OperationCanceledException("Administrator privileges were not granted.");
            }

            await Task.Delay(200, token).ConfigureAwait(false);
        }

        if (!File.Exists(startedMarkerPath))
        {
            try { osascriptProcess.Kill(entireProcessTree: true); } catch { }
            osascriptProcess.Dispose();
            throw new OperationCanceledException("Timed out waiting for administrator authorization.");
        }

        _macPrivilegedOsascriptProcess = osascriptProcess;
        _macPrivilegedSessionDir = sessionDir;
        _macPrivilegedRunnerScriptPath = runnerScriptPath;
        _macPrivilegedLogPath = logPath;
        _macPrivilegedSupervisorPidPath = supervisorPidPath;
        _macPrivilegedCorePidPath = corePidPath;
        _macPrivilegedExitCodePath = exitCodePath;
        _macPrivilegedCoreMode = launch.VpnCoreMode;
        _macPrivilegedTunInterface = tunInterface;
        _macPrivilegedManagesOnionResolver = config.ManageOnionResolver;
        _macPrivilegedStopMarkerPath = stopMarkerPath;
        _macPrivilegedStartedMarkerPath = startedMarkerPath;
        _macPrivilegedConfigKey = launch.ConfigKey;
        _macPrivilegedLogOffset = 0;
        StartMacPrivilegedMonitor();
        await Task.Delay(100, token).ConfigureAwait(false);
    }

    private async Task WaitForMacPrivilegedTunnelReadyAsync(string vpnCoreLabel, int startupDelayMs, CancellationToken token)
    {
        try
        {
            var startupWait = Stopwatch.StartNew();
            var maxStartupWaitMs = Math.Max(15_000, startupDelayMs + 10_000);

            while (startupWait.ElapsedMilliseconds < maxStartupWaitMs)
            {
                token.ThrowIfCancellationRequested();

                if (TryGetMacPrivilegedExitCode(out var exitCode))
                {
                    _lastExitCode = exitCode;
                    var crashDetail = ReadMacPrivilegedLogTail();
                    throw new InvalidOperationException(
                        $"{vpnCoreLabel} exited unexpectedly during startup (exit code {exitCode}).\n{crashDetail}");
                }

                if (IsMacPrivilegedTunnelRunning())
                {
                    return;
                }

                await Task.Delay(200, token).ConfigureAwait(false);
            }

            var timeoutDetail = ReadMacPrivilegedLogTail();
            throw new InvalidOperationException(
                $"Unable to launch {vpnCoreLabel} through macOS authorization.\n{timeoutDetail}");
        }
        catch
        {
            StopMacPrivilegedTunnelIfNeeded();
            throw;
        }
    }

    private bool StopMacPrivilegedTunnelIfNeeded()
    {
        if (_macPrivilegedRunnerScriptPath == null &&
            _macPrivilegedLogPath == null &&
            _macPrivilegedSupervisorPidPath == null &&
            _macPrivilegedCorePidPath == null &&
            _macPrivilegedExitCodePath == null &&
            _macPrivilegedOsascriptProcess == null)
        {
            return false;
        }

        StopMacPrivilegedMonitor();

        // Signal the runner script to stop gracefully.
        SignalMacPrivilegedStop();

        // Wait for the runner script to detect the stop marker and exit cleanly.
        var stopWait = Stopwatch.StartNew();
        while (stopWait.ElapsedMilliseconds < 5000)
        {
            if (TryGetMacPrivilegedExitCode(out _))
            {
                break;
            }

            var osascriptDead = _macPrivilegedOsascriptProcess == null ||
                                _macPrivilegedOsascriptProcess.HasExited;
            if (osascriptDead && !IsMacPrivilegedTunnelRunning())
            {
                break;
            }

            Thread.Sleep(200);
        }

        // If still running, kill the osascript process tree (which includes the runner and VPN core).
        KillMacPrivilegedOsascriptProcess();

        // If the VPN core is somehow still alive (e.g., it detached), use admin privileges to kill it.
        if (IsMacPrivilegedTunnelRunning())
        {
            var stopResult = MacAuthorization.RunScript(
                BuildMacPrivilegedStopScript(
                    _macPrivilegedSupervisorPidPath,
                    _macPrivilegedCorePidPath,
                    _macPrivilegedExitCodePath,
                    _macPrivilegedCoreMode,
                    _macPrivilegedManagesOnionResolver,
                    _macPrivilegedTunInterface),
                requireAdministrator: true,
                timeoutMs: 60_000);

            if (!stopResult.Success)
            {
                _log($"Failed to stop VPN core: {stopResult.FailureMessage}");
            }
        }

        CleanupMacPrivilegedStateFiles();
        ClearMacPrivilegedState();
        return true;
    }

    private void KillMacPrivilegedOsascriptProcess()
    {
        var proc = _macPrivilegedOsascriptProcess;
        if (proc == null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch
        {
        }
        finally
        {
            try { proc.Dispose(); } catch { }
            _macPrivilegedOsascriptProcess = null;
        }
    }
    private bool CanReuseMacPrivilegedTunnel(string configKey)
    {
        if (string.IsNullOrWhiteSpace(configKey) ||
            !string.Equals(_macPrivilegedConfigKey, configKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryGetMacPrivilegedExitCode(out _))
        {
            return false;
        }

        // Check that the osascript process is still alive (runner is still running).
        if (_macPrivilegedOsascriptProcess == null || _macPrivilegedOsascriptProcess.HasExited)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(_macPrivilegedRunnerScriptPath) &&
               !string.IsNullOrWhiteSpace(_macPrivilegedSupervisorPidPath);
    }

    private void StartMacPrivilegedMonitor()
    {
        StopMacPrivilegedMonitor();

        var logPath = _macPrivilegedLogPath;
        var exitCodePath = _macPrivilegedExitCodePath;
        if (string.IsNullOrWhiteSpace(logPath) || string.IsNullOrWhiteSpace(exitCodePath))
        {
            return;
        }

        _macPrivilegedMonitorCts = new CancellationTokenSource();
        var token = _macPrivilegedMonitorCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    DrainMacPrivilegedLogLines(logPath);

                    if (TryGetMacPrivilegedExitCode(out var exitCode))
                    {
                        _lastExitCode = exitCode;
                        Exited?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    // Also detect if the osascript process died unexpectedly.
                    if (_macPrivilegedOsascriptProcess is { HasExited: true } deadProc && !TryGetMacPrivilegedExitCode(out _))
                    {
                        _log($"osascript process exited unexpectedly (exit code {deadProc.ExitCode}).");
                        _lastExitCode = -1;
                        Exited?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    await Task.Delay(750, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log($"Failed to monitor macOS tunnel: {ex.Message}");
                    return;
                }
            }
        }, token);
    }

    private void StopMacPrivilegedMonitor()
    {
        try
        {
            _macPrivilegedMonitorCts?.Cancel();
            _macPrivilegedMonitorCts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _macPrivilegedMonitorCts = null;
        }
    }

    private void DrainMacPrivilegedLogLines(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _macPrivilegedLogOffset)
        {
            _macPrivilegedLogOffset = 0;
        }

        stream.Seek(_macPrivilegedLogOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        _macPrivilegedLogOffset = stream.Position;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            OutputLineReceived?.Invoke(line);
        }
    }

    private bool IsMacPrivilegedTunnelRunning()
    {
        var corePidPath = _macPrivilegedCorePidPath;
        if (string.IsNullOrWhiteSpace(corePidPath) || !File.Exists(corePidPath))
        {
            return false;
        }

        if (TryGetMacPrivilegedExitCode(out _))
        {
            return false;
        }

        if (!TryReadIntFromFile(corePidPath, out var pid) || pid <= 0)
        {
            return false;
        }

        var psOutput = PlatformHelper.RunCommand("ps", $"-p {pid} -o pid=");
        return !string.IsNullOrWhiteSpace(psOutput);
    }

    private bool HasMacPrivilegedSupervisorProcess()
    {
        // Check the osascript process first (it hosts the runner script).
        if (_macPrivilegedOsascriptProcess is { HasExited: false })
        {
            return true;
        }

        var supervisorPidPath = _macPrivilegedSupervisorPidPath;
        if (string.IsNullOrWhiteSpace(supervisorPidPath) || !File.Exists(supervisorPidPath))
        {
            return false;
        }

        if (!TryReadIntFromFile(supervisorPidPath, out var pid) || pid <= 0)
        {
            return false;
        }

        var psOutput = PlatformHelper.RunCommand("ps", $"-p {pid} -o pid=");
        return !string.IsNullOrWhiteSpace(psOutput);
    }

    private bool TryGetMacPrivilegedExitCode(out int exitCode)
    {
        var exitCodePath = _macPrivilegedExitCodePath;
        if (string.IsNullOrWhiteSpace(exitCodePath) || !File.Exists(exitCodePath))
        {
            exitCode = default;
            return false;
        }

        return TryReadIntFromFile(exitCodePath, out exitCode);
    }

    private static bool TryReadIntFromFile(string path, out int value)
    {
        try
        {
            var raw = File.ReadAllText(path).Trim();
            return int.TryParse(raw, out value);
        }
        catch
        {
            value = default;
            return false;
        }
    }

    private string ReadMacPrivilegedLogTail()
    {
        var logPath = _macPrivilegedLogPath;
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return "(no output captured)";
        }

        try
        {
            var lines = File.ReadAllLines(logPath);
            if (lines.Length == 0)
            {
                return "(no output captured)";
            }

            var take = Math.Min(lines.Length, 20);
            return string.Join("\n", lines[^take..]);
        }
        catch
        {
            return "(no output captured)";
        }
    }

    private void CleanupMacPrivilegedStateFiles()
    {
        foreach (var path in new[]
                 {
                     _macPrivilegedSupervisorPidPath,
                     _macPrivilegedCorePidPath,
                     _macPrivilegedExitCodePath,
                     _macPrivilegedLogPath,
                     _macPrivilegedRunnerScriptPath,
                     _macPrivilegedStopMarkerPath,
                     _macPrivilegedStartedMarkerPath
                 })
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    private void ClearMacPrivilegedState()
    {
        KillMacPrivilegedOsascriptProcess();
        _macPrivilegedSessionDir = null;
        _macPrivilegedRunnerScriptPath = null;
        _macPrivilegedLogPath = null;
        _macPrivilegedSupervisorPidPath = null;
        _macPrivilegedCorePidPath = null;
        _macPrivilegedExitCodePath = null;
        _macPrivilegedCoreMode = null;
        _macPrivilegedTunInterface = null;
        _macPrivilegedManagesOnionResolver = false;
        _macPrivilegedStopMarkerPath = null;
        _macPrivilegedStartedMarkerPath = null;
        _macPrivilegedConfigKey = null;
        _macPrivilegedLogOffset = 0;
    }

    private void SignalMacPrivilegedStop()
    {
        var stopMarkerPath = _macPrivilegedStopMarkerPath;
        if (string.IsNullOrWhiteSpace(stopMarkerPath))
        {
            return;
        }

        try
        {
            File.WriteAllText(stopMarkerPath, "stop");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Builds a self-contained runner script that includes setup tasks, SOCKS port wait,
    /// VPN core launch, and monitoring. This script runs SYNCHRONOUSLY inside the osascript
    /// admin context so the process stays alive for the full VPN session.
    /// </summary>
    private static string BuildMacPrivilegedRunnerScript(
        string sessionDir,
        string workDir,
        string vpnCorePath,
        string configPath,
        string logPath,
        string supervisorPidPath,
        string corePidPath,
        string exitCodePath,
        string stopMarkerPath,
        string startedMarkerPath,
        int socksPort,
        string tunInterface,
        int startupDelayMs,
        bool manageOnionResolver,
        string? onionDnsNameServer)
    {
        var startupSeconds = Math.Max(1, startupDelayMs / 1000);
        const int readyTimeoutSeconds = 420;
        var xrayRouteSetup = string.IsNullOrWhiteSpace(tunInterface)
            ? string.Empty
            : $$"""
                /sbin/route add -net 0.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                /sbin/route add -net 128.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                """;
        var xrayRouteCleanup = string.IsNullOrWhiteSpace(tunInterface)
            ? string.Empty
            : $$"""
                /sbin/route delete -net 0.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                /sbin/route delete -net 128.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                """;
        var onionResolverSetup = manageOnionResolver
            ? $$"""
                mkdir -p /etc/resolver
                printf 'nameserver %s\nport 53\n' {{MacAuthorization.QuoteShellArgument(string.IsNullOrWhiteSpace(onionDnsNameServer) ? "127.0.0.1" : onionDnsNameServer.Trim())}} > /etc/resolver/onion
                chmod 644 /etc/resolver/onion
                """
            : string.Empty;
        var onionResolverCleanup = manageOnionResolver
            ? "rm -f /etc/resolver/onion"
            : string.Empty;

        return $$"""
            #!/bin/sh
            # Do NOT use set -e: any command failure would kill the entire privileged
            # runner before it can write the exit-code file back to the user process.
            # Use set -u to catch typos but never set -e.
            set -u
            umask 022

            # Signal that authorization was granted and script is running.
            printf '' > {{MacAuthorization.QuoteShellArgument(startedMarkerPath)}}
            mkdir -p {{MacAuthorization.QuoteShellArgument(sessionDir)}}
            rm -f {{MacAuthorization.QuoteShellArgument(supervisorPidPath)}} {{MacAuthorization.QuoteShellArgument(corePidPath)}} {{MacAuthorization.QuoteShellArgument(exitCodePath)}} {{MacAuthorization.QuoteShellArgument(stopMarkerPath)}}
            : > {{MacAuthorization.QuoteShellArgument(logPath)}}

            # Record our PID as the supervisor.
            printf '%s\n' "$$" > {{MacAuthorization.QuoteShellArgument(supervisorPidPath)}}

            # Setup: configure .onion DNS resolver if needed.
            {{onionResolverSetup}}

            # Cleanup handler for graceful exit.
            cleanup() {
              :
              {{xrayRouteCleanup}}
              {{onionResolverCleanup}}
            }
            trap cleanup EXIT

            cd {{MacAuthorization.QuoteShellArgument(workDir)}} || exit 1

            # Wait for Tor SOCKS port to become available.
            DEADLINE=$(($(date +%s) + {{readyTimeoutSeconds}}))
            while :; do
              if [ -f {{MacAuthorization.QuoteShellArgument(stopMarkerPath)}} ]; then
                printf '%s\n' 130 > {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
                exit 0
              fi
              if /usr/bin/nc -z 127.0.0.1 {{socksPort}} >/dev/null 2>&1; then
                break
              fi
              if [ "$(date +%s)" -ge "$DEADLINE" ]; then
                printf 'Timed out waiting for Tor SOCKS port %s\n' {{socksPort}} >> {{MacAuthorization.QuoteShellArgument(logPath)}}
                printf '%s\n' 124 > {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
                exit 0
              fi
              sleep 1
            done

            # Start VPN core.
            {{MacAuthorization.QuoteShellArgument(vpnCorePath)}} run -c {{MacAuthorization.QuoteShellArgument(configPath)}} >> {{MacAuthorization.QuoteShellArgument(logPath)}} 2>&1 &
            CORE_PID=$!
            printf '%s\n' "$CORE_PID" > {{MacAuthorization.QuoteShellArgument(corePidPath)}}
            sleep {{startupSeconds}}
            {{xrayRouteSetup}}

            # Monitor VPN core, checking for stop signal.
            while kill -0 "$CORE_PID" 2>/dev/null; do
              if [ -f {{MacAuthorization.QuoteShellArgument(stopMarkerPath)}} ]; then
                kill "$CORE_PID" 2>/dev/null || true
                sleep 1
                kill -9 "$CORE_PID" 2>/dev/null || true
              fi
              sleep 1
            done
            wait "$CORE_PID" 2>/dev/null
            EXIT_CODE=$?
            printf '%s\n' "$EXIT_CODE" > {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
            # Always exit 0 — the real exit code is communicated via the exit code file.
            # If we exit non-zero, AppleScript's "do shell script" throws an error and
            # osascript dies with code 1, losing the actual exit code.
            exit 0
            """;
    }

    private static string BuildMacPrivilegedStopScript(
        string? supervisorPidPath,
        string? corePidPath,
        string? exitCodePath,
        string? coreMode,
        bool manageOnionResolver,
        string? tunInterface)
    {
        var deleteRoutes = string.Equals(coreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(tunInterface)
            ? $$"""
                /sbin/route delete -net 0.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                /sbin/route delete -net 128.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                """
            : string.Empty;
        var removeOnionResolver = manageOnionResolver
            ? "rm -f /etc/resolver/onion\n"
            : string.Empty;

        return $$"""
            #!/bin/sh
            set -eu
            {{deleteRoutes}}
            {{removeOnionResolver}}
            if [ -f {{MacAuthorization.QuoteShellArgument(corePidPath ?? string.Empty)}} ]; then
              CORE_PID=$(cat {{MacAuthorization.QuoteShellArgument(corePidPath ?? string.Empty)}} 2>/dev/null || true)
              if [ -n "$CORE_PID" ]; then
                kill "$CORE_PID" 2>/dev/null || true
                sleep 1
                kill -9 "$CORE_PID" 2>/dev/null || true
              fi
            fi
            if [ -f {{MacAuthorization.QuoteShellArgument(supervisorPidPath ?? string.Empty)}} ]; then
              SUPERVISOR_PID=$(cat {{MacAuthorization.QuoteShellArgument(supervisorPidPath ?? string.Empty)}} 2>/dev/null || true)
              if [ -n "$SUPERVISOR_PID" ]; then
                kill "$SUPERVISOR_PID" 2>/dev/null || true
              fi
            fi
            rm -f {{MacAuthorization.QuoteShellArgument(corePidPath ?? string.Empty)}} {{MacAuthorization.QuoteShellArgument(supervisorPidPath ?? string.Empty)}} {{MacAuthorization.QuoteShellArgument(exitCodePath ?? string.Empty)}}
            """;
    }

    private sealed record VpnLaunchArtifacts(
        string VpnCoreMode,
        string VpnCorePath,
        string VpnCoreLabel,
        string WorkDir,
        string ConfigPath,
        int StartupDelayMs,
        string ConfigKey);

    private static string NormalizeTunCoreMode(string? value)
    {
        return string.Equals(value, OnionHopConnectOptions.TunCoreXray, StringComparison.OrdinalIgnoreCase)
            ? OnionHopConnectOptions.TunCoreXray
            : OnionHopConnectOptions.TunCoreSingBox;
    }

    private async Task EnsureXrayTunRoutesAsync(CancellationToken token)
    {
        var adapter = await WaitForTunAdapterAsync(TunInterfaceName, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (adapter == null)
        {
            throw new InvalidOperationException("xray TUN interface was not found. Unable to install routes.");
        }

        adapter = await EnsureXrayAdapterAddressAsync(adapter, token).ConfigureAwait(false);
        var ifIndex = adapter.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _log($"xray route setup: using adapter '{adapter.Name}' (ifIndex={ifIndex}, ip={adapter.IPv4Address}).");
        await EnsureHalfDefaultRoutesWithRetryAsync(ifIndex, adapter.IPv4Address, token).ConfigureAwait(false);
        _log($"xray route setup: installed {RouteHalfDefaultA}/1 and {RouteHalfDefaultB}/1 via ifIndex={ifIndex}.");
        _xrayRoutesApplied = true;
    }

    private void RemoveXrayTunRoutes()
    {
        try
        {
            var adapter = TryFindTunAdapter(TunInterfaceName, preferExactName: false);
            if (adapter == null)
            {
                _xrayRoutesApplied = false;
                return;
            }

            var ifIndex = adapter.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RunRoute($"DELETE {RouteHalfDefaultA} MASK 128.0.0.0 IF {ifIndex}", allowFailure: true);
            RunRoute($"DELETE {RouteHalfDefaultB} MASK 128.0.0.0 IF {ifIndex}", allowFailure: true);
        }
        catch (Exception ex)
        {
            _log($"xray route cleanup failed: {ex.Message}");
        }
        finally
        {
            _xrayRoutesApplied = false;
        }
    }

    private async Task<TunAdapterInfo?> WaitForTunAdapterAsync(string expectedName, TimeSpan timeout, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        TunAdapterInfo? fallback = null;
        TunAdapterInfo? preferred = null;
        string? lastPreferredKey = null;
        var preferredStableSamples = 0;

        while (DateTime.UtcNow - started < timeout)
        {
            token.ThrowIfCancellationRequested();

            preferred = TryFindTunAdapter(expectedName, preferExactName: true);
            if (preferred is { HasUsableIpv4: true })
            {
                var preferredKey = $"{preferred.InterfaceIndex}:{preferred.IPv4Address}";
                preferredStableSamples = string.Equals(lastPreferredKey, preferredKey, StringComparison.Ordinal)
                    ? preferredStableSamples + 1
                    : 1;
                lastPreferredKey = preferredKey;

                // Give Xray a moment to remove any stale adapters before we bind routes.
                if (preferredStableSamples >= 2)
                {
                    return preferred;
                }
            }
            else
            {
                preferredStableSamples = 0;
                lastPreferredKey = null;
            }

            var candidate = TryFindTunAdapter(expectedName, preferExactName: false);
            if (candidate != null)
            {
                fallback = candidate;

                // If no exact-name adapter appears, tolerate suffixed adapter names.
                if (preferred == null && candidate.HasUsableIpv4 && DateTime.UtcNow - started >= TimeSpan.FromSeconds(4))
                {
                    return candidate;
                }
            }

            await Task.Delay(250, token).ConfigureAwait(false);
        }

        if (preferred is { HasUsableIpv4: true })
        {
            return preferred;
        }

        if (fallback is { HasUsableIpv4: true })
        {
            return fallback;
        }

        return preferred ?? fallback;
    }

    private static TunAdapterInfo? TryFindTunAdapter(string expectedName, bool preferExactName)
    {
        var candidates = new List<(TunAdapterInfo Adapter, int Score)>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return null;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus is OperationalStatus.Down or OperationalStatus.NotPresent)
            {
                continue;
            }

            IPInterfaceProperties props;
            IPv4InterfaceProperties? ipv4;
            try
            {
                props = nic.GetIPProperties();
                ipv4 = props.GetIPv4Properties();
            }
            catch
            {
                continue;
            }

            if (ipv4 == null)
            {
                continue;
            }

            IPAddress? tunIp;
            IPAddress? anyIpv4;
            try
            {
                tunIp = props.UnicastAddresses
                    .Select(address => address.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && IsTunIpv4(address));
                anyIpv4 = props.UnicastAddresses
                    .Select(address => address.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
            }
            catch
            {
                continue;
            }

            var nameEquals = string.Equals(nic.Name, expectedName, StringComparison.OrdinalIgnoreCase);
            var descriptionEquals = string.Equals(nic.Description, expectedName, StringComparison.OrdinalIgnoreCase);
            var nameStartsWith = nic.Name.StartsWith(expectedName, StringComparison.OrdinalIgnoreCase);
            var descriptionStartsWith = nic.Description.StartsWith(expectedName, StringComparison.OrdinalIgnoreCase);
            if (!nameEquals && !descriptionEquals && !nameStartsWith && !descriptionStartsWith)
            {
                continue;
            }

            if (preferExactName && !nameEquals && !descriptionEquals)
            {
                continue;
            }

            var candidateIp = tunIp ?? anyIpv4;
            var candidate = new TunAdapterInfo(
                ipv4.Index,
                nic.Name,
                nic.Description,
                candidateIp?.ToString() ?? "pending",
                candidateIp != null);
            var score = 0;
            if (nameEquals)
            {
                score += 1000;
            }

            if (descriptionEquals)
            {
                score += 900;
            }

            if (nameStartsWith)
            {
                score += 300;
            }

            if (descriptionStartsWith)
            {
                score += 200;
            }

            if (tunIp != null)
            {
                score += 150;
            }

            if (anyIpv4 != null)
            {
                score += 40;
            }

            if (nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase))
            {
                score += 250;
            }

            candidates.Add((candidate, score));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Adapter.InterfaceIndex)
            .Select(item => item.Adapter)
            .FirstOrDefault();
    }

    private async Task<TunAdapterInfo> EnsureXrayAdapterAddressAsync(TunAdapterInfo adapter, CancellationToken token)
    {
        if (!IPAddress.TryParse(adapter.IPv4Address, out var currentIp))
        {
            return adapter;
        }

        if (!IsLinkLocalIpv4(currentIp))
        {
            return adapter;
        }

        _log($"xray route setup: adapter '{adapter.Name}' has link-local IPv4 {adapter.IPv4Address}; assigning {XrayPreferredTunAddress}/30.");
        if (!TryAssignStaticTunAddress(adapter))
        {
            _log("xray route setup: failed to assign static TUN IPv4. Continuing with link-local address.");
            return adapter;
        }

        var updated = await WaitForTunAdapterByIndexAsync(adapter.InterfaceIndex, TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
        if (updated != null)
        {
            return updated;
        }

        _log("xray route setup: static TUN IPv4 assignment could not be confirmed. Continuing with current adapter state.");
        return adapter;
    }

    private bool TryAssignStaticTunAddress(TunAdapterInfo adapter)
    {
        var escapedName = adapter.Name.Replace("\"", "\\\"", StringComparison.Ordinal);
        var commands = new[]
        {
            $"interface ipv4 set address name=\"{escapedName}\" source=static address={XrayPreferredTunAddress} mask={XrayPreferredTunMask} gateway=none store=active",
            $"interface ip set address name=\"{escapedName}\" static {XrayPreferredTunAddress} {XrayPreferredTunMask} none",
            $"interface ipv4 set address interface={adapter.InterfaceIndex} source=static address={XrayPreferredTunAddress} mask={XrayPreferredTunMask} gateway=none store=active"
        };

        foreach (var args in commands)
        {
            if (RunNetsh(args, allowFailure: true))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<TunAdapterInfo?> WaitForTunAdapterByIndexAsync(int interfaceIndex, TimeSpan timeout, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        TunAdapterInfo? fallback = null;
        while (DateTime.UtcNow - started < timeout)
        {
            token.ThrowIfCancellationRequested();
            var adapter = TryFindTunAdapterByIndex(interfaceIndex);
            if (adapter != null)
            {
                fallback = adapter;
                if (adapter.HasUsableIpv4 && IPAddress.TryParse(adapter.IPv4Address, out var ip) && !IsLinkLocalIpv4(ip))
                {
                    return adapter;
                }
            }

            await Task.Delay(250, token).ConfigureAwait(false);
        }

        return fallback;
    }

    private static TunAdapterInfo? TryFindTunAdapterByIndex(int interfaceIndex)
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return null;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus is OperationalStatus.Down or OperationalStatus.NotPresent)
            {
                continue;
            }

            IPInterfaceProperties props;
            IPv4InterfaceProperties? ipv4;
            try
            {
                props = nic.GetIPProperties();
                ipv4 = props.GetIPv4Properties();
            }
            catch
            {
                continue;
            }

            if (ipv4 == null || ipv4.Index != interfaceIndex)
            {
                continue;
            }

            var ipv4Address = props.UnicastAddresses
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

            return new TunAdapterInfo(
                ipv4.Index,
                nic.Name,
                nic.Description,
                ipv4Address?.ToString() ?? "pending",
                ipv4Address != null);
        }

        return null;
    }

    private async Task EnsureHalfDefaultRoutesWithRetryAsync(string ifIndex, string adapterIpv4Address, CancellationToken token)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                UpsertHalfDefaultRoute(RouteHalfDefaultA, ifIndex, adapterIpv4Address);
                UpsertHalfDefaultRoute(RouteHalfDefaultB, ifIndex, adapterIpv4Address);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log($"xray route setup attempt {attempt}/8 failed: {ex.Message}");
                if (attempt < 8)
                {
                    await Task.Delay(300, token).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(lastError?.Message ?? "Failed to install Xray routes.");
    }

    private static bool IsTunIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 172 && bytes[1] == 19;
    }

    private static bool IsLinkLocalIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static string? ResolvePrimaryIpv4Address()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 53);
            if (socket.LocalEndPoint is IPEndPoint endpoint &&
                endpoint.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(endpoint.Address))
            {
                return endpoint.Address.ToString();
            }
        }
        catch
        {
        }

        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

            foreach (var nic in candidates)
            {
                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.Any.Equals(g.Address) &&
                    !IPAddress.None.Equals(g.Address));

                if (!hasGateway)
                {
                    continue;
                }

                var address = properties.UnicastAddresses
                    .Select(item => item.Address)
                    .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                          !IPAddress.IsLoopback(ip));

                if (address != null)
                {
                    return address.ToString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private void UpsertHalfDefaultRoute(string destination, string ifIndex, string adapterIpv4Address)
    {
        var preferredGateway = ResolveRouteGateway(adapterIpv4Address);
        _log($"xray route setup: installing {destination}/1 via gateway {preferredGateway} on ifIndex={ifIndex} (metric {XrayRouteMetric}).");
        if (TryUpsertHalfDefaultRoute(destination, ifIndex, preferredGateway))
        {
            return;
        }

        if (!string.Equals(preferredGateway, "0.0.0.0", StringComparison.Ordinal) &&
            TryUpsertHalfDefaultRoute(destination, ifIndex, "0.0.0.0"))
        {
            _log($"xray route setup: falling back to on-link gateway for {destination}/1.");
            return;
        }

        throw new InvalidOperationException($"Unable to install route for {destination}/1 via interface {ifIndex}.");
    }

    private bool TryUpsertHalfDefaultRoute(string destination, string ifIndex, string gateway)
    {
        var changeArgs = $"CHANGE {destination} MASK 128.0.0.0 {gateway} IF {ifIndex} METRIC {XrayRouteMetric}";
        if (RunRoute(changeArgs, allowFailure: true))
        {
            return true;
        }

        var addArgs = $"ADD {destination} MASK 128.0.0.0 {gateway} IF {ifIndex} METRIC {XrayRouteMetric}";
        return RunRoute(addArgs, allowFailure: true);
    }

    private static string ResolveRouteGateway(string adapterIpv4Address)
    {
        if (IPAddress.TryParse(adapterIpv4Address, out var address) &&
            address.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(address) &&
            !IsLinkLocalIpv4(address))
        {
            var peer = TryGetPointToPointPeer(address);
            if (peer != null)
            {
                return peer.ToString();
            }

            return address.ToString();
        }

        return "0.0.0.0";
    }

    private static IPAddress? TryGetPointToPointPeer(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return null;
        }

        // OnionHop assigns /30 on xray TUN. The peer is the other host address in that /30.
        // Example: 172.19.0.1 <-> 172.19.0.2
        var last = bytes[3];
        if (last == 1 || last == 2)
        {
            bytes[3] = last == 1 ? (byte)2 : (byte)1;
            return new IPAddress(bytes);
        }

        var hostPart = last & 0b11;
        if (hostPart == 1)
        {
            bytes[3] = (byte)(last + 1);
            return new IPAddress(bytes);
        }

        if (hostPart == 2)
        {
            bytes[3] = (byte)(last - 1);
            return new IPAddress(bytes);
        }

        return null;
    }

    private bool RunRoute(string args, bool allowFailure)
    {
        var psi = new ProcessStartInfo("route", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            if (allowFailure)
            {
                return false;
            }

            throw new InvalidOperationException($"route command failed to start: {args}");
        }

        var stdOut = proc.StandardOutput.ReadToEnd();
        var stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(8000);

        if (proc.ExitCode == 0)
        {
            return true;
        }

        if (allowFailure)
        {
            return false;
        }

        var combined = $"{stdOut}\n{stdErr}".Trim();
        throw new InvalidOperationException($"route {args} failed (exit {proc.ExitCode}): {combined}");
    }

    private bool RunNetsh(string args, bool allowFailure)
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
            if (allowFailure)
            {
                return false;
            }

            throw new InvalidOperationException($"netsh command failed to start: {args}");
        }

        var stdOut = proc.StandardOutput.ReadToEnd();
        var stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(8000);

        if (proc.ExitCode == 0)
        {
            return true;
        }

        if (allowFailure)
        {
            return false;
        }

        var combined = $"{stdOut}\n{stdErr}".Trim();
        throw new InvalidOperationException($"netsh {args} failed (exit {proc.ExitCode}): {combined}");
    }

    private sealed record TunAdapterInfo(int InterfaceIndex, string Name, string Description, string IPv4Address, bool HasUsableIpv4);
}

internal sealed class VpnLaunchConfig
{
    public string SingBoxPath { get; init; } = string.Empty;
    public string XrayPath { get; init; } = string.Empty;
    public string? WintunPath { get; init; }
    public string VpnCoreMode { get; init; } = OnionHopConnectOptions.TunCoreSingBox;
    public bool HybridRouting { get; init; }
    public bool SecureDns { get; init; }
    public int SocksPort { get; init; }
    public string DohServer { get; init; } = "cloudflare-dns.com";
    public int DohServerPort { get; init; } = 443;
    public string DohPath { get; init; } = "/dns-query";
    public bool RouteAllWebTrafficThroughTor { get; init; } = true;
    public bool BlockQuicForTorApps { get; init; } = true;
    public bool BlockUdpTraffic { get; init; } = true;
    public string TunStack { get; init; } = "mixed";
    public int? TunMtu { get; init; }
    public bool TunStrictRoute { get; init; } = true;
    public IReadOnlyList<string> TorAppProcessNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BypassAppProcessNames { get; init; } = Array.Empty<string>();
    public bool ManageOnionResolver { get; init; }
    public string? OnionDnsNameServer { get; init; }
    [JsonIgnore]
    public Action<Process>? ProcessStarted { get; init; }
}
