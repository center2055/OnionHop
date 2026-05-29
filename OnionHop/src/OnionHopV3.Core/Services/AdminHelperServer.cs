using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core;
using OnionHopV3.Core.Platform.Windows;

namespace OnionHopV3.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class AdminHelperServer
{
    private const int MaxBufferedLogLines = 400;
    private readonly VpnService _vpnService;
    private readonly object _vpnLogLock = new();
    private readonly Queue<string> _vpnLogLines = new();
    private readonly WindowsOnionDnsProxyService _dnsProxyService = new();
    private readonly bool _persistentMode;
    private bool _killSwitchEnabled;
    private bool _isStopping;
    private bool _shutdownRequested;
    private Mutex? _daemonMutex;

    public AdminHelperServer(bool persistentMode = false)
    {
        _persistentMode = persistentMode;
        _vpnService = new VpnService(LogVpnHelperLine);
        _vpnService.OutputReceived += OnVpnOutput;
        _vpnService.Exited += OnVpnExited;
    }

    public static bool IsHelperMode(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--helper", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsDaemonMode(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--helper-daemon", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            StartupLogger.Write("Admin helper is only supported on Windows.");
            return;
        }

        new AdminHelperServer(IsDaemonMode(args)).RunAsync(args).GetAwaiter().GetResult();
    }

    private static string GetPipeName(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--pipe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var name = args[i + 1]?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return AdminHelperProtocol.PipeName;
    }

    private async Task RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_persistentMode)
        {
            await RunPersistentAsync().ConfigureAwait(false);
            return;
        }

        await RunTransientAsync(args).ConfigureAwait(false);
    }

    private async Task RunTransientAsync(string[] args)
    {
        var pipeName = GetPipeName(args);
        StartupLogger.Write($"Admin helper starting. Pipe={pipeName}");

        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            StartupLogger.Write($"Admin helper connecting to pipe...");
            var connectTask = pipe.ConnectAsync();
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false);
            if (completed != connectTask)
            {
                StartupLogger.Write("Admin helper: connection timed out waiting for pipe.");
                return;
            }

            await connectTask.ConfigureAwait(false);
            StartupLogger.Write($"Admin helper connected to pipe successfully.");

            using var reader = new StreamReader(
                pipe,
                AdminHelperProtocol.PipeTextEncoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: AdminHelperProtocol.PipeTextBufferSize,
                leaveOpen: true);
            using var writer = new StreamWriter(
                pipe,
                AdminHelperProtocol.PipeTextEncoding,
                bufferSize: AdminHelperProtocol.PipeTextBufferSize,
                leaveOpen: true);
            StartupLogger.Write("Admin helper: ready, entering command loop...");

            while (pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    StartupLogger.Write("Admin helper: pipe closed by client.");
                    break;
                }

                var request = JsonSerializer.Deserialize<HelperRequest>(line, AdminHelperProtocol.JsonOptions);
                if (request == null)
                {
                    continue;
                }

                StartupLogger.Write($"Admin helper received command: {request.Command}");
                var response = await HandleRequestAsync(request).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(response, AdminHelperProtocol.JsonOptions);
                await writer.WriteLineAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);

                if (_shutdownRequested)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Admin helper failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            pipe?.Dispose();
            Cleanup();
        }
    }

    private async Task RunPersistentAsync()
    {
        if (!WindowsAdmin.IsAdministrator())
        {
            StartupLogger.Write("Persistent admin helper started without elevation. Exiting.");
            return;
        }

        var mutexName = $@"Local\{AdminHelperProtocol.PipeName}.daemon";
        _daemonMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            StartupLogger.Write("Persistent admin helper is already running.");
            _daemonMutex.Dispose();
            _daemonMutex = null;
            return;
        }

        StartupLogger.Write($"Persistent admin helper starting. Pipe={AdminHelperProtocol.PipeName}");
        try
        {
            while (!_shutdownRequested)
            {
                using var pipe = CreateServerPipe(AdminHelperProtocol.PipeName);
                StartupLogger.Write("Persistent admin helper waiting for client connection...");
                var connected = await WaitForConnectionAsync(pipe, TimeSpan.FromHours(24)).ConfigureAwait(false);
                if (!connected)
                {
                    continue;
                }

                StartupLogger.Write("Persistent admin helper connected successfully.");
                using var reader = new StreamReader(
                    pipe,
                    AdminHelperProtocol.PipeTextEncoding,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: AdminHelperProtocol.PipeTextBufferSize,
                    leaveOpen: true);
                using var writer = new StreamWriter(
                    pipe,
                    AdminHelperProtocol.PipeTextEncoding,
                    bufferSize: AdminHelperProtocol.PipeTextBufferSize,
                    leaveOpen: true);

                while (pipe.IsConnected && !_shutdownRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    var request = JsonSerializer.Deserialize<HelperRequest>(line, AdminHelperProtocol.JsonOptions);
                    if (request == null)
                    {
                        continue;
                    }

                    StartupLogger.Write($"Persistent admin helper received command: {request.Command}");
                    var response = await HandleRequestAsync(request).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(response, AdminHelperProtocol.JsonOptions);
                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Persistent admin helper failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Cleanup();
            try { _daemonMutex?.ReleaseMutex(); } catch { }
            try { _daemonMutex?.Dispose(); } catch { }
            _daemonMutex = null;
        }
    }

    private async Task<HelperResponse> HandleRequestAsync(HelperRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case "StartVpn":
                {
                    StartupLogger.Write("HandleRequestAsync: Processing StartVpn command...");
                    var config = DeserializePayload<VpnLaunchConfig>(request.Payload);
                    if (config == null)
                    {
                        StartupLogger.Write("HandleRequestAsync: VPN config deserialization failed");
                        return Fail(request, "Invalid VPN configuration.");
                    }

                    var corePath = string.Equals(config.VpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.OrdinalIgnoreCase)
                        ? config.XrayPath
                        : config.SingBoxPath;
                    StartupLogger.Write($"HandleRequestAsync: Starting VPN. Core={config.VpnCoreMode}, CorePath={corePath}");
                    _isStopping = false;
                    await _vpnService.StartAsync(config, default).ConfigureAwait(false);
                    StartupLogger.Write("HandleRequestAsync: VPN started successfully");
                    return Ok(request, null);
                }
                case "StopVpn":
                    _isStopping = true;
                    _vpnService.Stop();
                    return Ok(request, null);
                case "EnableKillSwitch":
                    _killSwitchEnabled = true;
                    EnableKillSwitchEmergencyBlock();
                    return Ok(request, null);
                case "DisableKillSwitch":
                    _killSwitchEnabled = false;
                    DisableKillSwitchEmergencyBlock();
                    return Ok(request, null);
                case "EnableOnionDnsProxy":
                {
                    var payload = DeserializePayload<AdminHelperDnsProxyRequest>(request.Payload);
                    var success = _dnsProxyService.Enable(payload?.NameServerAddress ?? "127.0.0.1", payload?.RouteAllDns ?? false, LogVpnHelperLine);
                    return success
                        ? Ok(request, null)
                        : Fail(request, "DNS-over-Tor could not be enabled.");
                }
                case "DisableOnionDnsProxy":
                    _dnsProxyService.Disable(LogVpnHelperLine);
                    return Ok(request, null);
                case "EnsurePersistentHelper":
                {
                    var payload = DeserializePayload<PersistentAdminHelperRequest>(request.Payload);
                    var processPath = Environment.ProcessPath;
                    if (string.IsNullOrWhiteSpace(processPath))
                    {
                        return Fail(request, "Persistent admin helper install failed: executable path unavailable.");
                    }

                    var success = WindowsPersistentAdminHelper.TryEnsureInstalled(
                        processPath,
                        payload?.UserSid ?? string.Empty,
                        payload?.UserName ?? string.Empty,
                        LogVpnHelperLine,
                        out var error);
                    return success
                        ? Ok(request, null)
                        : Fail(request, error ?? "Persistent admin helper install failed.");
                }
                case "GetStatus":
                    return Ok(request, new AdminHelperStatus
                    {
                        VpnRunning = _vpnService.IsRunning,
                        VpnExitCode = _vpnService.ExitCode,
                        KillSwitchEnabled = _killSwitchEnabled,
                        IsAdministrator = WindowsAdmin.IsAdministrator(),
                        Mode = _persistentMode ? "persistent" : "transient"
                    });
                case "DrainLogs":
                {
                    string[] lines;
                    lock (_vpnLogLock)
                    {
                        lines = _vpnLogLines.ToArray();
                        _vpnLogLines.Clear();
                    }

                    return Ok(request, lines);
                }
                case "Shutdown":
                    _shutdownRequested = true;
                    _isStopping = true;
                    _vpnService.Stop();
                    DisableKillSwitchEmergencyBlock();
                    return Ok(request, null);
                default:
                    return Fail(request, "Unknown command.");
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"HandleRequestAsync failed: {ex.GetType().Name}: {ex.Message}");
            return Fail(request, ex.Message);
        }
    }

    private void OnVpnExited(object? sender, EventArgs e)
    {
        if (_killSwitchEnabled && !_isStopping)
        {
            EnableKillSwitchEmergencyBlock();
        }
    }

    private static T? DeserializePayload<T>(object? payload)
    {
        if (payload is JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), AdminHelperProtocol.JsonOptions);
        }

        return payload is T typed ? typed : default;
    }

    private static HelperResponse Ok(HelperRequest request, object? payload)
    {
        return new HelperResponse
        {
            RequestId = request.RequestId,
            Success = true,
            Payload = payload
        };
    }

    private static HelperResponse Fail(HelperRequest request, string message)
    {
        return new HelperResponse
        {
            RequestId = request.RequestId,
            Success = false,
            Error = message
        };
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateServerPipe(string pipeName)
    {
        var pipeSecurity = new PipeSecurity();

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is { } userSid)
            {
                pipeSecurity.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Persistent admin helper: failed to add current-user pipe ACL: {ex.Message}");
        }

        try
        {
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            pipeSecurity.AddAccessRule(new PipeAccessRule(adminSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Persistent admin helper: failed to add Administrators pipe ACL: {ex.Message}");
        }

        try
        {
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            pipeSecurity.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Persistent admin helper: failed to add SYSTEM pipe ACL: {ex.Message}");
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            pipeSecurity);
    }

    private static async Task<bool> WaitForConnectionAsync(NamedPipeServerStream server, TimeSpan timeout)
    {
        try
        {
            var waitTask = server.WaitForConnectionAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != waitTask)
            {
                return false;
            }

            await waitTask.ConfigureAwait(false);
            return server.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private void Cleanup()
    {
        _isStopping = true;
        _vpnService.Stop();
        DisableKillSwitchEmergencyBlock();
    }

    private void OnVpnOutput(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        AppendVpnLogLine(e.Data);
    }

    private void LogVpnHelperLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AppendVpnLogLine($"vpn-helper: {message}");
    }

    private void AppendVpnLogLine(string line)
    {
        lock (_vpnLogLock)
        {
            _vpnLogLines.Enqueue(line);
            while (_vpnLogLines.Count > MaxBufferedLogLines)
            {
                _vpnLogLines.Dequeue();
            }
        }
    }

    private static string GetKillSwitchRuleName() => "OnionHop KillSwitch Emergency Block";
    private static string GetKillSwitchCleanupTaskName() => "OnionHop KillSwitch Cleanup";

    private void EnableKillSwitchEmergencyBlock()
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{GetKillSwitchRuleName()}\" dir=out action=block profile=any enable=yes");
            EnableKillSwitchFailsafe();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"EnableKillSwitchEmergencyBlock failed: {ex.Message}");
        }
    }

    private void DisableKillSwitchEmergencyBlock()
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
            DisableKillSwitchFailsafe();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"DisableKillSwitchEmergencyBlock failed: {ex.Message}");
        }
    }

    private void EnableKillSwitchFailsafe()
    {
        try
        {
            var action = $"cmd /c netsh advfirewall firewall delete rule name=\\\"{GetKillSwitchRuleName()}\\\"";
            RunSchTasks($"/Create /TN \"{GetKillSwitchCleanupTaskName()}\" /TR \"{action}\" /SC ONSTART /RL HIGHEST /RU SYSTEM /F");
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"EnableKillSwitchFailsafe failed: {ex.Message}");
        }
    }

    private void DisableKillSwitchFailsafe()
    {
        try
        {
            RunSchTasks($"/Delete /TN \"{GetKillSwitchCleanupTaskName()}\" /F");
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"DisableKillSwitchFailsafe failed: {ex.Message}");
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
