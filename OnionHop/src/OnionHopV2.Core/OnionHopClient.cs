using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV2.Core.Dependencies;
using OnionHopV2.Core.Models;
using OnionHopV2.Core.Networking;
using OnionHopV2.Core.Platform;
using OnionHopV2.Core.Platform.MacOS;
using OnionHopV2.Core.Platform.Windows;
using OnionHopV2.Core.Services;
using OnionHopV2.Core.Tor;

namespace OnionHopV2.Core;

public sealed class OnionHopClient : IDisposable
{
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
    public readonly record struct BridgeDbRefreshStatus(
        bool UsedTorProxy,
        int AttemptedTypes,
        int UpdatedTypes,
        DateTimeOffset? LastUpdatedUtc);

    public event EventHandler<string>? Log;
    public event EventHandler<string>? DnsLog;
    public event EventHandler<string>? VpnLog;
    public event EventHandler<StatusUpdate>? StatusUpdated;
    public event EventHandler<DependencyUpdate>? DependencyUpdated;

    private readonly string _baseDir;
    private readonly DependencyManager _deps = new();
    private readonly TorBridgeManager _bridgeManager;
    private readonly IProxyService _proxyService = PlatformHelper.CreateProxyService();
    private readonly IDnsProxyService _onionDnsProxyService = PlatformHelper.CreateDnsProxyService();
    private readonly IKillSwitchService _killSwitchService = PlatformHelper.CreateKillSwitchService();
    private readonly HttpProxyBridgeService _httpProxyBridgeService;
    private readonly TorNodeDatabaseService _nodeDatabaseService = new();
    private readonly SingBoxLogProcessor _singBoxLogProcessor = new();

    private readonly TorService _torService;
    private readonly VpnService _vpnService;
    private readonly AdminHelperClient _adminHelper = new();

    private Task<bool>? _torDependencyEnsureTask;
    private Task<bool>? _fullDependencyEnsureTask;
    private readonly object _dependencyEnsureLock = new();
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
    private string _activeProxyBindAddress = "127.0.0.1";
    private int? _activeDnsPort;
    private string? _activeDnsBindAddress;
    private string _activeVpnCoreMode = OnionHopConnectOptions.TunCoreSingBox;
    private bool _macNetworkExtensionActive;
    private VpnLaunchConfig? _preparedMacVpnLaunchConfig;

    private CancellationTokenSource? _adminVpnMonitorCts;
    private readonly object _bridgeFailureLock = new();
    private readonly Queue<DateTimeOffset> _recentTorProxyFailures = new();

    public OnionHopClient(string? baseDirectory = null)
    {
        _baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? ResolveDefaultBaseDirectory() : baseDirectory!;
        _bridgeManager = new TorBridgeManager(_baseDir);

        _torService = new TorService(RaiseLog);
        _vpnService = new VpnService(RaiseLog);
        _httpProxyBridgeService = new HttpProxyBridgeService(RaiseLog);

        _torService.OutputReceived += OnTorDataReceived;
        _torService.Exited += OnTorExited;

        _vpnService.OutputLineReceived += OnVpnOutputLine;
        _vpnService.Exited += OnSingBoxExited;

        _singBoxLogProcessor.SetSourceLabel(_activeVpnCoreMode);
        _singBoxLogProcessor.LogReceived += RaiseLog;
        _singBoxLogProcessor.LogReceived += RaiseVpnLog;
        _singBoxLogProcessor.DnsLogReceived += RaiseDnsLog;
        _singBoxLogProcessor.StatusMessageChanged += message =>
        {
            _statusMessage = message;
            PublishStatus();
        };
    }

