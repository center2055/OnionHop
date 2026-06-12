using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Platform.Windows;

namespace OnionHopV3.Core.Services;

internal enum AdminHelperConnectionKind
{
    None,
    TransientHelper,
    PersistentDaemon
}

internal sealed class AdminHelperClient : IDisposable
{
    private PipeStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private AdminHelperConnectionKind _connectionKind;
    private bool _persistentInstallAttempted;
    private bool _disposed;

    // Opt-in gate (default false). When false, no at-logon persistent helper task is installed and any
    // leftover task from a prior version is removed once per session. Set from the connect options.
    internal static bool PersistentHelperOptIn { get; set; }

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task<bool> TryConnectWithoutStartAsync()
    {
        if (_disposed)
        {
            return false;
        }

        if (_pipe?.IsConnected == true && _reader != null && _writer != null)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pipe?.IsConnected == true && _reader != null && _writer != null)
            {
                return true;
            }

            return await TryConnectToPersistentHelperAsync(startIfNeeded: false).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<bool> EnsureConnectedAsync()
    {
        if (_disposed)
        {
            return false;
        }

        if (_pipe?.IsConnected == true && _reader != null && _writer != null)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pipe?.IsConnected == true && _reader != null && _writer != null)
            {
                return true;
            }

            if (_pipe?.IsConnected == true && (_reader == null || _writer == null))
            {
                StartupLogger.Write("AdminHelperClient.EnsureConnectedAsync: Pipe was connected but streams were null. Resetting.");
            }

            if (await TryConnectToPersistentHelperAsync(startIfNeeded: true).ConfigureAwait(false))
            {
                StartupLogger.Write("AdminHelperClient.EnsureConnectedAsync: using persistent helper.");
                return true;
            }

            // IMPORTANT: run the IPC server in the non-elevated app and have the elevated helper connect as the client.
            // This avoids cross-UAC named pipe ACL issues ("Access to the path is denied").
            var rawPipeName = $"{AdminHelperProtocol.PipeName}.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var pipeName = rawPipeName.Length <= 180 ? rawPipeName : rawPipeName[..180];

            ResetPipe();

