using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV2.Core.Dependencies;
using OnionHopV2.Core.Networking;
using OnionHopV2.Core.Platform.Windows;
using OnionHopV2.Core.Services;
using OnionHopV2.Core.Tor;

namespace OnionHopV2.Core;

public sealed class OnionHopClient : IDisposable
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex SingBoxConnectionToRegex = new(@"connection to (?<dest>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SingBoxConnectionIdRegex = new(@"\[(?<id>\d+)\s", RegexOptions.Compiled);
    private static readonly Regex SingBoxDirectOutboundDestRegex = new(@"outbound/direct\[[^\]]+\]: outbound connection to (?<dest>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SingBoxClosedConnectionDestRegex = new(@"->(?<dest>\[[^\]]+\]:\d+|[^:\s]+:\d+):", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const int DefaultSocksPort = OnionHopConnectOptions.DefaultSocksPort;
    public const int DefaultHttpPort = OnionHopConnectOptions.DefaultHttpPort;
    public const int DefaultDnsPort = 53;
    private const int MaxBridgeLinesForLaunch = 64;
    private const int MaxBridgeArgumentCharsForLaunch = 12000;
    private const int AutomaticBridgeProxyFailureThreshold = 8;
    private static readonly TimeSpan AutomaticBridgeProxyFailureWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AutomaticBridgeStabilityProbeDelay = TimeSpan.FromSeconds(4);

    public readonly record struct StatusUpdate(
        bool IsConnecting,
        bool IsConnected,
        bool IsDisconnecting,
        string ConnectionStatus,
        string StatusMessage,
        double ConnectionProgress,
        string CurrentIp,
        int SocksPort,
        int? HttpPort);

    public readonly record struct DependencyUpdate(bool InProgress, string Status, double Progress);

    public event EventHandler<string>? Log;
    public event EventHandler<string>? DnsLog;
    public event EventHandler<StatusUpdate>? StatusUpdated;
    public event EventHandler<DependencyUpdate>? DependencyUpdated;

    private readonly string _baseDir;
    private readonly DependencyManager _deps = new();
    private readonly TorBridgeManager _bridgeManager;
    private readonly WindowsProxyService _proxyService = new();
    private readonly WindowsOnionDnsProxyService _onionDnsProxyService = new();
    private readonly TorNodeDatabaseService _nodeDatabaseService = new();

    private readonly TorService _torService;
    private readonly VpnService _vpnService;
    private readonly AdminHelperClient _adminHelper = new();

    private Task<bool>? _dependencyEnsureTask;
    private PluggableTransportConfig? _ptConfig;

    private TaskCompletionSource<bool>? _bootstrapSource;

    private bool _isConnecting;
    private bool _isConnected;
    private bool _isDisconnecting;
    private string _connectionStatus = "Disconnected";
    private string _statusMessage = "Ready to route traffic through Tor.";
    private double _connectionProgress;
    private string _currentIp = "--.--.--.--";

    private bool _dependencyDownloadInProgress;
    private string _dependencyDownloadStatus = "Checking components...";
    private double _dependencyDownloadProgress;

    private DateTime _lastNewnymUtc = DateTime.MinValue;
    private OnionHopConnectOptions? _activeOptions;
    private bool _snowflakeAmpHintShown;
    private int _activeSocksPort = DefaultSocksPort;
    private int? _activeHttpPort;
    private int? _activeDnsPort;
    private string? _activeDnsBindAddress;

    private readonly object _singBoxLogLock = new();
    private readonly Queue<string> _singBoxRecentLines = new();
    private DateTime _lastVpnMessageUtc = DateTime.MinValue;
    private CancellationTokenSource? _adminVpnMonitorCts;
    private readonly object _bridgeFailureLock = new();
    private readonly Queue<DateTimeOffset> _recentTorProxyFailures = new();
    private readonly HashSet<string> _webTunnelConnectionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _webTunnelConnectionDestinations = new(StringComparer.Ordinal);

    public OnionHopClient(string? baseDirectory = null)
    {
        _baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory!;
        _bridgeManager = new TorBridgeManager(_baseDir);

        _torService = new TorService(RaiseLog);
        _vpnService = new VpnService(RaiseLog);

        _torService.OutputReceived += OnTorDataReceived;
        _torService.Exited += OnTorExited;

        _vpnService.OutputReceived += OnSingBoxDataReceived;
        _vpnService.Exited += OnSingBoxExited;
    }

    public string BaseDirectory => _baseDir;

    public IReadOnlyList<string> GetBridgeTypes()
    {
        return TorBridgeManager.GetBridgeTypeKeys(_ptConfig);
    }

    public string? GetRecommendedBridgeType()
    {
        return _ptConfig?.RecommendedDefault;
    }

    public async Task<(long BytesRead, long BytesWritten)?> TryGetTorTrafficBytesAsync(CancellationToken token = default)
    {
        try
        {
            if (!_torService.IsRunning)
            {
                return null;
            }

            return await _torService.TryGetTrafficBytesAsync(token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> EnsureDependenciesAsync(CancellationToken token = default)
    {
        return await (_dependencyEnsureTask ??= EnsureDependenciesCoreAsync(token)).ConfigureAwait(false);
    }

    public async Task<bool> EnsureAdminHelperAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            StartupLogger.Write("OnionHopClient.EnsureAdminHelperAsync: calling _adminHelper.EnsureConnectedAsync...");
            var result = await _adminHelper.EnsureConnectedAsync().ConfigureAwait(false);
            StartupLogger.Write($"OnionHopClient.EnsureAdminHelperAsync: _adminHelper.EnsureConnectedAsync returned {result}");
            return result;
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"OnionHopClient.EnsureAdminHelperAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            RaiseLog($"EnsureAdminHelperAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task ConnectAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        StartupLogger.Write("OnionHopClient.ConnectAsync: Starting...");
        
        if (_isConnecting)
        {
            StartupLogger.Write("OnionHopClient.ConnectAsync: Already connecting, returning");
            return;
        }

        if (_isConnected)
        {
            StartupLogger.Write("OnionHopClient.ConnectAsync: Already connected, disconnecting first");
            await DisconnectAsync().ConfigureAwait(false);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            StartupLogger.Write("OnionHopClient.ConnectAsync: Not Windows");
            SetStatus(
                isConnecting: false,
                isConnected: false,
                isDisconnecting: false,
                connectionStatus: "Unavailable",
                statusMessage: "Windows-only for now. Linux/macOS integration will be added later.",
                progress: 0);
            return;
        }

        StartupLogger.Write("OnionHopClient.ConnectAsync: Checking dependencies...");
        if (!await EnsureDependenciesAsync(token).ConfigureAwait(false))
        {
            StartupLogger.Write("OnionHopClient.ConnectAsync: Dependencies check failed!");
            SetStatus(
                isConnecting: false,
                isConnected: false,
                isDisconnecting: false,
                connectionStatus: "Disconnected",
                statusMessage: "Failed to verify or download required components.",
                progress: 0);
            return;
        }
        StartupLogger.Write("OnionHopClient.ConnectAsync: Dependencies OK");

        var preferredSocksPort = NormalizePreferredProxyPort(options.PreferredSocksPort, DefaultSocksPort);
        _activeSocksPort = PortSelector.FindAvailablePort(preferredSocksPort, additionalAttempts: 30);
        if (_activeSocksPort != preferredSocksPort)
        {
            RaiseLog($"SOCKS port {preferredSocksPort} is busy. Using {_activeSocksPort}.");
        }

        var preferredHttpPort = NormalizePreferredProxyPort(options.PreferredHttpPort, DefaultHttpPort);
        _activeHttpPort = PortSelector.FindAvailablePort(
            preferredHttpPort,
            additionalAttempts: 30,
            excludedPorts: [_activeSocksPort]);
        if (_activeHttpPort != preferredHttpPort)
        {
            RaiseLog($"HTTP tunnel port {preferredHttpPort} is busy. Using {_activeHttpPort}.");
        }

        _activeDnsPort = null;
        _activeDnsBindAddress = null;
        if (options.OnionDnsProxyEnabled)
        {
            var dnsEndpoint = SelectOnionDnsEndpoint(out var attemptedDnsCandidates);
            if (dnsEndpoint.HasValue)
            {
                _activeDnsBindAddress = dnsEndpoint.Value.Address;
                _activeDnsPort = DefaultDnsPort;
                if (!string.Equals(_activeDnsBindAddress, "127.0.0.1", StringComparison.Ordinal))
                {
                    RaiseLog($"Onion DNS proxying: 127.0.0.1:{DefaultDnsPort} busy. Using {_activeDnsBindAddress}:{DefaultDnsPort}.");
                }
            }
            else
            {
                var attemptedList = attemptedDnsCandidates.Count == 0
                    ? "none"
                    : string.Join(", ", attemptedDnsCandidates);
                RaiseLog($"Onion DNS proxying requested, but all tested loopback candidates on TCP/UDP port 53 are busy ({attemptedList}). Continuing without DNS proxying.");
            }
        }

        var connectTimeout = options.UseTorBridges
            ? TorBridgeManager.IsAutomaticBridgeType(options.SelectedBridgeType)
                ? TimeSpan.FromSeconds(360)
                : TimeSpan.FromSeconds(240)
            : TimeSpan.FromSeconds(60);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(connectTimeout);

        _activeOptions = options;
        _snowflakeAmpHintShown = false;
        SetStatus(
            isConnecting: true,
            isConnected: false,
            isDisconnecting: false,
            connectionStatus: "Connecting...",
            statusMessage: "Starting Tor and bootstrapping network...",
            progress: 0.1);

        _currentIp = "Resolving...";
        PublishStatus();

        try
        {
            RaiseLog($"Connecting. Mode={options.SelectedConnectionMode}, Hybrid={options.UseHybridRouting}, Exit={options.SelectedLocation}, Bridges={(options.UseTorBridges ? options.SelectedBridgeType : "off")}");

            var resolvedOptions = await StartTorWithBridgeFallbackAsync(options, timeoutCts.Token).ConfigureAwait(false);
            _activeOptions = resolvedOptions;

            if (resolvedOptions.OnionDnsProxyEnabled)
            {
                if (!WindowsAdmin.IsAdministrator())
                {
                    RaiseLog(".onion DNS proxying requires Administrator; skipping.");
                }
                else if (_activeDnsPort == DefaultDnsPort && !string.IsNullOrWhiteSpace(_activeDnsBindAddress))
                {
                    _onionDnsProxyService.Enable(_activeDnsBindAddress!, RaiseLog);
                }
            }

            if (IsTunMode(resolvedOptions))
            {
                _connectionProgress = Math.Max(_connectionProgress, 0.9);
                _statusMessage = resolvedOptions.UseHybridRouting
                    ? "Tor is running. Starting Hybrid tunnel (web via Tor)..."
                    : "Tor is running. Starting VPN tunnel (all traffic via Tor)...";
                PublishStatus();

                await StartSingBoxVpnAsync(resolvedOptions, timeoutCts.Token).ConfigureAwait(false);
            }
            else
            {
                if (UsesSystemProxyScope(resolvedOptions))
                {
                    _proxyService.ApplyTorProxy(_activeSocksPort, _activeHttpPort, RaiseLog);
                }
                else
                {
                    RaiseLog(BuildManualProxyHint(_activeSocksPort, _activeHttpPort));
                }
            }

            _isConnected = true;
            _connectionStatus = "Connected";
            _connectionProgress = 1;
            _statusMessage = IsTunMode(resolvedOptions)
                ? (resolvedOptions.UseHybridRouting
                    ? "Tor is running. Hybrid routing is active (browser via Tor)."
                    : "Tor is running. VPN tunnel is active (all traffic via Tor).")
                : UsesSystemProxyScope(resolvedOptions)
                    ? "Tor is running. System proxy mode is active."
                    : "Tor is running. Local proxy mode is active (configure apps manually).";
            PublishStatus();

            await RefreshIpAsync(updateStatusMessage: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RaiseLog("Connect canceled or timed out.");
            await DisconnectCoreAsync(disableStatusUpdate: true).ConfigureAwait(false);
            SetStatus(
                isConnecting: false,
                isConnected: false,
                isDisconnecting: false,
                connectionStatus: "Disconnected",
                statusMessage: "Connection canceled or timed out.",
                progress: 0);
        }
        catch (Exception ex)
        {
            RaiseLog($"Connect failed: {ex.Message}");
            await DisconnectCoreAsync(disableStatusUpdate: true).ConfigureAwait(false);
            SetStatus(
                isConnecting: false,
                isConnected: false,
                isDisconnecting: false,
                connectionStatus: "Disconnected",
                statusMessage: $"Failed to connect: {ex.Message}",
                progress: 0);
        }
        finally
        {
            _isConnecting = false;
            PublishStatus();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_isConnecting)
        {
            return;
        }

        if (!_isConnected && !_torService.IsRunning)
        {
            return;
        }

        _isDisconnecting = true;
        SetStatus(
            isConnecting: false,
            isConnected: _isConnected,
            isDisconnecting: true,
            connectionStatus: "Disconnecting...",
            statusMessage: "Stopping Tor...",
            progress: 0.2);

        await DisconnectCoreAsync(disableStatusUpdate: false).ConfigureAwait(false);
    }

    public async Task RefreshIpAsync(bool updateStatusMessage, CancellationToken token)
    {
        var torFirst = _isConnected && _torService.IsRunning;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(18));

        try
        {
            string? ip = null;

            if (torFirst)
            {
                ip = await IpLookupService.TryFetchTorExitIpAsync(_activeSocksPort, RaiseLog, cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    _currentIp = ip;
                    if (updateStatusMessage)
                    {
                        _statusMessage = "Tor exit IP refreshed.";
                    }
                    PublishStatus();
                    return;
                }
            }

            ip = await IpLookupService.TryFetchDirectIpAsync(RaiseLog, cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ip))
            {
                _currentIp = ip;
                if (updateStatusMessage)
                {
                    _statusMessage = torFirst
                        ? "Tor IP lookup failed. Showing direct IP."
                        : "Direct IP refreshed.";
                }
                PublishStatus();
                return;
            }

            _currentIp = "--.--.--.--";
            if (updateStatusMessage)
            {
                _statusMessage = "Unable to fetch IP.";
            }
            PublishStatus();
        }
        catch (OperationCanceledException)
        {
            _currentIp = "--.--.--.--";
            if (updateStatusMessage)
            {
                _statusMessage = "IP lookup timed out.";
            }
            PublishStatus();
        }
    }

    public async Task ChangeIdentityAsync(CancellationToken token)
    {
        if (!_isConnected || !_torService.IsRunning)
        {
            _statusMessage = "Connect to Tor before requesting a new identity.";
            PublishStatus();
            return;
        }

        if (DateTime.UtcNow - _lastNewnymUtc < TimeSpan.FromSeconds(10))
        {
            _statusMessage = "Please wait a moment before requesting another identity.";
            PublishStatus();
            return;
        }

        _statusMessage = "Requesting a new Tor circuit...";
        PublishStatus();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        var success = await _torService.SendControlSignalAsync("SIGNAL NEWNYM", cts.Token).ConfigureAwait(false);
        if (!success)
        {
            _statusMessage = "Unable to request a new identity. Check Tor is running.";
            PublishStatus();
            return;
        }

        _lastNewnymUtc = DateTime.UtcNow;
        await Task.Delay(1200, token).ConfigureAwait(false);
        await RefreshIpAsync(updateStatusMessage: true, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _adminVpnMonitorCts?.Cancel();
            _adminVpnMonitorCts = null;
        }
        catch
        {
        }

        try
        {
            if (_proxyService.IsApplied)
            {
                _proxyService.RestorePreviousProxy(RaiseLog);
            }
        }
        catch
        {
        }

        try
        {
            _onionDnsProxyService.Disable(RaiseLog);
        }
        catch
        {
        }

        try
        {
            StopSingBoxProcess();
        }
        catch
        {
        }

        try
        {
            StopTorProcess();
        }
        catch
        {
        }

        try
        {
            if (_adminHelper.IsConnected)
            {
                _adminHelper.ShutdownIfConnectedAsync().Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
        }

        _adminHelper.Dispose();
        _vpnService.Dispose();
        _torService.Dispose();
    }

    private async Task<bool> EnsureDependenciesCoreAsync(CancellationToken token)
    {
        StartupLogger.Write("EnsureDependenciesCoreAsync: Starting dependency check...");
        
        void Progress(DependencyManager.DependencyUpdate update)
        {
            _dependencyDownloadInProgress = update.InProgress;
            _dependencyDownloadStatus = update.Status;
            _dependencyDownloadProgress = update.Progress;
            PublishDependency();
        }

        var success = await _deps.EnsureAsync(_baseDir, Progress, RaiseLog, token).ConfigureAwait(false);
        StartupLogger.Write($"EnsureDependenciesCoreAsync: _deps.EnsureAsync returned {success}");
        if (!success)
        {
            return false;
        }

        _ptConfig = DependencyManager.TryLoadPluggableTransportConfig(_baseDir, RaiseLog);
        return true;
    }

    private async Task DisconnectCoreAsync(bool disableStatusUpdate)
    {
        try
        {
            StopSingBoxProcess();

            if (KillSwitchService.IsEmergencyBlockActive())
            {
                if (WindowsAdmin.IsAdministrator())
                {
                    KillSwitchService.DisableEmergencyBlock(RaiseLog);
                }
                else
                {
                    _ = Task.Run(async () => await _adminHelper.DisableKillSwitchIfAvailableAsync().ConfigureAwait(false));
                }
            }

            if (_proxyService.IsApplied)
            {
                _proxyService.RestorePreviousProxy(RaiseLog);
            }

            if (_activeOptions?.OnionDnsProxyEnabled == true)
            {
                _onionDnsProxyService.Disable(RaiseLog);
            }

            StopTorProcess();
            await Task.Delay(250).ConfigureAwait(false);
        }
        finally
        {
            _activeOptions = null;
            _isConnected = false;
            _isDisconnecting = false;
            _connectionStatus = "Disconnected";
            _connectionProgress = 0;
            _activeSocksPort = DefaultSocksPort;
            _activeHttpPort = null;
            _activeDnsPort = null;
            _activeDnsBindAddress = null;

            if (!disableStatusUpdate)
            {
                _statusMessage = "Tor stopped. Traffic is back to normal.";
                _currentIp = "Resolving...";
            }

            PublishStatus();

            if (!disableStatusUpdate)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RefreshIpAsync(updateStatusMessage: false, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                });
            }
        }
    }

    private void PublishStatus()
    {
        StatusUpdated?.Invoke(this, new StatusUpdate(
            IsConnecting: _isConnecting,
            IsConnected: _isConnected,
            IsDisconnecting: _isDisconnecting,
            ConnectionStatus: _connectionStatus,
            StatusMessage: _statusMessage,
            ConnectionProgress: _connectionProgress,
            CurrentIp: _currentIp,
            SocksPort: _activeSocksPort,
            HttpPort: _activeHttpPort));
    }

    private void PublishDependency()
    {
        DependencyUpdated?.Invoke(this, new DependencyUpdate(_dependencyDownloadInProgress, _dependencyDownloadStatus, _dependencyDownloadProgress));
    }

    private void SetStatus(bool isConnecting, bool isConnected, bool isDisconnecting, string connectionStatus, string statusMessage, double progress)
    {
        _isConnecting = isConnecting;
        _isConnected = isConnected;
        _isDisconnecting = isDisconnecting;
        _connectionStatus = connectionStatus;
        _statusMessage = statusMessage;
        _connectionProgress = progress;
        PublishStatus();
    }

    private static bool IsTunMode(OnionHopConnectOptions options)
    {
        return string.Equals(options.SelectedConnectionMode, OnionHopConnectOptions.ConnectionModeTun, StringComparison.Ordinal);
    }

    private void RaiseLog(string message)
    {
        Log?.Invoke(this, message);
    }

    private void RaiseDnsLog(string message)
    {
        DnsLog?.Invoke(this, message);
    }

    private async Task<OnionHopConnectOptions> StartTorWithBridgeFallbackAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        if (!options.UseTorBridges || !TorBridgeManager.IsAutomaticBridgeType(options.SelectedBridgeType))
        {
            _activeOptions = options;
            await StartTorAsync(options, token).ConfigureAwait(false);
            return options;
        }

        var attempts = TorBridgeManager.BuildAutomaticBridgeFallbackOrder(options);
        if (attempts.Count == 0)
        {
            attempts = ["webtunnel", "snowflake", "obfs4"];
        }

        Exception? lastError = null;
        for (var index = 0; index < attempts.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            ResetBridgeFailureTracking();

            var bridgeType = attempts[index];
            var attemptOptions = CloneOptionsWithBridgeType(options, bridgeType);
            RaiseLog($"Automatic bridges: trying {bridgeType} ({index + 1}/{attempts.Count})...");
            _activeOptions = attemptOptions;

            try
            {
                await StartTorAsync(attemptOptions, token).ConfigureAwait(false);
                await EnsureAutomaticBridgeAttemptStabilityAsync(attemptOptions, bridgeType, token).ConfigureAwait(false);
                RaiseLog($"Automatic bridges: connected using {bridgeType}.");
                return attemptOptions;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                RaiseLog($"Automatic bridges: {bridgeType} failed: {ex.Message}");
                await CleanupFailedBridgeAttemptAsync().ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(lastError == null
            ? "Automatic bridges failed: no usable bridge transport succeeded."
            : $"Automatic bridges failed: {lastError.Message}");
    }

    private async Task CleanupFailedBridgeAttemptAsync()
    {
        try
        {
            StopTorProcess();
        }
        catch
        {
        }

        try
        {
            await Task.Delay(200).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task EnsureAutomaticBridgeAttemptStabilityAsync(OnionHopConnectOptions options, string bridgeType, CancellationToken token)
    {
        if (!options.UseTorBridges || string.IsNullOrWhiteSpace(bridgeType))
        {
            return;
        }

        await Task.Delay(AutomaticBridgeStabilityProbeDelay, token).ConfigureAwait(false);
        var failures = CountRecentTorProxyFailures();
        if (failures < AutomaticBridgeProxyFailureThreshold)
        {
            if (failures > 0)
            {
                RaiseLog($"Automatic bridges: observed {failures} proxy handshake warning(s) during {bridgeType} startup.");
            }

            return;
        }

        throw new InvalidOperationException($"{bridgeType} bridges appear unstable ({failures} proxy handshake failures during startup).");
    }

    private static OnionHopConnectOptions CloneOptionsWithBridgeType(OnionHopConnectOptions options, string bridgeType)
    {
        return new OnionHopConnectOptions
        {
            SelectedLocation = options.SelectedLocation,
            SelectedEntryLocation = options.SelectedEntryLocation,
            ExitNodeFingerprint = options.ExitNodeFingerprint,
            SelectedConnectionMode = options.SelectedConnectionMode,
            UseHybridRouting = options.UseHybridRouting,
            KillSwitchEnabled = options.KillSwitchEnabled,
            UseTorBridges = options.UseTorBridges,
            UseCensoredMode = options.UseCensoredMode,
            SelectedBridgeType = bridgeType,
            BridgeSourceMode = options.BridgeSourceMode,
            CustomBridges = options.CustomBridges,
            CustomSniHosts = options.CustomSniHosts,
            UseSnowflakeAmp = options.UseSnowflakeAmp,
            SnowflakeAmpCache = options.SnowflakeAmpCache,
            TorIpv6Mode = options.TorIpv6Mode,
            HardwareAccelerationMode = options.HardwareAccelerationMode,
            ConnectionPaddingMode = options.ConnectionPaddingMode,
            SelectedDnsProvider = options.SelectedDnsProvider,
            CustomDohHost = options.CustomDohHost,
            CustomDohPath = options.CustomDohPath,
            ProxyScopeMode = options.ProxyScopeMode,
            PreferredSocksPort = options.PreferredSocksPort,
            PreferredHttpPort = options.PreferredHttpPort,
            RestrictedFirewallMode = options.RestrictedFirewallMode,
            AllowedPorts = options.AllowedPorts,
            OnionDnsProxyEnabled = options.OnionDnsProxyEnabled,
            StrictManualExitNodeFingerprint = options.StrictManualExitNodeFingerprint,
            MaxCircuitInactivityMinutes = options.MaxCircuitInactivityMinutes,
            OpenConnectedPageEnabled = options.OpenConnectedPageEnabled,
            ConnectedPageUrl = options.ConnectedPageUrl,
            OpenDisconnectedPageEnabled = options.OpenDisconnectedPageEnabled,
            DisconnectedPageUrl = options.DisconnectedPageUrl,
            EnableDiscordStatus = options.EnableDiscordStatus,
            HybridRouteAllWebTraffic = options.HybridRouteAllWebTraffic,
            HybridBlockQuicForTorApps = options.HybridBlockQuicForTorApps,
            HybridTorApps = options.HybridTorApps,
            HybridBypassApps = options.HybridBypassApps
        };
    }

    private async Task StartTorAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        _bootstrapSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => _bootstrapSource.TrySetCanceled(token));

        var torDir = Path.Combine(_baseDir, "tor");
        var torPath = Path.Combine(torDir, "tor.exe");
        var geoIpPath = Path.Combine(torDir, "geoip");
        var geoIp6Path = Path.Combine(torDir, "geoip6");

        IReadOnlyList<string>? bridgeLines = null;
        List<string>? normalizedPlugins = null;
        if (options.UseTorBridges)
        {
            bridgeLines = await _bridgeManager.GetBridgeLinesAsync(options, _ptConfig, RaiseLog, token).ConfigureAwait(false);
            if (bridgeLines.Count == 0)
            {
                var message = _bridgeManager.BridgeValidationMessage;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = "Bridges enabled but no bridge lines are configured.";
                }
                throw new InvalidOperationException(message);
            }

            bridgeLines = LimitBridgeLinesForLaunch(bridgeLines, RaiseLog);

            var pluginLines = _bridgeManager.GetClientTransportPlugins(options, bridgeLines, torDir, _ptConfig, RaiseLog);
            if (pluginLines.Count == 0)
            {
                var message = _bridgeManager.BridgeValidationMessage;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = "Bridges enabled but no transport plugins were found.";
                }
                throw new InvalidOperationException(message);
            }

            normalizedPlugins = pluginLines
                .Select(TorBridgeManager.NormalizeClientTransportPlugin)
                .Where(normalized => !string.IsNullOrWhiteSpace(normalized))
                .ToList();
        }

        var countries = await _nodeDatabaseService.GetCountryStatsAsync(RaiseLog, token).ConfigureAwait(false);
        var countryCode = TorNodeDatabaseService.NormalizeSelectionToCountryCode(options.SelectedLocation, countries);
        var entryCode = TorNodeDatabaseService.NormalizeSelectionToCountryCode(options.SelectedEntryLocation, countries);

        if (countries.Count > 0 &&
            !string.IsNullOrWhiteSpace(countryCode) &&
            !TorNodeDatabaseService.HasExitNodes(countries, countryCode))
        {
            RaiseLog($"Selected exit country '{options.SelectedLocation}' currently reports no running exit nodes. Continuing with the selected country.");
        }

        if (countries.Count > 0 &&
            !string.IsNullOrWhiteSpace(entryCode) &&
            !TorNodeDatabaseService.HasEntryNodes(countries, entryCode))
        {
            RaiseLog($"Selected entry country '{options.SelectedEntryLocation}' has no running guard nodes. Falling back to Automatic.");
            entryCode = string.Empty;
        }

        if (options.UseTorBridges && !string.IsNullOrWhiteSpace(entryCode))
        {
            // Tor does not allow UseBridges together with EntryNodes.
            // When bridges are enabled we silently ignore the entry pin to avoid a hard failure.
            RaiseLog("Note: Entry node pinning is not compatible with Tor bridges and will be ignored.");
            entryCode = null;
        }

        var allowedPorts = ParseAllowedPorts(options.AllowedPorts);
        var maxCircuitMinutes = Math.Clamp(options.MaxCircuitInactivityMinutes <= 0 ? 10 : options.MaxCircuitInactivityMinutes, 5, 120);

        var config = new TorLaunchConfig
        {
            TorPath = torPath,
            SocksPort = _activeSocksPort,
            HttpTunnelPort = _activeHttpPort,
            DnsPort = options.OnionDnsProxyEnabled ? _activeDnsPort : null,
            DnsListenAddress = _activeDnsBindAddress,
            GeoIpPath = geoIpPath,
            GeoIp6Path = geoIp6Path,
            BridgeLines = bridgeLines,
            ClientTransportPlugins = normalizedPlugins,
            AllowedPorts = options.RestrictedFirewallMode ? allowedPorts : null,
            MaxCircuitDirtinessSeconds = maxCircuitMinutes * 60,
            ExitCountryCode = countryCode,
            ExitNodeFingerprint = options.ExitNodeFingerprint,
            StrictManualExitNodeFingerprint = options.StrictManualExitNodeFingerprint,
            EntryCountryCode = entryCode,
            ClientUseIpv6 = ParseToggleMode(options.TorIpv6Mode),
            HardwareAccel = ParseToggleMode(options.HardwareAccelerationMode),
            ConnectionPadding = ParseConnectionPaddingMode(options.ConnectionPaddingMode)
        };

        await _torService.StartAsync(config, token).ConfigureAwait(false);
        await _bootstrapSource.Task.ConfigureAwait(false);
    }

    private async Task StartSingBoxVpnAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        RaiseLog("StartSingBoxVpnAsync: Starting VPN setup...");
        StopSingBoxProcess();

        var vpnDir = Path.Combine(_baseDir, "vpn");
        var singBoxPath = Path.Combine(vpnDir, "sing-box.exe");
        var wintunPath = Path.Combine(vpnDir, "wintun.dll");
        var doh = DohSettingsResolver.Resolve(options);
        if (options.UseCensoredMode)
        {
            var dohResolution = await DohSettingsResolver.ResolveWithHealthFallbackAsync(options, RaiseLog, token).ConfigureAwait(false);
            doh = dohResolution.Settings;
        }

        var config = new VpnLaunchConfig
        {
            SingBoxPath = singBoxPath,
            WintunPath = wintunPath,
            HybridRouting = options.UseHybridRouting,
            SecureDns = options.UseCensoredMode,
            SocksPort = _activeSocksPort,
            DohServer = doh.Server,
            DohServerPort = doh.Port,
            DohPath = doh.Path,
            TorAppProcessNames = ResolveHybridTorApps(options),
            BypassAppProcessNames = ParseProcessNames(options.HybridBypassApps),
            RouteAllWebTrafficThroughTor = options.HybridRouteAllWebTraffic,
            BlockQuicForTorApps = options.HybridBlockQuicForTorApps
        };

        RaiseLog($"StartSingBoxVpnAsync: IsAdmin={WindowsAdmin.IsAdministrator()}, SingBoxPath={singBoxPath}");

        if (!WindowsAdmin.IsAdministrator())
        {
            RaiseLog("StartSingBoxVpnAsync: Calling TryStartVpnAsync via admin helper...");
            var result = await _adminHelper.TryStartVpnAsync(config).ConfigureAwait(false);
            RaiseLog($"StartSingBoxVpnAsync: TryStartVpnAsync returned Success={result.Success}, Error={result.Error ?? "none"}");
            if (!result.Success)
            {
                var drained = await _adminHelper.DrainLogsAsync().ConfigureAwait(false);
                foreach (var logLine in drained)
                {
                    ProcessSingBoxLogLine(logLine);
                }

                var lastLines = string.Join("\n", drained.Count > 8
                    ? drained.Skip(Math.Max(0, drained.Count - 8))
                    : drained);

                var details = string.IsNullOrWhiteSpace(result.Error) ? "Unknown error." : result.Error;
                if (!string.IsNullOrWhiteSpace(lastLines))
                {
                    details += "\nLast logs:\n" + lastLines;
                }

                throw new InvalidOperationException($"Unable to start elevated VPN helper: {details}");
            }

            StartAdminVpnMonitor(options);
            return;
        }

        await _vpnService.StartAsync(config, token).ConfigureAwait(false);
    }

    private static readonly string[] BrowserProcessNames =
    [
        "firefox.exe",
        "chrome.exe",
        "msedge.exe"
    ];

    private static IReadOnlyList<string> ResolveHybridTorApps(OnionHopConnectOptions options)
    {
        var apps = new List<string>(BrowserProcessNames);
        apps.AddRange(ParseProcessNames(options.HybridTorApps));

        return apps
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ParseProcessNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var token in line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = token.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.Contains('\\') || name.Contains('/'))
                {
                    name = Path.GetFileName(name);
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    results.Add(name);
                }
            }
        }

        return results
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool? ParseToggleMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        if (string.Equals(mode, OnionHopConnectOptions.ToggleModeEnabled, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(mode, OnionHopConnectOptions.ToggleModeDisabled, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static string? ParseConnectionPaddingMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        if (string.Equals(mode, OnionHopConnectOptions.ConnectionPaddingAuto, StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (string.Equals(mode, OnionHopConnectOptions.ConnectionPaddingEnabled, StringComparison.OrdinalIgnoreCase))
        {
            return "1";
        }

        if (string.Equals(mode, OnionHopConnectOptions.ConnectionPaddingDisabled, StringComparison.OrdinalIgnoreCase))
        {
            return "0";
        }

        return null;
    }

    private void StartAdminVpnMonitor(OnionHopConnectOptions options)
    {
        _adminVpnMonitorCts?.Cancel();
        _adminVpnMonitorCts = new CancellationTokenSource();
        var token = _adminVpnMonitorCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, token).ConfigureAwait(false);
                    var status = await _adminHelper.GetStatusAsync().ConfigureAwait(false);
                    if (status == null)
                    {
                        RaiseLog("VPN helper unavailable. Disconnecting...");
                        _connectionStatus = "VPN stopped";
                        _statusMessage = "VPN helper unavailable. Disconnecting...";
                        PublishStatus();
                        await DisconnectAsync().ConfigureAwait(false);
                        return;
                    }

                    var logLines = await _adminHelper.DrainLogsAsync().ConfigureAwait(false);
                    foreach (var logLine in logLines)
                    {
                        ProcessSingBoxLogLine(logLine);
                    }

                    if (status.VpnRunning)
                    {
                        continue;
                    }

                    if (_isDisconnecting || !_isConnected || !IsTunMode(options))
                    {
                        return;
                    }

                    if (options.KillSwitchEnabled && !options.UseHybridRouting)
                    {
                        await _adminHelper.EnableKillSwitchAsync().ConfigureAwait(false);
                    }

                    RaiseLog($"VPN helper reports tunnel stopped (exit code {status.VpnExitCode?.ToString() ?? "unknown"}). Disconnecting...");
                    _connectionStatus = "VPN stopped";
                    _statusMessage = "VPN tunnel stopped unexpectedly. Disconnecting...";
                    PublishStatus();
                    await DisconnectAsync().ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    RaiseLog($"VPN helper monitor failed: {ex.Message}");
                }
            }
        }, token);
    }

    private void OnTorExited(object? sender, EventArgs e)
    {
        if (_isDisconnecting)
        {
            return;
        }

        var exitCode = _torService.ExitCode ?? 0;
        RaiseLog($"Tor exited with code {exitCode}.");

        // If Tor dies while we're connecting, fail fast instead of waiting for the connect timeout.
        if (_isConnecting)
        {
            _bootstrapSource?.TrySetException(new InvalidOperationException($"Tor exited with code {exitCode}."));
            return;
        }

        if (_isConnected)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _connectionStatus = "Tor stopped";
                    _statusMessage = $"Tor stopped unexpectedly (exit code {exitCode}). Disconnecting...";
                    _connectionProgress = 0;
                    PublishStatus();
                    await DisconnectAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }
    }

    private void OnTorDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        var line = e.Data;
        if (line.Contains("Bootstrapped", StringComparison.OrdinalIgnoreCase))
        {
            var percent = ExtractProgress(line);
            _connectionProgress = percent / 100d;
            if (_isConnecting)
            {
                var summary = ExtractBootstrapSummary(line);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    _statusMessage = summary;
                }
            }

            PublishStatus();
            if (percent >= 100)
            {
                _bootstrapSource?.TrySetResult(true);
            }

            return;
        }

        if (IsFatalTorBootstrapLine(line))
        {
            _bootstrapSource?.TrySetException(new InvalidOperationException(line));
            return;
        }

        if (IsTorProxyHandshakeFailureLine(line))
        {
            RecordRecentTorProxyFailure();
        }

        if (ShouldLogTorLine(line))
        {
            if (_isConnecting
                && !_snowflakeAmpHintShown
                && _activeOptions is { UseTorBridges: true, UseSnowflakeAmp: false } options
                && string.Equals(options.SelectedBridgeType, "snowflake", StringComparison.OrdinalIgnoreCase)
                && line.Contains("snowflake-client.exe", StringComparison.OrdinalIgnoreCase)
                && line.Contains("broker failure", StringComparison.OrdinalIgnoreCase))
            {
                _snowflakeAmpHintShown = true;
                _statusMessage = "Snowflake broker unreachable. Try enabling AMP cache in Settings → Network.";
                PublishStatus();
            }

            RaiseLog($"Tor log: {line}");
        }
    }

    private void OnSingBoxDataReceived(object sender, DataReceivedEventArgs e)
    {
        ProcessSingBoxLogLine(e.Data);
    }

    private void ProcessSingBoxLogLine(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        var line = AnsiEscapeRegex.Replace(data, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        RaiseLog($"sing-box: {line}");
        if (LooksLikeDnsLogLine(line))
        {
            RaiseDnsLog($"sing-box: {line}");
        }

        TrackWebTunnelBridgeHealthFromSingBoxLine(line);

        lock (_singBoxLogLock)
        {
            _singBoxRecentLines.Enqueue(line);
            while (_singBoxRecentLines.Count > 40)
            {
                _singBoxRecentLines.Dequeue();
            }
        }

        if (line.Contains("socks5: request rejected", StringComparison.OrdinalIgnoreCase))
        {
            var destMatch = SingBoxConnectionToRegex.Match(line);
            var dest = destMatch.Success ? destMatch.Groups["dest"].Value : "a destination";
            var now = DateTime.UtcNow;
            if (now - _lastVpnMessageUtc >= TimeSpan.FromSeconds(10))
            {
                _lastVpnMessageUtc = now;
                _statusMessage = $"VPN tunnel: Tor rejected a connection to {dest}. Non-web ports are often blocked by Tor exits.";
                PublishStatus();
            }
        }
    }

    private void OnSingBoxExited(object? sender, EventArgs e)
    {
        var exitCode = _vpnService.ExitCode ?? 0;
        RaiseLog($"sing-box exited with code {exitCode}.");

        if (_isConnected && _activeOptions is { } options && IsTunMode(options) && options.KillSwitchEnabled && !options.UseHybridRouting && !_isDisconnecting)
        {
            if (WindowsAdmin.IsAdministrator())
            {
                KillSwitchService.EnableEmergencyBlock(RaiseLog);
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    if (await _adminHelper.EnsureConnectedAsync().ConfigureAwait(false))
                    {
                        await _adminHelper.EnableKillSwitchAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        RaiseLog("Kill switch could not be enabled (admin helper unavailable).");
                    }
                });
            }
        }

        if (_isConnected && !_isDisconnecting)
        {
            string lastLines;
            lock (_singBoxLogLock)
            {
                lastLines = string.Join("\n", _singBoxRecentLines.Count > 6
                    ? _singBoxRecentLines.Skip(Math.Max(0, _singBoxRecentLines.Count - 6))
                    : _singBoxRecentLines);
            }

            _connectionStatus = "VPN stopped";
            _statusMessage = string.IsNullOrWhiteSpace(lastLines)
                ? $"VPN tunnel stopped unexpectedly (exit code {exitCode}). Disconnecting..."
                : $"VPN tunnel stopped unexpectedly (exit code {exitCode}). Last logs:\n{lastLines}";
            PublishStatus();

            _ = Task.Run(async () =>
            {
                try
                {
                    await DisconnectAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }
    }

    private void StopSingBoxProcess()
    {
        _adminVpnMonitorCts?.Cancel();
        _adminVpnMonitorCts = null;
        ClearRuntimeBridgeConnectionTracking();

        if (!WindowsAdmin.IsAdministrator())
        {
            _ = Task.Run(async () => await _adminHelper.StopVpnIfAvailableAsync().ConfigureAwait(false));
            lock (_singBoxLogLock)
            {
                _singBoxRecentLines.Clear();
            }
            return;
        }

        _vpnService.Stop();
        lock (_singBoxLogLock)
        {
            _singBoxRecentLines.Clear();
        }
    }

    private void StopTorProcess()
    {
        _torService.Stop();
        _lastNewnymUtc = DateTime.MinValue;
    }

    private static bool LooksLikeDnsLogLine(string line)
    {
        return line.Contains("doh", StringComparison.OrdinalIgnoreCase)
               || line.Contains("dns", StringComparison.OrdinalIgnoreCase)
               || line.Contains("hijack-dns", StringComparison.OrdinalIgnoreCase)
               || line.Contains("[dns]", StringComparison.OrdinalIgnoreCase)
               || line.Contains(" protocol=dns", StringComparison.OrdinalIgnoreCase)
               || line.Contains(" protocol dns", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFatalTorBootstrapLine(string line)
    {
        return line.Contains("no configured transport called", StringComparison.OrdinalIgnoreCase)
               || line.Contains("no such transport is supported", StringComparison.OrdinalIgnoreCase)
               || line.Contains("didn't launch any pluggable transport listeners", StringComparison.OrdinalIgnoreCase)
               || line.Contains("failed to bind", StringComparison.OrdinalIgnoreCase)
               || line.Contains("could not bind", StringComparison.OrdinalIgnoreCase)
               || line.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
               || line.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTorProxyHandshakeFailureLine(string line)
    {
        return line.Contains("handshaking (proxy)", StringComparison.OrdinalIgnoreCase)
               && line.Contains("general SOCKS server failure", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldLogTorLine(string line)
    {
        return line.Contains("Failed", StringComparison.OrdinalIgnoreCase)
               || line.Contains("[warn]", StringComparison.OrdinalIgnoreCase)
               || line.Contains("[err]", StringComparison.OrdinalIgnoreCase)
               || line.Contains("warn", StringComparison.OrdinalIgnoreCase)
               || line.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    private void ResetBridgeFailureTracking()
    {
        lock (_bridgeFailureLock)
        {
            _recentTorProxyFailures.Clear();
        }
    }

    private void RecordRecentTorProxyFailure()
    {
        lock (_bridgeFailureLock)
        {
            var now = DateTimeOffset.UtcNow;
            _recentTorProxyFailures.Enqueue(now);
            while (_recentTorProxyFailures.Count > 0 &&
                   now - _recentTorProxyFailures.Peek() > AutomaticBridgeProxyFailureWindow)
            {
                _recentTorProxyFailures.Dequeue();
            }
        }
    }

    private int CountRecentTorProxyFailures()
    {
        lock (_bridgeFailureLock)
        {
            var now = DateTimeOffset.UtcNow;
            while (_recentTorProxyFailures.Count > 0 &&
                   now - _recentTorProxyFailures.Peek() > AutomaticBridgeProxyFailureWindow)
            {
                _recentTorProxyFailures.Dequeue();
            }

            return _recentTorProxyFailures.Count;
        }
    }

    private void TrackWebTunnelBridgeHealthFromSingBoxLine(string line)
    {
        var id = TryExtractSingBoxConnectionId(line);
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (line.Contains("router: found process path:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("webtunnel-client.exe", StringComparison.OrdinalIgnoreCase))
        {
            lock (_bridgeFailureLock)
            {
                _webTunnelConnectionIds.Add(id);
                if (_webTunnelConnectionIds.Count > 4096)
                {
                    _webTunnelConnectionIds.Clear();
                    _webTunnelConnectionDestinations.Clear();
                }
            }

            return;
        }

        if (line.Contains("outbound/direct", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("outbound connection to ", StringComparison.OrdinalIgnoreCase))
        {
            var destinationMatch = SingBoxDirectOutboundDestRegex.Match(line);
            if (destinationMatch.Success)
            {
                lock (_bridgeFailureLock)
                {
                    if (_webTunnelConnectionIds.Contains(id))
                    {
                        _webTunnelConnectionDestinations[id] = destinationMatch.Groups["dest"].Value;
                    }
                }
            }

            return;
        }

        if (!line.Contains("connection download closed", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? destinationFromMap;
        bool wasTrackedWebTunnelConnection;
        lock (_bridgeFailureLock)
        {
            wasTrackedWebTunnelConnection = _webTunnelConnectionIds.Contains(id) || _webTunnelConnectionDestinations.ContainsKey(id);
            _webTunnelConnectionDestinations.TryGetValue(id, out destinationFromMap);
            _webTunnelConnectionDestinations.Remove(id);
            _webTunnelConnectionIds.Remove(id);
        }

        if (!wasTrackedWebTunnelConnection)
        {
            return;
        }

        var destinationMatchFromLine = SingBoxClosedConnectionDestRegex.Match(line);
        var destination = destinationMatchFromLine.Success
            ? destinationMatchFromLine.Groups["dest"].Value
            : destinationFromMap;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        var activeBridgeType = GetActiveBridgeTypeForRuntimeHealth();
        if (string.IsNullOrWhiteSpace(activeBridgeType) ||
            !string.Equals(activeBridgeType, "webtunnel", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _bridgeManager.ReportRuntimeBridgeFailure(activeBridgeType, destination, RaiseLog);
    }

    private string? GetActiveBridgeTypeForRuntimeHealth()
    {
        var options = _activeOptions;
        if (options is null || !options.UseTorBridges)
        {
            return null;
        }

        return options.SelectedBridgeType?.Trim();
    }

    private void ClearRuntimeBridgeConnectionTracking()
    {
        lock (_bridgeFailureLock)
        {
            _webTunnelConnectionIds.Clear();
            _webTunnelConnectionDestinations.Clear();
        }
    }

    private static string? TryExtractSingBoxConnectionId(string line)
    {
        var match = SingBoxConnectionIdRegex.Match(line);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static int ExtractProgress(string line)
    {
        var percentIndex = line.IndexOf('%');
        if (percentIndex <= 0)
        {
            return 0;
        }

        var start = percentIndex - 1;
        while (start >= 0 && char.IsDigit(line[start]))
        {
            start--;
        }

        var number = line.Substring(start + 1, percentIndex - start - 1);
        return int.TryParse(number, out var value) ? value : 0;
    }

    private static string? ExtractBootstrapSummary(string line)
    {
        // Example: "Bootstrapped 25% (requesting_status): Asking for networkstatus consensus"
        var colonIndex = line.IndexOf("):", StringComparison.Ordinal);
        if (colonIndex < 0 || colonIndex + 2 >= line.Length)
        {
            return null;
        }

        var summary = line[(colonIndex + 2)..].Trim();
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    private static IReadOnlyList<int> ParseAllowedPorts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [80, 443];
        }

        var result = new List<int>();
        foreach (var token in raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var port) && port is >= 1 and <= 65535 && !result.Contains(port))
            {
                result.Add(port);
            }
        }

        return result.Count == 0 ? [80, 443] : result;
    }

    private static int NormalizePreferredProxyPort(int preferredPort, int fallbackPort)
    {
        return preferredPort is >= 1 and <= 65535
            ? preferredPort
            : fallbackPort;
    }

    private static bool UsesSystemProxyScope(OnionHopConnectOptions options)
    {
        return !string.Equals(
            options.ProxyScopeMode,
            OnionHopConnectOptions.ProxyScopeLocalOnly,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildManualProxyHint(int socksPort, int? httpPort)
    {
        if (httpPort.HasValue)
        {
            return $"Local proxy mode: configure apps manually (SOCKS 127.0.0.1:{socksPort}, HTTP 127.0.0.1:{httpPort.Value}).";
        }

        return $"Local proxy mode: configure apps manually (SOCKS 127.0.0.1:{socksPort}).";
    }

    private static (string Address, int Port)? SelectOnionDnsEndpoint(out IReadOnlyList<string> attemptedCandidates)
    {
        var attempted = new List<string>();
        var candidates = BuildOnionDnsLoopbackCandidates();
        foreach (var candidate in candidates)
        {
            if (!IPAddress.TryParse(candidate, out var address))
            {
                continue;
            }

            attempted.Add(candidate);
            if (PortSelector.IsTcpAndUdpEndpointAvailable(address, DefaultDnsPort))
            {
                attemptedCandidates = attempted;
                return (candidate, DefaultDnsPort);
            }
        }

        attemptedCandidates = attempted;
        return null;
    }

    private static IReadOnlyList<string> BuildOnionDnsLoopbackCandidates()
    {
        var candidates = new List<string>
        {
            "127.0.0.1",
            "127.0.0.2",
            "127.0.0.53",
            "127.0.0.54",
            "127.0.0.100",
            "127.0.1.1",
            "::1"
        };

        for (var suffix = 3; suffix <= 32; suffix++)
        {
            candidates.Add($"127.0.0.{suffix}");
        }

        return candidates
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> LimitBridgeLinesForLaunch(IReadOnlyList<string> bridgeLines, Action<string> log)
    {
        if (bridgeLines.Count == 0)
        {
            return bridgeLines;
        }

        var selected = new List<string>(Math.Min(MaxBridgeLinesForLaunch, bridgeLines.Count));
        var totalChars = 0;

        foreach (var line in bridgeLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            var estimatedArgChars = trimmed.Length + 12; // --Bridge "..."
            if (selected.Count >= MaxBridgeLinesForLaunch || totalChars + estimatedArgChars > MaxBridgeArgumentCharsForLaunch)
            {
                break;
            }

            selected.Add(trimmed);
            totalChars += estimatedArgChars;
        }

        if (selected.Count == 0)
        {
            selected.Add(bridgeLines[0].Trim());
        }

        if (selected.Count < bridgeLines.Count)
        {
            log($"Using {selected.Count} of {bridgeLines.Count} bridge lines to avoid Windows command-line length limits.");
        }

        return selected;
    }
}