    private static string ResolveDefaultBaseDirectory()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var runtimeDir = Path.Combine(localAppData, "OnionHop");
                Directory.CreateDirectory(runtimeDir);
                return runtimeDir;
            }
        }
        catch
        {
        }

        return AppContext.BaseDirectory;
    }

    public string BaseDirectory => _baseDir;

    private void EnsureGeoIpFile(string targetPath, string fileName)
    {
        if (File.Exists(targetPath))
        {
            return;
        }

        // Look for the file next to the application binary (app bundle).
        var bundlePath = Path.Combine(AppContext.BaseDirectory, fileName);
        RaiseLog($"GeoIP check: target={targetPath} exists={File.Exists(targetPath)}, bundle={bundlePath} exists={File.Exists(bundlePath)}, BaseDir={AppContext.BaseDirectory}");

        // Also try the executable's actual directory (for macOS .app bundles)
        var exePath = Environment.ProcessPath;
        var exeDir = exePath != null ? Path.GetDirectoryName(exePath) : null;
        if (exeDir != null && !string.Equals(exeDir, AppContext.BaseDirectory, StringComparison.Ordinal))
        {
            var exeDirPath = Path.Combine(exeDir, fileName);
            RaiseLog($"GeoIP check (exe dir): {exeDirPath} exists={File.Exists(exeDirPath)}");
            if (File.Exists(exeDirPath))
            {
                bundlePath = exeDirPath;
            }
        }

        if (File.Exists(bundlePath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(bundlePath, targetPath, true);
                RaiseLog($"Copied {fileName} from app bundle to {targetPath}");
            }
            catch (Exception ex)
            {
                RaiseLog($"Warning: Failed to copy {fileName}: {ex.Message}");
            }
        }
        else
        {
            RaiseLog($"Warning: {fileName} not found at {targetPath} or {bundlePath}. Exit country selection will not work.");
        }
    }

    public IReadOnlyList<string> GetBridgeTypes()
    {
        return TorBridgeManager.GetBridgeTypeKeys(_ptConfig);
    }

    public string? GetRecommendedBridgeType()
    {
        return _ptConfig?.RecommendedDefault;
    }

    public DateTimeOffset? GetLastBridgeDbUpdateUtc()
    {
        return _bridgeManager.GetLatestBridgeCacheUpdateUtc();
    }

    public bool CanUseMacNetworkExtension()
    {
        return MacNetworkExtensionService.IsConfigured();
    }

    public async Task<BridgeDbRefreshStatus> RefreshBridgeDatabaseAsync(OnionHopConnectOptions options, CancellationToken token = default)
    {
        if (!await EnsureTorDependenciesAsync(token).ConfigureAwait(false))
        {
            RaiseLog("BridgeDB refresh skipped: dependencies are not available.");
            return new BridgeDbRefreshStatus(
                UsedTorProxy: false,
                AttemptedTypes: 0,
                UpdatedTypes: 0,
                LastUpdatedUtc: _bridgeManager.GetLatestBridgeCacheUpdateUtc());
        }

        var bridgeTypes = ResolveBridgeDbRefreshTypes(options);
        if (bridgeTypes.Count == 0)
        {
            RaiseLog("BridgeDB refresh skipped: no eligible bridge types were found.");
            return new BridgeDbRefreshStatus(
                UsedTorProxy: false,
                AttemptedTypes: 0,
                UpdatedTypes: 0,
                LastUpdatedUtc: _bridgeManager.GetLatestBridgeCacheUpdateUtc());
        }

        var useTorProxy = _isConnected && _torService.IsRunning && _activeSocksPort > 0;
        HttpClient? httpClient = null;
        try
        {
            if (useTorProxy)
            {
                httpClient = Socks5HttpClient.Create("127.0.0.1", _activeSocksPort, TimeSpan.FromSeconds(35));
                RaiseLog($"BridgeDB refresh: routing requests through Tor SOCKS 127.0.0.1:{_activeSocksPort}.");
            }
            else
            {
                RaiseLog("BridgeDB refresh: Tor is not active, using direct network access.");
            }

            var summary = await _bridgeManager
                .RefreshBridgeDbAsync(bridgeTypes, RaiseLog, token, httpClient)
                .ConfigureAwait(false);

            if (summary.UpdatedTypes > 0)
            {
                RaiseLog($"BridgeDB refresh complete: {summary.UpdatedTypes}/{summary.AttemptedTypes} bridge type(s) updated.");
            }
            else
            {
                RaiseLog("BridgeDB refresh completed, but no new usable bridge lines were fetched.");
            }

            return new BridgeDbRefreshStatus(
                UsedTorProxy: useTorProxy,
                AttemptedTypes: summary.AttemptedTypes,
                UpdatedTypes: summary.UpdatedTypes,
                LastUpdatedUtc: summary.LastUpdatedUtc);
        }
        finally
        {
            httpClient?.Dispose();
        }
    }

    public async Task<IReadOnlyList<TorCountryNodeStats>> GetCountryStatsAsync(CancellationToken token = default)
    {
        return await _nodeDatabaseService.GetCountryStatsAsync(RaiseLog, token).ConfigureAwait(false);
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

    public Task<bool> EnsureDependenciesAsync(CancellationToken token = default)
        => EnsureDependenciesAsync(requireVpnDependencies: true, token);

    public Task<bool> EnsureTorDependenciesAsync(CancellationToken token = default)
        => EnsureDependenciesAsync(requireVpnDependencies: false, token);

    private async Task<bool> EnsureDependenciesAsync(bool requireVpnDependencies, CancellationToken token)
    {
        var task = GetOrCreateDependencyEnsureTask(requireVpnDependencies, token);
        try
        {
            var success = await task.ConfigureAwait(false);
            if (!success)
            {
                ClearDependencyEnsureTask(task);
                return false;
            }

            if (requireVpnDependencies)
            {
                PromoteTorDependencyTask(task);
            }

            return true;
        }
        catch
        {
            ClearDependencyEnsureTask(task);
            throw;
        }
    }

    private Task<bool> GetOrCreateDependencyEnsureTask(bool requireVpnDependencies, CancellationToken token)
    {
        lock (_dependencyEnsureLock)
        {
            // A full dependency check also satisfies tor-only requirements.
            if (!requireVpnDependencies && _fullDependencyEnsureTask != null)
            {
                return _fullDependencyEnsureTask;
            }

            var cached = requireVpnDependencies ? _fullDependencyEnsureTask : _torDependencyEnsureTask;
            if (cached != null)
            {
                return cached;
            }

            var created = EnsureDependenciesCoreAsync(requireVpnDependencies, token);
            if (requireVpnDependencies)
            {
                _fullDependencyEnsureTask = created;
            }
            else
            {
                _torDependencyEnsureTask = created;
            }

            return created;
        }
    }

    private void PromoteTorDependencyTask(Task<bool> task)
    {
        lock (_dependencyEnsureLock)
        {
            if (ReferenceEquals(_fullDependencyEnsureTask, task))
            {
                _torDependencyEnsureTask = task;
            }
        }
    }

    private void ClearDependencyEnsureTask(Task<bool> task)
    {
        lock (_dependencyEnsureLock)
        {
            if (ReferenceEquals(_torDependencyEnsureTask, task))
            {
                _torDependencyEnsureTask = null;
            }

            if (ReferenceEquals(_fullDependencyEnsureTask, task))
            {
                _fullDependencyEnsureTask = null;
            }
        }
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

        ClearPreparedMacVpnLaunchConfig();

        StartupLogger.Write("OnionHopClient.ConnectAsync: Checking internet connectivity...");
        var connectivity = await InternetConnectivityProbe.CheckAsync(token).ConfigureAwait(false);
        if (connectivity.State == InternetConnectivityState.Offline)
        {
            RaiseLog($"Connect blocked: {connectivity.Reason}");
            SetStatus(
                isConnecting: false,
                isConnected: false,
                isDisconnecting: false,
                connectionStatus: "Disconnected",
                statusMessage: "No internet connection detected. Check your network and try again.",
                progress: 0);
            return;
        }

        if (connectivity.State == InternetConnectivityState.Unknown)
        {
            RaiseLog($"Internet check warning: {connectivity.Reason} Continuing connection attempt.");
        }

        StartupLogger.Write("OnionHopClient.ConnectAsync: Checking dependencies...");
        var requiresVpnDependencies = IsTunMode(options);
        var dependenciesReady = requiresVpnDependencies
            ? await EnsureDependenciesAsync(token).ConfigureAwait(false)
            : await EnsureTorDependenciesAsync(token).ConfigureAwait(false);
        if (!dependenciesReady)
        {
            StartupLogger.Write("OnionHopClient.ConnectAsync: Dependencies check failed!");
            var dependencyStatus = string.IsNullOrWhiteSpace(_dependencyDownloadStatus)
                ? "Failed to verify or download required components."
                : _dependencyDownloadStatus;
            SetStatus(
                isConnecting: false,
                isConnected: false,
                isDisconnecting: false,
                connectionStatus: "Disconnected",
                statusMessage: dependencyStatus,
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
        _activeProxyBindAddress = options.AllowLanProxyAccess ? "0.0.0.0" : "127.0.0.1";
        if (options.AllowLanProxyAccess)
        {
            RaiseLog("LAN proxy access enabled: SOCKS/HTTP listeners will accept connections from local network interfaces.");
        }

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

        var automaticConnectTimeout = options.UseTorBridges
            ? TorBridgeManager.IsAutomaticBridgeType(options.SelectedBridgeType)
                ? TimeSpan.FromSeconds(360)
                : TimeSpan.FromSeconds(240)
            : TimeSpan.FromSeconds(60);

        var connectTimeout = ResolveConnectTimeout(options.ConnectionTimeoutSeconds, automaticConnectTimeout);
        if (connectTimeout.HasValue)
        {
            RaiseLog($"Connection timeout: {(int)connectTimeout.Value.TotalSeconds}s.");
        }
        else
        {
            RaiseLog("Connection timeout disabled: waiting until bootstrap succeeds or user cancels.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (connectTimeout.HasValue)
        {
            timeoutCts.CancelAfter(connectTimeout.Value);
        }

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
            var xrayTunCompatibilityProxyApplied = false;
            RaiseLog($"Connecting. Mode={options.SelectedConnectionMode}, Hybrid={options.UseHybridRouting}, Exit={options.SelectedLocation}, Bridges={(options.UseTorBridges ? options.SelectedBridgeType : "off")}");

            var resolvedOptions = await StartTorWithBridgeFallbackAsync(options, timeoutCts.Token).ConfigureAwait(false);
            _activeOptions = resolvedOptions;

            if (resolvedOptions.OnionDnsProxyEnabled)
            {
                if (ShouldManageOnionDnsInsideMacTun(resolvedOptions))
                {
                    RaiseLog(".onion DNS proxying will be managed by the macOS tunnel session.");
                }
                else if (_activeDnsPort == DefaultDnsPort && !string.IsNullOrWhiteSpace(_activeDnsBindAddress))
                {
                    if (OperatingSystem.IsWindows() && !PlatformHelper.IsAdministrator())
                    {
                        var enabled = await _adminHelper.EnableOnionDnsProxyAsync(_activeDnsBindAddress!).ConfigureAwait(false);
                        if (!enabled)
                        {
                            throw new InvalidOperationException(".onion DNS proxying could not be enabled by the privileged helper.");
                        }

                        RaiseLog($".onion DNS proxying enabled (helper-managed, nameserver={_activeDnsBindAddress}).");
                    }
                    else
                    {
                        _onionDnsProxyService.Enable(_activeDnsBindAddress!, RaiseLog);
                    }
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

            if (_activeHttpPort.HasValue)
            {
                await StartHttpProxyBridgeAsync(timeoutCts.Token).ConfigureAwait(false);
            }

            if (IsTunMode(resolvedOptions) &&
                string.Equals(_activeVpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal))
            {
                _proxyService.ApplyTorProxy(_activeSocksPort, _activeHttpPort, RaiseLog);
                xrayTunCompatibilityProxyApplied = true;
                RaiseLog(_activeHttpPort.HasValue
                    ? "xray compatibility mode: system HTTP/SOCKS proxy fallback enabled to ensure browser traffic uses Tor."
                    : "xray compatibility mode: system SOCKS proxy fallback enabled to ensure browser traffic uses Tor.");
            }

            if (!IsTunMode(resolvedOptions))
            {
                if (UsesSystemProxyScope(resolvedOptions))
                {
                    var systemHttpPort = UsesSocksOnlySystemProxyScope(resolvedOptions) ? null : _activeHttpPort;
                    _proxyService.ApplyTorProxy(_activeSocksPort, systemHttpPort, RaiseLog);
                    if (systemHttpPort is null)
                    {
                        RaiseLog($"System proxy SOCKS browser/.onion mode enabled (socks={_activeProxyBindAddress}:{_activeSocksPort}).");
                    }
                }
                else
                {
                    RaiseLog(BuildManualProxyHint(_activeProxyBindAddress, _activeSocksPort, _activeHttpPort));
                }
            }

            _isConnected = true;
            _connectionStatus = "Connected";
            _connectionProgress = 1;
            _statusMessage = IsTunMode(resolvedOptions)
                ? (resolvedOptions.UseHybridRouting
                    ? "Tor is running. Hybrid routing is active (browser via Tor)."
                    : xrayTunCompatibilityProxyApplied
                        ? "Tor is running. xray compatibility proxy is active (browser traffic via Tor)."
                    : "Tor is running. VPN tunnel is active (all traffic via Tor).")
                : UsesSystemProxyScope(resolvedOptions)
                    ? "Tor is running. System proxy mode is active."
                    : "Tor is running. Local proxy mode is active (configure apps manually).";
            PublishStatus();

            await RefreshIpAsync(updateStatusMessage: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var canceledByUser = token.IsCancellationRequested;
            var canceledMessage = canceledByUser || !connectTimeout.HasValue
                ? "Connection canceled."
                : "Connection canceled or timed out.";
            RaiseLog(canceledMessage);
            await DisconnectCoreAsync(disableStatusUpdate: true).ConfigureAwait(false);
            SetStatus(
                isConnecting: false,
                isConnected: false,
                isDisconnecting: false,
                connectionStatus: "Disconnected",
                statusMessage: canceledMessage,
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
        RaiseLog($"IP check: torFirst={torFirst}, isConnected={_isConnected}, torRunning={_torService.IsRunning}, socksPort={_activeSocksPort}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(18));

        try
        {
            string? ip = null;

            if (torFirst)
            {
                ip = await IpLookupService.TryFetchTorExitIpAsync(_activeSocksPort, RaiseLog, cts.Token).ConfigureAwait(false);
                RaiseLog($"IP check via SOCKS: result='{ip}'");
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

                if (!IPAddress.TryParse(_currentIp?.Trim(), out _))
                {
                    _currentIp = "--.--.--.--";
                }
                if (updateStatusMessage)
                {
                    _statusMessage = "Unable to refresh Tor exit IP right now.";
                }
                PublishStatus();
                return;
            }

            ip = await IpLookupService.TryFetchDirectIpAsync(RaiseLog, cts.Token).ConfigureAwait(false);
            RaiseLog($"IP check via DIRECT (not through Tor): result='{ip}'");
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

    public async Task ChangeExitCountryAsync(string? countryCode, CancellationToken token)
    {
        if (!_isConnected || !_torService.IsRunning)
        {
            return;
        }

        var exitNodesValue = string.IsNullOrWhiteSpace(countryCode)
            ? ""
            : $"{{{countryCode}}}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        var strictCmd = string.IsNullOrWhiteSpace(countryCode)
            ? "SETCONF StrictNodes=0"
            : "SETCONF StrictNodes=1";

        await _torService.SendControlSignalAsync($"SETCONF ExitNodes=\"{exitNodesValue}\"", cts.Token).ConfigureAwait(false);
        await _torService.SendControlSignalAsync(strictCmd, cts.Token).ConfigureAwait(false);
        await _torService.SendControlSignalAsync("SIGNAL NEWNYM", cts.Token).ConfigureAwait(false);

        RaiseLog(string.IsNullOrWhiteSpace(countryCode)
            ? "Exit country cleared (Automatic). Requesting new circuit..."
            : $"Exit country changed to {{{countryCode}}}. Requesting new circuit...");

        _lastNewnymUtc = DateTime.UtcNow;
        await Task.Delay(1500, token).ConfigureAwait(false);
        await RefreshIpAsync(updateStatusMessage: true, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _adminVpnMonitorCts?.Cancel();
            _adminVpnMonitorCts = null;
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Dispose: failed to cancel VPN monitor: {ex.Message}");
        }

        try
        {
            if (_proxyService.IsApplied)
            {
                _proxyService.RestorePreviousProxy(RaiseLog);
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Dispose: failed to restore proxy: {ex.Message}");
        }

        try
        {
            if (OperatingSystem.IsWindows() && !WindowsAdmin.IsAdministrator())
            {
                _ = Task.Run(async () => await _adminHelper.DisableOnionDnsProxyIfAvailableAsync().ConfigureAwait(false));
            }
            else
            {
                _onionDnsProxyService.Disable(RaiseLog);
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Dispose: failed to disable DNS proxy: {ex.Message}");
        }

        try
        {
            _httpProxyBridgeService.Stop();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Dispose: failed to stop HTTP proxy bridge: {ex.Message}");
        }

        try
        {
            StopSingBoxProcess();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Dispose: failed to stop VPN core: {ex.Message}");
        }

        try
        {
            StopTorProcess();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Dispose: failed to stop Tor: {ex.Message}");
        }

        try
        {
            if (_adminHelper.IsConnected)
            {
                _adminHelper.ShutdownIfConnectedAsync().Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Dispose: failed to shutdown admin helper: {ex.Message}");
        }

        _adminHelper.Dispose();
        _vpnService.Dispose();
        _torService.Dispose();
    }

    private async Task<bool> EnsureDependenciesCoreAsync(bool requireVpnDependencies, CancellationToken token)
    {
        StartupLogger.Write("EnsureDependenciesCoreAsync: Starting dependency check...");
        
        void Progress(DependencyManager.DependencyUpdate update)
        {
            _dependencyDownloadInProgress = update.InProgress;
            _dependencyDownloadStatus = update.Status;
            _dependencyDownloadProgress = update.Progress;
            PublishDependency();
        }

        var success = await _deps.EnsureAsync(_baseDir, requireVpnDependencies, Progress, RaiseLog, token).ConfigureAwait(false);
        StartupLogger.Write($"EnsureDependenciesCoreAsync: _deps.EnsureAsync returned {success}");
        if (!success)
        {
            return false;
        }

        FixBaseDirectoryPermissions();

        _ptConfig = DependencyManager.TryLoadPluggableTransportConfig(_baseDir, RaiseLog);
        return true;
    }

    private void FixBaseDirectoryPermissions()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || geteuid() == 0)
        {
            return;
        }

        // Check if any subdirectories are inaccessible (root-owned from a previous TUN session).
        var dirsToFix = new List<string>();
        foreach (var subDir in new[] { "tor", "vpn", "tor-data" })
        {
            var dirPath = Path.Combine(_baseDir, subDir);
            if (!Directory.Exists(dirPath))
            {
                continue;
            }

            var testFile = Path.Combine(dirPath, ".write-test");
            try
            {
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch
            {
                dirsToFix.Add(dirPath);
            }
        }

        if (dirsToFix.Count == 0)
        {
            return;
        }

        RaiseLog($"Detected {dirsToFix.Count} directory(ies) with wrong ownership. Requesting admin privileges to fix...");

        // Use macOS osascript to prompt for admin password and fix permissions.
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var uid = geteuid();
                var gid = getegid();
                var ownerSpec = $"{uid}:{gid}";
                var dirs = string.Join(" ", dirsToFix.Select(MacAuthorization.QuoteShellArgument));
                var script = $"""
#!/bin/sh
set -eu
/usr/sbin/chown -R {MacAuthorization.QuoteShellArgument(ownerSpec)} {dirs}
/bin/chmod -R u+rwX {dirs}
""";

                var result = MacAuthorization.RunScript(script, requireAdministrator: true, timeoutMs: 30_000);
                if (result.Success)
                {
                    RaiseLog("Successfully fixed directory permissions.");
                }
                else
                {
                    RaiseLog($"Permission fix was declined or failed: {result.FailureMessage}");
                }
            }
            catch (Exception ex)
            {
                RaiseLog($"Failed to request permission fix: {ex.Message}");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            // On Linux, use pkexec without shell interpolation to avoid command injection.
            try
            {
                var uid = geteuid();
                var gid = getegid();
                if (TryRunPkexecOwnershipFix(uid, gid, dirsToFix, TimeSpan.FromSeconds(30), out var error))
                {
                    RaiseLog("Successfully fixed directory permissions.");
                }
                else
                {
                    RaiseLog(string.IsNullOrWhiteSpace(error)
                        ? "Permission fix was declined or failed."
                        : $"Permission fix was declined or failed: {error}");
                }
            }
            catch (Exception ex)
            {
                RaiseLog($"Failed to request permission fix: {ex.Message}");
            }
        }
    }

    private static bool TryRunPkexecOwnershipFix(
        uint uid,
        uint gid,
        IReadOnlyList<string> dirsToFix,
        TimeSpan timeout,
        out string? error)
    {
        error = null;
        if (dirsToFix.Count == 0)
        {
            return true;
        }

        var timeoutMs = (int)Math.Max(1, timeout.TotalMilliseconds);

        var chownArgs = new List<string> { "-R", $"{uid}:{gid}" };
        chownArgs.AddRange(dirsToFix);
        if (!TryRunPkexecCommand("chown", chownArgs, timeoutMs, out error))
        {
            return false;
        }

        var chmodArgs = new List<string> { "-R", "u+rwX" };
        chmodArgs.AddRange(dirsToFix);
        return TryRunPkexecCommand("chmod", chmodArgs, timeoutMs, out error);
    }

    private static bool TryRunPkexecCommand(
        string executable,
        IReadOnlyList<string> arguments,
        int timeoutMs,
        out string? error)
    {
        error = null;
        var psi = new ProcessStartInfo("pkexec")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(executable);
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            error = "Failed to start pkexec.";
            return false;
        }

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            error = "pkexec command timed out.";
            return false;
        }

        var stdErr = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0)
        {
            error = string.IsNullOrWhiteSpace(stdErr)
                ? $"{executable} exited with code {process.ExitCode}."
                : stdErr;
            return false;
        }

        return true;
    }

    private void FixBaseDirectoryOwnershipForNonRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        if (geteuid() != 0)
        {
            return; // Only makes sense when running as root.
        }

        // Determine the real user's uid/gid so we can restore ownership.
        // Avoid P/Invoke stat() — the struct layout varies across OS/arch and causes crashes.
        uint targetUid;
        uint targetGid;
        try
        {
            // Try stat via shell command — safe across all platforms.
            if (OperatingSystem.IsMacOS())
            {
                // macOS stat: -f "%u %g" prints uid and gid
                var output = PlatformHelper.RunCommand("stat", $"-f \"%u %g\" \"{_baseDir}\"");
                var parts = output?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts?.Length >= 2 && uint.TryParse(parts[0], out targetUid) && uint.TryParse(parts[1], out targetGid))
                {
                    // Successfully parsed uid/gid from stat
                }
                else
                {
                    targetUid = 0;
                    targetGid = 0;
                }
            }
            else
            {
                // Linux stat: -c "%u %g" prints uid and gid
                var output = PlatformHelper.RunCommand("stat", $"-c \"%u %g\" \"{_baseDir}\"");
                var parts = output?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts?.Length >= 2 && uint.TryParse(parts[0], out targetUid) && uint.TryParse(parts[1], out targetGid))
                {
                    // Successfully parsed uid/gid from stat
                }
                else
                {
                    targetUid = 0;
                    targetGid = 0;
                }
            }

            // Fallback: use SUDO_UID/SUDO_GID environment variables
            if (targetUid == 0)
            {
                var sudoUid = Environment.GetEnvironmentVariable("SUDO_UID");
                var sudoGid = Environment.GetEnvironmentVariable("SUDO_GID");
                if (sudoUid == null || !uint.TryParse(sudoUid, out targetUid))
                {
                    return;
                }
                targetGid = sudoGid != null && uint.TryParse(sudoGid, out var gid) ? gid : targetUid;
            }
        }
        catch
        {
            return;
        }

        if (targetUid == 0)
        {
            return; // Base dir is also root-owned; no user to restore to.
        }

        RaiseLog($"Fixing ownership of base directory subdirectories to uid={targetUid} gid={targetGid}");

        foreach (var subDir in new[] { "tor", "vpn", "tor-data" })
        {
            var dirPath = Path.Combine(_baseDir, subDir);
            if (!Directory.Exists(dirPath))
            {
                continue;
            }

            try
            {
                RecursiveChmodAndChown(dirPath, targetUid, targetGid);
            }
            catch (Exception ex)
            {
                RaiseLog($"Warning: could not fix permissions on '{dirPath}': {ex.Message}");
            }
        }
    }

    private static void RecursiveChmodAndChown(string path, uint uid, uint gid)
    {
        chmod(path, 0b111_101_101); // 0755
        chown(path, uid, gid);

        foreach (var file in Directory.GetFiles(path))
        {
            var isExecutable = !Path.HasExtension(file)
                || file.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".so", StringComparison.OrdinalIgnoreCase);
            chmod(file, isExecutable ? 0b111_101_101u : 0b110_100_100u); // 0755 or 0644
            chown(file, uid, gid);
        }

        foreach (var dir in Directory.GetDirectories(path))
        {
            RecursiveChmodAndChown(dir, uid, gid);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string pathname, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string pathname, uint owner, uint group);

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc")]
    private static extern uint getegid();

    private async Task DisconnectCoreAsync(bool disableStatusUpdate)
    {
        try
        {
            _httpProxyBridgeService.Stop();
            StopSingBoxProcess();

            if (_killSwitchService.IsEmergencyBlockActive())
            {
                if (PlatformHelper.IsAdministrator())
                {
                    _killSwitchService.DisableEmergencyBlock(RaiseLog);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    _killSwitchService.DisableEmergencyBlock(RaiseLog);
                }
                else if (OperatingSystem.IsWindows())
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
                if (OperatingSystem.IsWindows() && !WindowsAdmin.IsAdministrator())
                {
                    _ = Task.Run(async () => await _adminHelper.DisableOnionDnsProxyIfAvailableAsync().ConfigureAwait(false));
                }
                else
                {
                    _onionDnsProxyService.Disable(RaiseLog);
                }
            }

            StopTorProcess();
            await Task.Delay(250).ConfigureAwait(false);

            // When disconnecting as root, fix ownership so the next non-root session
            // can access tor binaries, libraries, and data files.
            FixBaseDirectoryOwnershipForNonRoot();
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
            _activeVpnCoreMode = OnionHopConnectOptions.TunCoreSingBox;
            _macNetworkExtensionActive = false;
            ClearPreparedMacVpnLaunchConfig();
            _singBoxLogProcessor.SetSourceLabel(_activeVpnCoreMode);

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

    private void RaiseVpnLog(string message)
    {
        VpnLog?.Invoke(this, message);
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
        return options with { SelectedBridgeType = bridgeType };
    }

    private IReadOnlyList<string> ResolveBridgeDbRefreshTypes(OnionHopConnectOptions options)
    {
        var ordered = new List<string>();
        var availableTypes = new HashSet<string>(TorBridgeManager.GetBridgeTypeKeys(_ptConfig), StringComparer.OrdinalIgnoreCase);

        static bool IsAlwaysExcluded(string bridgeType)
        {
            return string.Equals(bridgeType, TorBridgeManager.AutomaticBridgeType, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(bridgeType, "custom", StringComparison.OrdinalIgnoreCase);
        }

        bool IsEligible(string bridgeType)
        {
            if (string.IsNullOrWhiteSpace(bridgeType) || IsAlwaysExcluded(bridgeType))
            {
                return false;
            }

            return availableTypes.Count == 0 || availableTypes.Contains(bridgeType);
        }

        var selectedBridgeType = options.SelectedBridgeType?.Trim();
        if (TorBridgeManager.IsAutomaticBridgeType(selectedBridgeType))
        {
            foreach (var bridgeType in TorBridgeManager.BuildAutomaticBridgeFallbackOrder(options))
            {
                if (IsEligible(bridgeType))
                {
                    ordered.Add(bridgeType);
                }
            }
        }
        else if (IsEligible(selectedBridgeType ?? string.Empty))
        {
            ordered.Add(selectedBridgeType!);
        }

        foreach (var fallbackType in new[] { "webtunnel", "snowflake", "obfs4" })
        {
            if (IsEligible(fallbackType))
            {
                ordered.Add(fallbackType);
            }
        }

        if (ordered.Count == 0)
        {
            ordered.AddRange(["webtunnel", "snowflake", "obfs4"]);
        }

        return ordered
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task StartTorAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        _bootstrapSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => _bootstrapSource.TrySetCanceled(token));

        var torDir = Path.Combine(_baseDir, "tor");
        var torPath = Path.Combine(torDir, PlatformHelper.TorBinaryName);
        var geoIpPath = Path.Combine(torDir, "geoip");
        var geoIp6Path = Path.Combine(torDir, "geoip6");

        StartupLogger.Write($"StartTorAsync: torPath={torPath}, exists={File.Exists(torPath)}, baseDir={_baseDir}");
        RaiseLog($"Paths: baseDir={_baseDir}, torPath={torPath} (exists={File.Exists(torPath)}), geoip exists={File.Exists(geoIpPath)}, geoip6 exists={File.Exists(geoIp6Path)}, AppBaseDir={AppContext.BaseDirectory}");

        // Ensure GeoIP files exist — copy from app bundle directory if missing.
        // Without these files Tor cannot map relays to countries, making ExitNodes useless.
        EnsureGeoIpFile(geoIpPath, "geoip");
        EnsureGeoIpFile(geoIp6Path, "geoip6");

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

        RaiseLog($"Exit selection: SelectedLocation='{options.SelectedLocation}', resolved countryCode='{countryCode}', countries fetched={countries.Count}");
        if (!string.IsNullOrWhiteSpace(entryCode))
        {
            RaiseLog($"Entry selection: SelectedEntryLocation='{options.SelectedEntryLocation}', resolved entryCode='{entryCode}'");
        }

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
            SocksListenAddress = _activeProxyBindAddress,
            HttpTunnelPort = null,
            HttpTunnelListenAddress = null,
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
            ConnectionPadding = ParseConnectionPaddingMode(options.ConnectionPaddingMode),
            DataDirectory = Path.Combine(_baseDir, "tor-data")
        };

        await _torService.StartAsync(config, token).ConfigureAwait(false);
        await _bootstrapSource.Task.ConfigureAwait(false);
    }

    private async Task StartHttpProxyBridgeAsync(CancellationToken token)
    {
        if (!_activeHttpPort.HasValue)
        {
            return;
        }

        try
        {
            await _httpProxyBridgeService.StartAsync(new HttpProxyBridgeConfig
            {
                ListenAddress = _activeProxyBindAddress,
                ListenPort = _activeHttpPort.Value,
                SocksProxyHost = "127.0.0.1",
                SocksProxyPort = _activeSocksPort
            }, token).ConfigureAwait(false);

            RaiseLog($"HTTP proxy bridge enabled on {_activeProxyBindAddress}:{_activeHttpPort.Value} (upstream SOCKS 127.0.0.1:{_activeSocksPort}).");
            RaiseLog("HTTP proxy note: HTTP/HTTPS traffic is proxied through SOCKS; ICMP ping is not supported.");
        }
        catch (Exception ex)
        {
            RaiseLog($"HTTP proxy bridge failed to start: {ex.Message}. Falling back to SOCKS-only.");
            _activeHttpPort = null;
        }
    }

    private async Task StartSingBoxVpnAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        StartupLogger.Write($"StartSingBoxVpnAsync: VPN mode, isAdmin={PlatformHelper.IsAdministrator()}");
        RaiseLog("StartSingBoxVpnAsync: Starting VPN setup...");
        if (!ShouldPrepareMacPrivilegedTunnel(options))
        {
            StopSingBoxProcess();
        }

        var tunCoreMode = NormalizeTunCoreMode(options.TunCoreMode);
        _activeVpnCoreMode = tunCoreMode;
        _singBoxLogProcessor.SetSourceLabel(tunCoreMode);
        if (string.Equals(tunCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal) &&
            (!string.Equals(options.TunStackMode, OnionHopConnectOptions.TunStackMixed, StringComparison.OrdinalIgnoreCase) || !options.TunStrictRoute))
        {
            RaiseLog("xray currently ignores TUN stack and strict-route tuning; using xray defaults.");
        }

        VpnLaunchConfig config;
        if (ShouldPrepareMacPrivilegedTunnel(options) && _preparedMacVpnLaunchConfig != null)
        {
            RaiseLog("StartSingBoxVpnAsync: Reusing prepared macOS VPN launch config.");
            config = _preparedMacVpnLaunchConfig;
        }
        else
        {
            config = await BuildVpnLaunchConfigAsync(options, token).ConfigureAwait(false);
        }

        // Verify Tor's SOCKS port is actually accepting connections before starting the VPN engine.
        // With bridges, Tor may report 100% bootstrap but need a moment for the SOCKS listener.
        if (!await WaitForSocksPortReadyAsync(_activeSocksPort, token).ConfigureAwait(false))
        {
            RaiseLog($"Warning: Tor SOCKS port {_activeSocksPort} not responding after bootstrap. Proceeding anyway.");
        }

        var selectedCorePath = string.Equals(tunCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? config.XrayPath
            : config.SingBoxPath;
        var isAdmin = PlatformHelper.IsAdministrator();
        RaiseLog($"StartSingBoxVpnAsync: IsAdmin={isAdmin}, Core={tunCoreMode}, CorePath={selectedCorePath}");

        if (OperatingSystem.IsWindows() && !isAdmin)
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

                var details = string.IsNullOrWhiteSpace(result.Error) ? "Unknown error." : result.Error;

                throw new InvalidOperationException($"Unable to start elevated VPN helper: {details}");
            }

            StartAdminVpnMonitor(options);
            return;
        }

        if (OperatingSystem.IsMacOS() && !isAdmin)
        {
            if (MacNetworkExtensionService.IsConfigured(RaiseLog))
            {
                RaiseLog($"StartSingBoxVpnAsync: Starting macOS Network Extension tunnel '{MacNetworkExtensionService.ServiceName}'...");
                if (!MacNetworkExtensionService.TryStart(RaiseLog))
                {
                    throw new InvalidOperationException(
                        $"Unable to start macOS Network Extension tunnel '{MacNetworkExtensionService.ServiceName}'.");
                }

                _macNetworkExtensionActive = true;
                return;
            }

            RaiseLog(_preparedMacVpnLaunchConfig != null
                ? "StartSingBoxVpnAsync: Reusing prepared macOS administrator tunnel session."
                : "StartSingBoxVpnAsync: macOS will request administrator privileges for tunnel setup.");
        }

        if (!isAdmin && !OperatingSystem.IsMacOS())
        {
            throw new InvalidOperationException("TUN/VPN mode requires elevated privileges (run as Administrator/root).");
        }

        await _vpnService.StartAsync(config, token).ConfigureAwait(false);
    }

    private async Task PrepareMacPrivilegedTunnelAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        if (!ShouldPrepareMacPrivilegedTunnel(options))
        {
            return;
        }

        var config = await BuildVpnLaunchConfigAsync(options, token).ConfigureAwait(false);
        await _vpnService.PrepareMacPrivilegedTunnelAsync(config, token).ConfigureAwait(false);
        _preparedMacVpnLaunchConfig = config;
    }

    private void ClearPreparedMacVpnLaunchConfig()
    {
        _preparedMacVpnLaunchConfig = null;
    }

    private async Task<VpnLaunchConfig> BuildVpnLaunchConfigAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        var tunCoreMode = NormalizeTunCoreMode(options.TunCoreMode);
        var vpnDir = Path.Combine(_baseDir, "vpn");
        var singBoxPath = Path.Combine(vpnDir, PlatformHelper.SingBoxBinaryName);
        var xrayPath = Path.Combine(vpnDir, PlatformHelper.XrayBinaryName);
        var wintunPath = PlatformHelper.NeedsWintun
            ? Path.Combine(vpnDir, PlatformHelper.WintunLibraryName)
            : null;
        var doh = DohSettingsResolver.Resolve(options);
        if (options.UseCensoredMode &&
            string.Equals(options.SelectedDnsProvider, OnionHopConnectOptions.DnsProviderAuto, StringComparison.Ordinal))
        {
            var dohResolution = await DohSettingsResolver.ResolveWithHealthFallbackAsync(options, RaiseLog, token).ConfigureAwait(false);
            doh = dohResolution.Settings;
        }

        return new VpnLaunchConfig
        {
            SingBoxPath = singBoxPath,
            XrayPath = xrayPath,
            WintunPath = wintunPath,
            VpnCoreMode = tunCoreMode,
            HybridRouting = options.UseHybridRouting,
            SecureDns = options.UseCensoredMode,
            SocksPort = _activeSocksPort,
            DohServer = doh.Server,
            DohServerPort = doh.Port,
            DohPath = doh.Path,
            TorAppProcessNames = ResolveHybridTorApps(options),
            BypassAppProcessNames = ParseProcessNames(options.HybridBypassApps),
            RouteAllWebTrafficThroughTor = options.HybridRouteAllWebTraffic,
            BlockQuicForTorApps = options.HybridBlockQuicForTorApps,
            TunStack = NormalizeTunStackModeForSingBox(options.TunStackMode),
            TunMtu = options.TunMtu,
            TunStrictRoute = options.TunStrictRoute,
            ManageOnionResolver = ShouldManageOnionDnsInsideMacTun(options),
            OnionDnsNameServer = _activeDnsBindAddress
        };
    }

    private static bool ShouldManageOnionDnsInsideMacTun(OnionHopConnectOptions options)
    {
        return options.OnionDnsProxyEnabled &&
               OperatingSystem.IsMacOS() &&
               IsTunMode(options) &&
               !MacNetworkExtensionService.IsConfigured();
    }

    private static bool ShouldPrepareMacPrivilegedTunnel(OnionHopConnectOptions options)
    {
        return OperatingSystem.IsMacOS() &&
               IsTunMode(options) &&
               !PlatformHelper.IsAdministrator() &&
               !MacNetworkExtensionService.IsConfigured();
    }

    private static IReadOnlyList<string> ResolveHybridTorApps(OnionHopConnectOptions options)
    {
        var apps = new List<string>(PlatformHelper.DefaultBrowserProcessNames);
        apps.AddRange(ParseProcessNames(options.HybridTorApps));

        return apps
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ParseProcessNames(string? text) => TorLogHelper.ParseProcessNames(text);

    private static bool? ParseToggleMode(string? mode) => TorLogHelper.ParseToggleMode(mode);
    private static string? ParseConnectionPaddingMode(string? mode) => TorLogHelper.ParseConnectionPaddingMode(mode);
    private static string NormalizeTunCoreMode(string? mode) => string.Equals(mode, OnionHopConnectOptions.TunCoreXray, StringComparison.OrdinalIgnoreCase)
        ? OnionHopConnectOptions.TunCoreXray
        : OnionHopConnectOptions.TunCoreSingBox;
    private static string NormalizeTunStackModeForSingBox(string? mode) => TorLogHelper.NormalizeTunStackModeForSingBox(mode);

    private static async Task<bool> WaitForSocksPortReadyAsync(int port, CancellationToken token, int maxWaitMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, token).ConfigureAwait(false);
                return true;
            }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }

        return false;
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
        var recentOutput = _torService.RecentOutput;
        RaiseLog($"Tor exited with code {exitCode}.");
        if (!string.IsNullOrWhiteSpace(recentOutput))
        {
            RaiseLog($"Tor recent output:\n{recentOutput}");
        }

        // If Tor dies while we're connecting, fail fast instead of waiting for the connect timeout.
        if (_isConnecting)
        {
            var message = $"Tor exited with code {exitCode}.";
            if (!string.IsNullOrWhiteSpace(recentOutput))
            {
                message += $"\n\nTor output:\n{recentOutput}";
            }
            _bootstrapSource?.TrySetException(new InvalidOperationException(message));
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
                && (line.Contains(PlatformHelper.SnowflakeClientBinaryName, StringComparison.OrdinalIgnoreCase)
                    || line.Contains(PlatformHelper.LyrebirdBinaryName, StringComparison.OrdinalIgnoreCase))
                && line.Contains("broker failure", StringComparison.OrdinalIgnoreCase))
            {
                _snowflakeAmpHintShown = true;
                _statusMessage = "Snowflake broker unreachable. Try enabling AMP cache in Settings → Network.";
                PublishStatus();
            }

            RaiseLog($"Tor log: {line}");
        }
    }

    private void OnVpnOutputLine(string line)
    {
        ProcessSingBoxLogLine(line);
    }

    private void ProcessSingBoxLogLine(string? data)
    {
        var line = _singBoxLogProcessor.ProcessLine(data);
        if (line != null)
        {
            _singBoxLogProcessor.TrackWebTunnelBridgeHealth(line, GetActiveBridgeTypeForRuntimeHealth, _bridgeManager, RaiseLog);
        }
    }

    private void OnSingBoxExited(object? sender, EventArgs e)
    {
        int exitCode;
        try
        {
            exitCode = _vpnService.ExitCode ?? 0;
        }
        catch
        {
            exitCode = -1;
        }

        RaiseLog($"{_activeVpnCoreMode} exited with code {exitCode}.");

        if (_isConnected && _activeOptions is { } options && IsTunMode(options) && options.KillSwitchEnabled && !options.UseHybridRouting && !_isDisconnecting)
        {
            if (PlatformHelper.IsAdministrator())
            {
                _killSwitchService.EnableEmergencyBlock(RaiseLog);
            }
            else if (OperatingSystem.IsWindows())
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
            else if (OperatingSystem.IsMacOS())
            {
                _killSwitchService.EnableEmergencyBlock(RaiseLog);
            }
            else
            {
                RaiseLog("Kill switch could not be enabled: elevated privileges are required.");
            }
        }

        if (_isConnected && !_isDisconnecting)
        {
            var lastLines = string.Join("\n", _singBoxLogProcessor.GetRecentLines());
            if (!string.IsNullOrWhiteSpace(lastLines))
            {
                RaiseLog($"VPN last logs before exit:\n{lastLines}");
            }

            _connectionStatus = "VPN stopped";
            _statusMessage = $"VPN tunnel stopped unexpectedly (exit code {exitCode}). Disconnecting...";
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
        _singBoxLogProcessor.ClearConnectionTracking();

        if (OperatingSystem.IsMacOS() && _macNetworkExtensionActive)
        {
            if (!MacNetworkExtensionService.TryStop(RaiseLog))
            {
                RaiseLog("Failed to stop macOS Network Extension tunnel cleanly.");
            }

            _macNetworkExtensionActive = false;
            _singBoxLogProcessor.ClearRecentLines();
            return;
        }

        if (OperatingSystem.IsWindows() && !WindowsAdmin.IsAdministrator())
        {
            _ = Task.Run(async () => await _adminHelper.StopVpnIfAvailableAsync().ConfigureAwait(false));
            _singBoxLogProcessor.ClearRecentLines();
            return;
        }

        _vpnService.Stop();
        _singBoxLogProcessor.ClearRecentLines();
    }

    private void StopTorProcess()
    {
        _torService.Stop();
        _lastNewnymUtc = DateTime.MinValue;
    }

    private static bool IsFatalTorBootstrapLine(string line) => TorLogHelper.IsFatalTorBootstrapLine(line);
    private static bool IsTorProxyHandshakeFailureLine(string line) => TorLogHelper.IsTorProxyHandshakeFailureLine(line);
    private static bool ShouldLogTorLine(string line) => TorLogHelper.ShouldLogTorLine(line);

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

    private string? GetActiveBridgeTypeForRuntimeHealth()
    {
        var options = _activeOptions;
        if (options is null || !options.UseTorBridges)
        {
            return null;
        }

        return options.SelectedBridgeType?.Trim();
    }

    private static int ExtractProgress(string line) => TorLogHelper.ExtractProgress(line);
    private static string? ExtractBootstrapSummary(string line) => TorLogHelper.ExtractBootstrapSummary(line);
    private static IReadOnlyList<int> ParseAllowedPorts(string? raw) => TorLogHelper.ParseAllowedPorts(raw);
    private static int NormalizePreferredProxyPort(int preferredPort, int fallbackPort) => TorLogHelper.NormalizePreferredProxyPort(preferredPort, fallbackPort);
    private static TimeSpan? ResolveConnectTimeout(int? configuredSeconds, TimeSpan automaticTimeout) => TorLogHelper.ResolveConnectTimeout(configuredSeconds, automaticTimeout);
    private static string BuildManualProxyHint(string bindAddress, int socksPort, int? httpPort) => TorLogHelper.BuildManualProxyHint(bindAddress, socksPort, httpPort);

    private static bool UsesSystemProxyScope(OnionHopConnectOptions options)
    {
        return !string.Equals(
            options.ProxyScopeMode,
            OnionHopConnectOptions.ProxyScopeLocalOnly,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesSocksOnlySystemProxyScope(OnionHopConnectOptions options)
    {
        return string.Equals(
            options.ProxyScopeMode,
            OnionHopConnectOptions.ProxyScopeSystemSocks,
            StringComparison.OrdinalIgnoreCase);
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
        => TorLogHelper.LimitBridgeLinesForLaunch(bridgeLines, MaxBridgeLinesForLaunch, MaxBridgeArgumentCharsForLaunch, log);
}