            NamedPipeServerStream? server = null;
            try
            {
                StartupLogger.Write($"Creating pipe server: {pipeName}");
                server = CreateServerPipe(pipeName);

                if (!StartHelperProcess(pipeName))
                {
                    StartupLogger.Write("Failed to start helper process.");
                    server.Dispose();
                    return false;
                }

                StartupLogger.Write("Waiting for elevated helper to connect...");
                var connected = await WaitForConnectionAsync(server, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
                if (!connected)
                {
                    StartupLogger.Write("AdminHelperClient: Timed out waiting for helper to connect.");
                    server.Dispose();
                    return false;
                }

                StartupLogger.Write("Elevated helper connected successfully.");

                // Create the text protocol streams first, then publish them as the active connection.
                var reader = new StreamReader(
                    server,
                    AdminHelperProtocol.PipeTextEncoding,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: AdminHelperProtocol.PipeTextBufferSize,
                    leaveOpen: true);
                var writer = new StreamWriter(
                    server,
                    AdminHelperProtocol.PipeTextEncoding,
                    bufferSize: AdminHelperProtocol.PipeTextBufferSize,
                    leaveOpen: true);

                _pipe = server;
                _reader = reader;
                _writer = writer;
                _connectionKind = AdminHelperConnectionKind.TransientHelper;

                // Prevent disposal in the failure path now that the pipe is owned by this instance.
                server = null;

                StartupLogger.Write("AdminHelperClient.EnsureConnectedAsync returning true.");
                return true;
            }
            catch (Exception ex)
            {
                StartupLogger.Write("AdminHelperClient.EnsureConnectedAsync failed.", ex);
                try { server?.Dispose(); } catch { }
                ResetPipe();
                return false;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<bool> StartVpnAsync(VpnLaunchConfig config)
    {
        var result = await TryStartVpnAsync(config).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<(bool Success, string? Error)> TryStartVpnAsync(VpnLaunchConfig config)
    {
        StartupLogger.Write("TryStartVpnAsync: Sending StartVpn command...");
        var response = await SendAsync("StartVpn", config).ConfigureAwait(false);
        if (response == null)
        {
            StartupLogger.Write("TryStartVpnAsync: Response was null (connection issue?)");
        }
        else
        {
            StartupLogger.Write($"TryStartVpnAsync: Response received. Success={response.Success}, Error={response.Error ?? "none"}");
        }
        return (response?.Success == true, response?.Error);
    }

    public async Task<bool> StopVpnAsync()
    {
        var response = await SendAsync("StopVpn", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> StopVpnIfAvailableAsync()
    {
        if (!await TryConnectWithoutStartAsync().ConfigureAwait(false))
        {
            return false;
        }

        var response = await SendIfConnectedAsync("StopVpn", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> EnableKillSwitchAsync()
    {
        var response = await SendAsync("EnableKillSwitch", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> DisableKillSwitchAsync()
    {
        var response = await SendAsync("DisableKillSwitch", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> DisableKillSwitchIfConnectedAsync()
    {
        var response = await SendIfConnectedAsync("DisableKillSwitch", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> DisableKillSwitchIfAvailableAsync()
    {
        if (!await TryConnectWithoutStartAsync().ConfigureAwait(false))
        {
            return false;
        }

        var response = await SendIfConnectedAsync("DisableKillSwitch", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> EnableOnionDnsProxyAsync(string? nameServerAddress, bool routeAllDns = false)
    {
        var response = await SendAsync(
            "EnableOnionDnsProxy",
            new AdminHelperDnsProxyRequest { NameServerAddress = nameServerAddress, RouteAllDns = routeAllDns }).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> DisableOnionDnsProxyAsync()
    {
        var response = await SendAsync("DisableOnionDnsProxy", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> DisableOnionDnsProxyIfAvailableAsync()
    {
        if (!await TryConnectWithoutStartAsync().ConfigureAwait(false))
        {
            return false;
        }

        var response = await SendIfConnectedAsync("DisableOnionDnsProxy", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<AdminHelperStatus?> GetStatusAsync()
    {
        // Don't start helper just to check status - use existing connection only
        var response = await SendIfConnectedAsync("GetStatus", null).ConfigureAwait(false);
        if (response?.Success != true)
        {
            return null;
        }

        if (response.Payload is JsonElement element)
        {
            return JsonSerializer.Deserialize<AdminHelperStatus>(element.GetRawText(), AdminHelperProtocol.JsonOptions);
        }

        return null;
    }

    public async Task<IReadOnlyList<string>> DrainLogsAsync()
    {
        var response = await SendIfConnectedAsync("DrainLogs", null).ConfigureAwait(false);
        if (response?.Success != true)
        {
            return Array.Empty<string>();
        }

        if (response.Payload is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            var lines = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                var line = item.GetString();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        if (response.Payload is string[] array)
        {
            return array;
        }

        return Array.Empty<string>();
    }

    public async Task<bool> ShutdownAsync()
    {
        if (_connectionKind == AdminHelperConnectionKind.PersistentDaemon)
        {
            ResetPipe();
            return true;
        }

        var response = await SendAsync("Shutdown", null).ConfigureAwait(false);
        ResetPipe();
        return response?.Success == true;
    }

    public async Task<bool> ShutdownIfConnectedAsync()
    {
        if (_connectionKind == AdminHelperConnectionKind.PersistentDaemon)
        {
            ResetPipe();
            return true;
        }

        var response = await SendIfConnectedAsync("Shutdown", null).ConfigureAwait(false);
        ResetPipe();
        return response?.Success == true;
    }

    public async Task<bool> ShutdownIfAvailableAsync()
    {
        if (!await TryConnectWithoutStartAsync().ConfigureAwait(false))
        {
            return false;
        }

        if (_connectionKind == AdminHelperConnectionKind.PersistentDaemon)
        {
            ResetPipe();
            return true;
        }

        var response = await SendIfConnectedAsync("Shutdown", null).ConfigureAwait(false);
        ResetPipe();
        return response?.Success == true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetPipe();
        _lock.Dispose();
        _connectLock.Dispose();
    }

    private async Task<bool> TryConnectToPersistentHelperAsync(bool startIfNeeded)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (await TryOpenPersistentPipeAsync(TimeSpan.FromMilliseconds(350)).ConfigureAwait(false))
        {
            return true;
        }

        if (!startIfNeeded)
        {
            return false;
        }

        if (!WindowsPersistentAdminHelper.TryStartForCurrentUser(message => StartupLogger.Write(message)))
        {
            return false;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (await TryOpenPersistentPipeAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> TryOpenPersistentPipeAsync(TimeSpan timeout)
    {
        NamedPipeClientStream? client = null;
        try
        {
            client = new NamedPipeClientStream(
                ".",
                AdminHelperProtocol.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var timeoutCts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!client.IsConnected)
            {
                client.Dispose();
                return false;
            }

            var reader = new StreamReader(
                client,
                AdminHelperProtocol.PipeTextEncoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: AdminHelperProtocol.PipeTextBufferSize,
                leaveOpen: true);
            var writer = new StreamWriter(
                client,
                AdminHelperProtocol.PipeTextEncoding,
                bufferSize: AdminHelperProtocol.PipeTextBufferSize,
                leaveOpen: true);

            ResetPipe();
            _pipe = client;
            _reader = reader;
            _writer = writer;
            _connectionKind = AdminHelperConnectionKind.PersistentDaemon;
            client = null;

            var status = await GetStatusAsync().ConfigureAwait(false);
            if (status?.IsAdministrator == true && string.Equals(status.Mode, "persistent", StringComparison.OrdinalIgnoreCase))
            {
                StartupLogger.Write("AdminHelperClient: connected to persistent admin helper.");
                return true;
            }

            StartupLogger.Write("AdminHelperClient: persistent helper connection was not elevated; falling back.");
            ResetPipe();
            return false;
        }
        catch (OperationCanceledException)
        {
            try { client?.Dispose(); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"AdminHelperClient: persistent helper connect failed: {ex.Message}");
            try { client?.Dispose(); } catch { }
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task TryInstallPersistentHelperInlineAsync()
    {
        if (_connectionKind != AdminHelperConnectionKind.TransientHelper || _persistentInstallAttempted)
        {
            StartupLogger.Write($"AdminHelperClient: skipping persistent helper bootstrap. ConnectionKind={_connectionKind}, Attempted={_persistentInstallAttempted}");
            return;
        }

        _persistentInstallAttempted = true;

        var processPath = Environment.ProcessPath;
        if (!WindowsPersistentAdminHelper.IsEligibleInstallationPath(processPath))
        {
            StartupLogger.Write($"AdminHelperClient: persistent helper bootstrap skipped because install path was not eligible. ProcessPath={processPath ?? "<null>"}");
            return;
        }

        string? userSid = null;
        string? userName = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            userSid = identity.User?.Value;
            userName = identity.Name;
        }
        catch (Exception ex)
        {
            StartupLogger.Write("AdminHelperClient: failed to resolve current user identity for persistent helper install.", ex);
        }

        if (string.IsNullOrWhiteSpace(userSid) || string.IsNullOrWhiteSpace(userName))
        {
            StartupLogger.Write("AdminHelperClient: persistent helper bootstrap skipped because user identity was incomplete.");
            return;
        }

        var response = await SendCoreWithoutLockAsync(
            "EnsurePersistentHelper",
            new PersistentAdminHelperRequest
            {
                UserSid = userSid,
                UserName = userName
            }).ConfigureAwait(false);

        if (response?.Success == true)
        {
            StartupLogger.Write("AdminHelperClient: persistent admin helper installed or refreshed.");
        }
        else if (!string.IsNullOrWhiteSpace(response?.Error))
        {
            StartupLogger.Write($"AdminHelperClient: persistent admin helper install skipped: {response.Error}");
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task TryRemovePersistentHelperInlineAsync()
    {
        if (_connectionKind != AdminHelperConnectionKind.TransientHelper || _persistentInstallAttempted)
        {
            StartupLogger.Write($"AdminHelperClient: skipping persistent helper removal. ConnectionKind={_connectionKind}, Attempted={_persistentInstallAttempted}");
            return;
        }

        _persistentInstallAttempted = true;

        string? userSid = null;
        string? userName = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            userSid = identity.User?.Value;
            userName = identity.Name;
        }
        catch (Exception ex)
        {
            StartupLogger.Write("AdminHelperClient: failed to resolve current user identity for persistent helper removal.", ex);
        }

        if (string.IsNullOrWhiteSpace(userSid) || string.IsNullOrWhiteSpace(userName))
        {
            StartupLogger.Write("AdminHelperClient: persistent helper removal skipped because user identity was incomplete.");
            return;
        }

        var response = await SendCoreWithoutLockAsync(
            "RemovePersistentHelper",
            new PersistentAdminHelperRequest
            {
                UserSid = userSid,
                UserName = userName
            }).ConfigureAwait(false);

        if (response?.Success == true)
        {
            StartupLogger.Write("AdminHelperClient: persistent admin helper task removed (or already absent).");
        }
        else if (!string.IsNullOrWhiteSpace(response?.Error))
        {
            StartupLogger.Write($"AdminHelperClient: persistent admin helper removal skipped: {response.Error}");
        }
    }

    private async Task<HelperResponse?> SendAsync(string command, object? payload)
    {
        return await SendCoreAsync(command, payload, ensureConnected: true).ConfigureAwait(false);
    }

    private async Task<HelperResponse?> SendIfConnectedAsync(string command, object? payload)
    {
        return await SendCoreAsync(command, payload, ensureConnected: false).ConfigureAwait(false);
    }

    private async Task<HelperResponse?> SendCoreAsync(string command, object? payload, bool ensureConnected)
    {
        StartupLogger.Write($"SendCoreAsync: command={command}, ensureConnected={ensureConnected}");
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ensureConnected)
            {
                if (!await EnsureConnectedAsync().ConfigureAwait(false))
                {
                    StartupLogger.Write("SendCoreAsync: EnsureConnectedAsync returned false");
                    return null;
                }
            }
            else if (_pipe?.IsConnected != true)
            {
                StartupLogger.Write("SendCoreAsync: Pipe not connected");
                return null;
            }

            if (_writer == null || _reader == null)
            {
                StartupLogger.Write("SendCoreAsync: Writer or reader is null");
                return null;
            }

            var response = await SendCoreWithoutLockAsync(command, payload).ConfigureAwait(false);
            if (response != null
                && ensureConnected
                && OperatingSystem.IsWindows()
                && !string.Equals(command, "EnsurePersistentHelper", StringComparison.Ordinal)
                && !string.Equals(command, "RemovePersistentHelper", StringComparison.Ordinal)
                && _connectionKind == AdminHelperConnectionKind.TransientHelper
                && !_persistentInstallAttempted)
            {
                if (PersistentHelperOptIn)
                {
                    await TryInstallPersistentHelperInlineAsync().ConfigureAwait(false);
                }
                else
                {
                    // Opt-out (default): make sure a task left over from a prior version is removed via
                    // the elevated (transient) helper. Runs at most once per session.
                    await TryRemovePersistentHelperInlineAsync().ConfigureAwait(false);
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            StartupLogger.Write("AdminHelperClient.SendCoreAsync failed.", ex);
            ResetPipe();
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<HelperResponse?> SendCoreWithoutLockAsync(string command, object? payload)
    {
        if (_writer == null || _reader == null)
        {
            StartupLogger.Write("SendCoreWithoutLockAsync: Writer or reader is null");
            return null;
        }

        var request = new HelperRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Command = command,
            Payload = payload
        };

        var json = JsonSerializer.Serialize(request, AdminHelperProtocol.JsonOptions);
        StartupLogger.Write($"SendCoreAsync: Sending JSON ({json.Length} chars)...");
        await _writer.WriteLineAsync(json).ConfigureAwait(false);
        await _writer.FlushAsync().ConfigureAwait(false);
        StartupLogger.Write("SendCoreAsync: Waiting for response...");

        var responseLine = await _reader.ReadLineAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            StartupLogger.Write("SendCoreAsync: Empty response received");
            return null;
        }

        StartupLogger.Write($"SendCoreAsync: Response received ({responseLine.Length} chars)");
        return JsonSerializer.Deserialize<HelperResponse>(responseLine, AdminHelperProtocol.JsonOptions);
    }

    private static NamedPipeServerStream CreateServerPipe(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        // We intentionally allow:
        // - the current (non-elevated) user (so the app can use it),
        // - Built-in Administrators (so the elevated helper can connect even if it runs under a different admin account),
        // - Everyone (to ensure cross-integrity-level communication works with UAC),
        // - LocalSystem (defensive; some environments launch elevated helpers differently).
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
            StartupLogger.Write("AdminHelperClient: Failed to add current-user pipe ACL.", ex);
        }

        try
        {
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            pipeSecurity.AddAccessRule(new PipeAccessRule(adminSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        catch (Exception ex)
        {
            StartupLogger.Write("AdminHelperClient: Failed to add Administrators pipe ACL.", ex);
        }

        // Intentionally NOT granting Everyone/World access. The elevated helper runs as the same
        // user (UAC-elevated) and is a member of Administrators, so the current-user + Administrators
        // + SYSTEM rules below already let it connect; mandatory integrity policy permits a high-IL
        // process to access this medium-IL pipe. Granting Everyone would let any local process connect
        // to (and receive the launch config / spoof responses on) the helper channel.

        try
        {
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            pipeSecurity.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        catch (Exception ex)
        {
            StartupLogger.Write("AdminHelperClient: Failed to add SYSTEM pipe ACL.", ex);
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

    private static bool StartHelperProcess(string pipeName)
    {
        if (!TryCreateHelperStartInfo(pipeName, out var psi))
        {
            return false;
        }

        try
        {
            StartupLogger.Write($"Starting elevated helper: \"{psi.FileName}\" {psi.Arguments}");
            return Process.Start(psi) != null;
        }
        catch (Exception ex)
        {
            StartupLogger.Write("AdminHelperClient.StartHelperProcess failed.", ex);
            return false;
        }
    }

    private static bool TryCreateHelperStartInfo(string pipeName, out ProcessStartInfo startInfo)
    {
        startInfo = new ProcessStartInfo();

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            StartupLogger.Write("AdminHelperClient: Environment.ProcessPath was empty.");
            return false;
        }

        var args = $"--helper --pipe {QuoteArg(pipeName)}";
        if (LooksLikeDotNetHost(processPath))
        {
            var entryAssemblyPath = TryGetEntryAssemblyPath();
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                StartupLogger.Write($"AdminHelperClient: ProcessPath looks like dotnet host ('{processPath}') but entry assembly path could not be resolved.");
                return false;
            }

            // When OnionHopV2 is launched via `dotnet`, ProcessPath points to dotnet.exe.
            // In that case, we must invoke: dotnet <OnionHopV3.dll> --helper --pipe <name>
            args = $"{QuoteArg(entryAssemblyPath)} --helper --pipe {QuoteArg(pipeName)}";
        }

        startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = args,
            WorkingDirectory = AppContext.BaseDirectory
        };

        return true;
    }

    private static bool LooksLikeDotNetHost(string processPath)
    {
        var fileName = Path.GetFileName(processPath);
        return fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetEntryAssemblyPath()
    {
        try
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            if (!string.IsNullOrWhiteSpace(entryAssembly?.Location))
            {
                return entryAssembly.Location;
            }

            var name = entryAssembly?.GetName().Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Framework-dependent apps launched via `dotnet` typically live beside the .dll in BaseDirectory.
            var dllPath = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (File.Exists(dllPath))
            {
                return dllPath;
            }

            // As a fallback, also try the .exe (e.g., apphost/portable scenarios).
            var exePath = Path.Combine(AppContext.BaseDirectory, name + ".exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write("AdminHelperClient: Failed to resolve entry assembly path.", ex);
        }

        return null;
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        if (!arg.Contains(' ') && !arg.Contains('\t') && !arg.Contains('"'))
        {
            return arg;
        }

        return $"\"{arg.Replace("\"", "\\\"")}\"";
    }

    private void ResetPipe()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"ResetPipe: Failed to dispose writer: {ex.Message}");
        }

        try
        {
            _reader?.Dispose();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"ResetPipe: Failed to dispose reader: {ex.Message}");
        }

        try
        {
            _pipe?.Dispose();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"ResetPipe: Failed to dispose pipe: {ex.Message}");
        }

        _writer = null;
        _reader = null;
        _pipe = null;
        _connectionKind = AdminHelperConnectionKind.None;
    }
}
