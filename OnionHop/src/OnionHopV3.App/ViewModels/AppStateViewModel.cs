using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnionHopV3.App.Services;
using OnionHopV3.Core;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Platform;
using OnionHopV3.Core.Platform.Linux;
using OnionHopV3.Core.Platform.MacOS;
using OnionHopV3.Core.Platform.Windows;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.ViewModels;

public sealed partial class AppStateViewModel : ViewModelBase, IDisposable
{
    private const string DefaultAllowedPorts = "80,443";
    private const string DefaultConnectedPageUrl = "https://check.torproject.org/";
    private const string DefaultDisconnectedPageUrl = "https://support.torproject.org/";
    private const string UpdateApiUrl = "https://api.github.com/repos/center2055/OnionHop/releases/latest";
    private const string UpdatePreviewApiUrl = "https://api.github.com/repos/center2055/OnionHop/releases?per_page=20";
    private const string UpdateReleasesPageUrl = "https://github.com/center2055/OnionHop/releases/latest";
    private const int MaxBufferedLogEntries = 6000;
    private const int LogFlushBatchPerQueue = 200;

    public const string AutomaticLocationLabel = "Automatic";
    public const string ConnectionModeProxy = "Proxy Mode (Recommended)";
    public const string ConnectionModeTun = "TUN/VPN Mode (Admin)";
    public const string ProxyScopeSystem = OnionHopConnectOptions.ProxyScopeSystem;
    public const string ProxyScopeSystemSocks = OnionHopConnectOptions.ProxyScopeSystemSocks;
    public const string ProxyScopeLocalOnly = OnionHopConnectOptions.ProxyScopeLocalOnly;
    public const string TunCoreSingBox = OnionHopConnectOptions.TunCoreSingBox;
    public const string TunCoreXray = OnionHopConnectOptions.TunCoreXray;
    public const string TunStackMixed = OnionHopConnectOptions.TunStackMixed;
    public const string TunStackSystem = OnionHopConnectOptions.TunStackSystem;
    public const string TunStackGvisor = OnionHopConnectOptions.TunStackGvisor;
    public const string AutoStartModeOff = "Off";
    public const string AutoStartModeOn = "On";
    public const string AutoStartModeMinimized = "On (Minimized)";
    public const string ThemeModeSystem = "system";
    public const string ThemeModeDark = "dark";
    public const string ThemeModeLight = "light";

    public const string DnsProviderCloudflare = "Cloudflare (DoH)";
    public const string DnsProviderGoogle = "Google (DoH)";
    public const string DnsProviderQuad9 = "Quad9 (DoH)";
    public const string DnsProviderAuto = "Auto (best available DoH)";
    public const string DnsProviderAdGuard = "AdGuard (DoH)";
    public const string DnsProviderMullvad = "Mullvad (DoH)";
    public const string DnsProviderOpenDns = "OpenDNS (DoH)";
    public const string DnsProviderCustom = "Custom (DoH)";
    public const string BridgeTypeAutomatic = "automatic";
    public const string BridgeTypeVanilla = "vanilla";
    public const string BridgeSourceAuto = OnionHopConnectOptions.BridgeSourceAuto;
    public const string BridgeSourceOnlineOnly = OnionHopConnectOptions.BridgeSourceOnlineOnly;
    public const string BridgeSourceOfflineOnly = OnionHopConnectOptions.BridgeSourceOfflineOnly;
    public const string BridgeSourceCollectorOnly = OnionHopConnectOptions.BridgeSourceCollectorOnly;
    private static readonly Regex ExitFingerprintRegex = new("^[A-F0-9]{40}$", RegexOptions.Compiled);
    /// <summary>
    /// Maps runtime status text to localization resource keys.
    /// Includes legacy/localized values so language switching can re-localize already-displayed text.
    /// </summary>
    private static readonly Dictionary<string, string> RuntimeStatusResourceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Disconnected"] = "Status.Disconnected",
        ["Getrennt"] = "Status.Disconnected",
        ["Connected"] = "Status.Connected",
        ["Verbunden"] = "Status.Connected",
        ["Connecting..."] = "Status.Connecting",
        ["Verbinde..."] = "Status.Connecting",
        ["Disconnecting..."] = "Status.Disconnecting",
        ["Trenne..."] = "Status.Disconnecting",
        ["Ready to route traffic through Tor."] = "Status.ReadyToRoute",
        ["Bereit, den Datenverkehr über Tor zu leiten."] = "Status.ReadyToRoute",
        ["Resolving..."] = "Status.Resolving",
        ["Wird aufgelöst..."] = "Status.Resolving",
        ["Downloading components. Please wait."] = "Status.DownloadingComponentsWait",
        ["Komponenten werden heruntergeladen. Bitte warten."] = "Status.DownloadingComponentsWait",
        ["Windows networking changes require Administrator access. Starting the privileged helper..."] = "Status.AdminRequiredRequesting",
        ["Windows-Netzwerkänderungen benötigen Administratorrechte. Privilegierter Helfer wird gestartet..."] = "Status.AdminRequiredRequesting",
        ["Administrator access is required for this Windows networking feature. Connection canceled."] = "Status.AdminRequiredCanceled",
        ["Administratorrechte sind für diese Windows-Netzwerkfunktion erforderlich. Verbindung abgebrochen."] = "Status.AdminRequiredCanceled",
        ["Canceling connection attempt..."] = "Status.CancelingConnect",
        ["Verbindungsaufbau wird abgebrochen..."] = "Status.CancelingConnect",
        ["Default settings restored."] = "Status.DefaultsRestored",
        ["Standardeinstellungen wiederhergestellt."] = "Status.DefaultsRestored",
        ["Checking components..."] = "Status.CheckingComponents",
        ["Komponenten werden geprüft..."] = "Status.CheckingComponents"
    };

    private static readonly HashSet<string> SettingsProperties = new(StringComparer.Ordinal)
    {
        nameof(AutoConnect),
        nameof(AutoStartMode),
        nameof(MinimizeToTray),
        nameof(AutoUpdate),
        nameof(KillSwitchEnabled),
        nameof(ThemeMode),
        nameof(IsDarkMode),
        nameof(UseNativeTheme),
        nameof(SelectedLocation),
        nameof(SelectedEntryLocation),
        nameof(EntryNodeFingerprint),
        nameof(MiddleNodeFingerprint),
        nameof(ExitNodeFingerprint),
        nameof(SelectedConnectionMode),
        nameof(UseHybridRouting),
        nameof(SmartConnectEnabled),
        nameof(UseTorBridges),
        nameof(UseCensoredMode),
        nameof(SelectedBridgeType),
        nameof(BridgeSourceMode),
        nameof(CustomBridges),
        nameof(CustomSniHosts),
        nameof(UseSnowflakeAmp),
        nameof(SnowflakeAmpCache),
        nameof(TorIpv6Mode),
        nameof(HardwareAccelerationMode),
        nameof(ConnectionPaddingMode),
        nameof(SelectedDnsProvider),
        nameof(CustomDohHost),
        nameof(CustomDohPath),
        nameof(ProxyScopeMode),
        nameof(PreferredSocksPort),
        nameof(PreferredHttpPort),
        nameof(AllowLanProxyAccess),
        nameof(TunCoreMode),
        nameof(TunStackMode),
        nameof(TunMtu),
        nameof(TunStrictRoute),
        nameof(ConnectionTimeoutSeconds),
        nameof(RestrictedFirewallMode),
        nameof(AllowedPorts),
        nameof(OnionDnsProxyEnabled),
        nameof(StrictManualEntryNodeFingerprint),
        nameof(StrictManualMiddleNodeFingerprint),
        nameof(StrictManualExitNodeFingerprint),
        nameof(ShowAdvancedHomeConnectionDetails),
        nameof(MaxCircuitInactivityMinutes),
        nameof(OpenConnectedPageEnabled),
        nameof(ConnectedPageUrl),
        nameof(OpenDisconnectedPageEnabled),
        nameof(DisconnectedPageUrl),
        nameof(EnableDiscordStatus),
        nameof(HybridRouteAllWebTraffic),
        nameof(HybridBlockQuicForTorApps),
        nameof(BlockUdpTraffic),
        nameof(HybridTorApps),
        nameof(HybridBypassApps),
        nameof(SelectedLanguage),
        nameof(SelectedAccentColor),
        nameof(SelectedTorEngineMode),
        nameof(SelectedRelayRefreshInterval),
        nameof(SelectedUpdateChannel),
        nameof(ClearSessionDataOnDisconnect),
        nameof(DnsLeakProtectionEnabled),
        nameof(ClipboardProtectionEnabled),
        nameof(SnowflakeProxyAutoStart),
        nameof(SnowflakeProxyCapacity)
    };

    private readonly OnionHopClient _client;
    private readonly SettingsService _settingsService;
    private readonly TorNodeDatabaseService _nodeDatabaseService = new();
    private readonly DiscordPresenceService _discordPresence = new();
    private readonly SmartConnectAdvisor _smartConnectAdvisor = new();
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _settingsSaveCts;
    private DispatcherTimer? _bridgeRefreshTimer;
    private bool _loadingSettings;
    private bool _disposed;
    private bool _hasStatusSnapshot;
    private bool _wasConnected;
    private Dictionary<string, TorCountryNodeStats> _countryStatsByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLogsLock = new();
    private readonly Queue<string> _pendingAppLogs = new();
    private readonly Queue<string> _pendingDnsLogs = new();
    private readonly Queue<string> _pendingXrayLogs = new();
    private int _droppedAppLogLines;
    private int _droppedDnsLogLines;
    private int _droppedXrayLogLines;

    public AppStateViewModel()
    {
        Locations =
        [
            AutomaticLocationLabel,
            "us",
            "gb",
            "de",
            "fr",
            "ch",
            "nl",
            "ca",
            "sg"
        ];

        ConnectionModes =
        [
            ConnectionModeProxy,
            ConnectionModeTun
        ];

        AutoStartModes =
        [
            AutoStartModeOff,
            AutoStartModeOn,
            AutoStartModeMinimized
        ];

        ThemeModes =
        [
            ThemeModeSystem,
            ThemeModeDark,
            ThemeModeLight
        ];

        DnsProviders =
        [
            DnsProviderAuto,
            DnsProviderCloudflare,
            DnsProviderQuad9,
            DnsProviderAdGuard,
            DnsProviderMullvad,
            DnsProviderOpenDns,
            DnsProviderGoogle,
            DnsProviderCustom
        ];

        ProxyScopeModes =
        [
            ProxyScopeSystem,
            ProxyScopeSystemSocks,
            ProxyScopeLocalOnly
        ];

        // Xray TUN is not supported on macOS: xray-core lacks process-name matching on macOS
        // (no find_process_darwin.go), so routing rules can't distinguish tor's traffic from
        // regular traffic, causing routing loops that kill all internet connectivity.
        TunCoreModes = OperatingSystem.IsMacOS()
            ? [TunCoreSingBox]
            : [TunCoreSingBox, TunCoreXray];

        TunStackModes =
        [
            TunStackMixed,
            TunStackSystem,
            TunStackGvisor
        ];

        TorOptionModes =
        [
            OnionHopConnectOptions.ToggleModeDefault,
            OnionHopConnectOptions.ToggleModeEnabled,
            OnionHopConnectOptions.ToggleModeDisabled
        ];

        ConnectionPaddingModes =
        [
            OnionHopConnectOptions.ConnectionPaddingAuto,
            OnionHopConnectOptions.ConnectionPaddingEnabled,
            OnionHopConnectOptions.ConnectionPaddingDisabled
        ];

        BridgeSourceModes =
        [
            BridgeSourceAuto,
            BridgeSourceOnlineOnly,
            BridgeSourceCollectorOnly,
            BridgeSourceOfflineOnly
        ];

        RefreshLanguageOptions();
        RefreshLocalizedOptions();

        BridgeTypes.Add(BridgeTypeAutomatic);
        BridgeTypes.Add(BridgeTypeVanilla);
        BridgeTypes.Add("obfs4");
        BridgeTypes.Add("snowflake");
        BridgeTypes.Add("conjure");
        BridgeTypes.Add("meek-azure");
        BridgeTypes.Add("webtunnel");
        BridgeTypes.Add("dnstt");
        BridgeTypes.Add("custom");

        _settingsService = new SettingsService(Program.OverrideBaseDirectory);
        _client = new OnionHopClient(Program.OverrideBaseDirectory);
        _client.Log += (_, message) => EnqueueClientLog(message);
        _client.DnsLog += (_, message) => EnqueueDnsLog(message);
        _client.VpnLog += (_, message) => Dispatcher.UIThread.Post(() => AppendVpnLog(message));
        _client.StatusUpdated += (_, update) => Dispatcher.UIThread.Post(() => ApplyClientStatus(update));
        _client.DependencyUpdated += (_, update) => Dispatcher.UIThread.Post(() => ApplyDependencyUpdate(update));
        _client.SnowflakeProxyStatusUpdated += (_, status) => Dispatcher.UIThread.Post(() => ApplySnowflakeProxyStatus(status));
        StartLogPump();
        LastBridgeDataUpdateUtc = _client.GetLastBridgeDataUpdateUtc();

        LoadSettings();
        ApplyTheme();
        ConnectionStatus = LocalizationService.Get("Status.Disconnected");
        StatusMessage = LocalizationService.Get("Status.ReadyToRoute");
        DependencyDownloadStatus = LocalizationService.Get("Status.CheckingComponents");

        PropertyChanged += OnAnyPropertyChanged;
    }

    public ObservableCollection<string> Locations { get; }
    public ObservableCollection<LocalizedOption> LocationOptions { get; } = [];
    public ObservableCollection<string> ConnectionModes { get; }
    public ObservableCollection<LocalizedOption> ConnectionModeOptions { get; } = [];
    public ObservableCollection<string> AutoStartModes { get; }
    public ObservableCollection<LocalizedOption> AutoStartModeOptions { get; } = [];
    public ObservableCollection<string> ThemeModes { get; }
    public ObservableCollection<LocalizedOption> ThemeModeOptions { get; } = [];
    public ObservableCollection<string> BridgeTypes { get; } = [];
    public ObservableCollection<LocalizedOption> BridgeTypeOptions { get; } = [];
    public ObservableCollection<string> BridgeSourceModes { get; }
    public ObservableCollection<LocalizedOption> BridgeSourceModeOptions { get; } = [];
    public ObservableCollection<string> DnsProviders { get; }
    public ObservableCollection<string> ProxyScopeModes { get; }
    public ObservableCollection<LocalizedOption> ProxyScopeModeOptions { get; } = [];
    public ObservableCollection<string> TunCoreModes { get; }
    public ObservableCollection<LocalizedOption> TunCoreModeOptions { get; } = [];
    public ObservableCollection<string> TunStackModes { get; }
    public ObservableCollection<LocalizedOption> TunStackModeOptions { get; } = [];
    public ObservableCollection<string> TorOptionModes { get; }
    public ObservableCollection<string> ConnectionPaddingModes { get; }
    public ObservableCollection<LocalizedOption> LanguageOptions { get; } = [];
    public ObservableCollection<LocalizedOption> TorOptionModeOptions { get; } = [];
    public ObservableCollection<LocalizedOption> ConnectionPaddingModeOptions { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<string> DnsLogLines { get; } = [];
    public ObservableCollection<string> XrayLogLines { get; } = [];
    public string TunEngineLogTabHeader => string.Equals(
            NormalizeTunCoreMode(TunCoreMode),
            TunCoreXray,
            StringComparison.Ordinal)
        ? LocalizationService.Get("Logs.TabXray")
        : LocalizationService.Get("Logs.TabSingBox");
    public ObservableCollection<string> VpnLogLines { get; } = [];

    [ObservableProperty] private string _downloadSpeed = "--";
    [ObservableProperty] private string _uploadSpeed = "--";
    [ObservableProperty] private double _downloadSpeedGauge;
    [ObservableProperty] private double _uploadSpeedGauge;
    [ObservableProperty] private string _connectionElapsed = string.Empty;
    [ObservableProperty] private bool _showConnectionElapsed;

    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isDisconnecting;
    [ObservableProperty] private bool _isPreparingConnection;

    [ObservableProperty] private string _selectedLocation = AutomaticLocationLabel;
    [ObservableProperty] private string _selectedEntryLocation = AutomaticLocationLabel;
    [ObservableProperty] private string _entryNodeFingerprint = string.Empty;
    [ObservableProperty] private string _middleNodeFingerprint = string.Empty;
    [ObservableProperty] private string _exitNodeFingerprint = string.Empty;
    [ObservableProperty] private string _selectedConnectionMode = ConnectionModeProxy;
    [ObservableProperty] private bool _useHybridRouting;
    [ObservableProperty] private bool _smartConnectEnabled = true;
    [ObservableProperty] private bool _killSwitchEnabled;

    [ObservableProperty] private bool _useTorBridges;
    [ObservableProperty] private bool _useCensoredMode;
    [ObservableProperty] private string _selectedBridgeType = "obfs4";
    [ObservableProperty] private string _bridgeSourceMode = BridgeSourceAuto;
    [ObservableProperty] private string _customBridges = string.Empty;
    [ObservableProperty] private string _customSniHosts = string.Empty;
    [ObservableProperty] private bool _useSnowflakeAmp;
    [ObservableProperty] private string _snowflakeAmpCache = string.Empty;

    [ObservableProperty] private string _torIpv6Mode = OnionHopConnectOptions.ToggleModeDefault;
    [ObservableProperty] private string _hardwareAccelerationMode = OnionHopConnectOptions.ToggleModeDefault;
    [ObservableProperty] private string _connectionPaddingMode = OnionHopConnectOptions.ConnectionPaddingAuto;

    [ObservableProperty] private string _selectedDnsProvider = DnsProviderCloudflare;
    [ObservableProperty] private string _customDohHost = string.Empty;
    [ObservableProperty] private string _customDohPath = "/dns-query";
    [ObservableProperty] private string _proxyScopeMode = ProxyScopeSystem;
    [ObservableProperty] private string _preferredSocksPort = OnionHopConnectOptions.DefaultSocksPort.ToString();
    [ObservableProperty] private string _preferredHttpPort = OnionHopConnectOptions.DefaultHttpPort.ToString();
    [ObservableProperty] private bool _allowLanProxyAccess;
    [ObservableProperty] private string _tunCoreMode = TunCoreSingBox;
    [ObservableProperty] private string _tunStackMode = TunStackMixed;
    [ObservableProperty] private string _tunMtu = string.Empty;
    [ObservableProperty] private bool _tunStrictRoute = true;
    [ObservableProperty] private string _connectionTimeoutSeconds = string.Empty;
    [ObservableProperty] private bool _restrictedFirewallMode;
    [ObservableProperty] private string _allowedPorts = DefaultAllowedPorts;
    [ObservableProperty] private bool _onionDnsProxyEnabled;
    [ObservableProperty] private bool _strictManualEntryNodeFingerprint = true;
    [ObservableProperty] private bool _strictManualMiddleNodeFingerprint = true;
    [ObservableProperty] private bool _strictManualExitNodeFingerprint = true;
    [ObservableProperty] private bool _showAdvancedHomeConnectionDetails;
    [ObservableProperty] private int _maxCircuitInactivityMinutes = 10;
    [ObservableProperty] private bool _openConnectedPageEnabled;
    [ObservableProperty] private string _connectedPageUrl = DefaultConnectedPageUrl;
    [ObservableProperty] private bool _openDisconnectedPageEnabled;
    [ObservableProperty] private string _disconnectedPageUrl = DefaultDisconnectedPageUrl;
    [ObservableProperty] private bool _enableDiscordStatus;
    [ObservableProperty] private string _selectedLanguage = "en";
    [ObservableProperty] private int _selectedLanguageIndex;
    [ObservableProperty] private LocalizedOption? _selectedConnectionModeOption;
    [ObservableProperty] private LocalizedOption? _selectedLocationOption;
    [ObservableProperty] private LocalizedOption? _selectedEntryLocationOption;
    [ObservableProperty] private LocalizedOption? _selectedBridgeTypeOption;
    [ObservableProperty] private LocalizedOption? _selectedBridgeSourceModeOption;
    [ObservableProperty] private LocalizedOption? _selectedProxyScopeModeOption;
    [ObservableProperty] private LocalizedOption? _selectedTunCoreModeOption;
    [ObservableProperty] private LocalizedOption? _selectedTunStackModeOption;
    [ObservableProperty] private LocalizedOption? _selectedLanguageOption;
    [ObservableProperty] private LocalizedOption? _selectedAutoStartModeOption;
    [ObservableProperty] private LocalizedOption? _selectedThemeModeOption;
    [ObservableProperty] private LocalizedOption? _selectedTorIpv6ModeOption;
    [ObservableProperty] private LocalizedOption? _selectedHardwareAccelerationModeOption;
    [ObservableProperty] private LocalizedOption? _selectedConnectionPaddingModeOption;

    [ObservableProperty] private bool _hybridRouteAllWebTraffic = true;
    [ObservableProperty] private bool _hybridBlockQuicForTorApps = true;
    [ObservableProperty] private string _hybridTorApps = string.Empty;
    [ObservableProperty] private string _hybridBypassApps = string.Empty;

    /// <summary>One-line summary of the per-app split-tunnel rules, shown under the picker button.</summary>
    public string HybridAppsSummary
    {
        get
        {
            var tor = HybridAppList.Parse(HybridTorApps);
            var bypass = HybridAppList.Parse(HybridBypassApps);
            if (tor.Count == 0 && bypass.Count == 0)
            {
                return LocalizationService.Get("Settings.AppPickerNone");
            }

            var parts = new List<string>();
            if (tor.Count > 0)
            {
                parts.Add($"{LocalizationService.Get("Settings.AppPickerModeTor")}: {string.Join(", ", tor)}");
            }

            if (bypass.Count > 0)
            {
                parts.Add($"{LocalizationService.Get("Settings.AppPickerModeDirect")}: {string.Join(", ", bypass)}");
            }

            return string.Join("    •    ", parts);
        }
    }

    partial void OnHybridTorAppsChanged(string value) => OnPropertyChanged(nameof(HybridAppsSummary));
    partial void OnHybridBypassAppsChanged(string value) => OnPropertyChanged(nameof(HybridAppsSummary));

    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private string _autoStartMode = AutoStartModeOff;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private string _themeMode = ThemeModeSystem;
    [ObservableProperty] private bool _isDarkMode;
    [ObservableProperty] private bool _useNativeTheme;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _sidebarStatusMessage = string.Empty;
    [ObservableProperty] private string _connectionStatus = string.Empty;
    [ObservableProperty] private string _currentIp = "--.--.--.--";
    [ObservableProperty] private string _socksProxyPort = OnionHopClient.DefaultSocksPort.ToString();
    [ObservableProperty] private string _httpProxyPort = "--";
    [ObservableProperty] private double _connectionProgress;

    [ObservableProperty] private bool _isDependencyDownloadInProgress;
    [ObservableProperty] private double _dependencyDownloadProgress;
    [ObservableProperty] private string _dependencyDownloadStatus = string.Empty;
    [ObservableProperty] private bool _isBridgeDataUpdateInProgress;
    [ObservableProperty] private DateTimeOffset? _lastBridgeDataUpdateUtc;

    private DispatcherTimer? _speedTimer;
    private DispatcherTimer? _ipRefreshTimer;
    private DispatcherTimer? _connectionElapsedTimer;
    private DispatcherTimer? _logFlushTimer;
    private DateTime? _connectionStartedUtc;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSpeedSampleUtc;
    private bool _speedUpdateInProgress;

    public bool IsBusy => IsPreparingConnection || IsConnecting || IsDisconnecting || IsDependencyDownloadInProgress;

    public bool ShowConnectButton => !IsConnected && !IsPreparingConnection && !IsConnecting;
    public bool ShowDisconnectButton => IsConnected && !IsPreparingConnection && !IsConnecting && !IsDisconnecting;
    public bool ShowCancelButton => IsPreparingConnection || IsConnecting;
    public bool CanUpdateBridgeData => IsConnected && !IsPreparingConnection && !IsConnecting && !IsDisconnecting && !IsBridgeDataUpdateInProgress;

    public bool IsTunMode => string.Equals(SelectedConnectionMode, ConnectionModeTun, StringComparison.Ordinal);
    public bool IsProxyMode => !IsTunMode;
    public bool CanUseKillSwitch => IsTunMode && !UseHybridRouting;
    public bool IsManualEntryNodeFingerprintSet => !string.IsNullOrWhiteSpace(EntryNodeFingerprint);
    public bool IsManualMiddleNodeFingerprintSet => !string.IsNullOrWhiteSpace(MiddleNodeFingerprint);
    public bool IsManualExitNodeFingerprintSet => !string.IsNullOrWhiteSpace(ExitNodeFingerprint);
    public bool CanSelectExitLocation => !IsManualExitNodeFingerprintSet;
    public bool CanSelectEntryLocation => !UseTorBridges && !IsManualEntryNodeFingerprintSet;
    public bool IsCustomDoh => string.Equals(SelectedDnsProvider, DnsProviderCustom, StringComparison.Ordinal);
    public bool UseCustomBridges => string.Equals(SelectedBridgeType, "custom", StringComparison.OrdinalIgnoreCase);
    public bool IsSnowflakeBridgeSelected => string.Equals(SelectedBridgeType, "snowflake", StringComparison.OrdinalIgnoreCase);
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsLinux => OperatingSystem.IsLinux();
    public bool IsMacOS => OperatingSystem.IsMacOS();
    public bool ShowWindowsOnlySettings => IsWindows;
    public bool ShowMacOnlySettings => IsMacOS;
    public bool ShowTunStackOptions => !IsMacOS;
    public string VpnLogTabHeader => TunEngineLogTabHeader;
    public bool CanUseOnionDnsProxy => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || PlatformHelper.IsAdministrator();
    public string ManualEntryFingerprintSummary => BuildFingerprintSummary(EntryNodeFingerprint);
    public string ManualMiddleFingerprintSummary => BuildFingerprintSummary(MiddleNodeFingerprint);
    public string ManualExitFingerprintSummary => BuildFingerprintSummary(ExitNodeFingerprint);
    public bool UseCustomChrome => !UseNativeTheme;
    public bool UseNativeMacChrome => IsMacOS && UseNativeTheme;
    public bool SupportsNativeWindowChrome => true;
    public bool CanConfigureSplitTunneling => IsTunMode && UseHybridRouting;
    public string BridgeDataLastUpdateText
    {
        get
        {
            if (!LastBridgeDataUpdateUtc.HasValue || LastBridgeDataUpdateUtc.Value == DateTimeOffset.MinValue)
            {
                return LocalizationService.Get("Home.BridgeDataLastUpdateUnknown");
            }

            var localTime = LastBridgeDataUpdateUtc.Value.ToLocalTime();
            return string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Get("Home.BridgeDataLastUpdateValue"),
                localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
        }
    }

    // NOTE: intentionally a class (reference equality), NOT a record. ComboBox option lists are
    // rebuilt (Clear + re-Add) on language/data refresh. With record value-equality, reassigning
    // Selected*Option to a value-equal new instance is a no-op under [ObservableProperty].SetProperty,
    // so the ComboBox never learns its (now-removed) selected item changed and renders blank.
    // Reference equality guarantees each rebuilt instance is distinct -> the selection updates.
    public sealed class LocalizedOption
    {
        public LocalizedOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }

    partial void OnStatusMessageChanged(string value)
    {
        SidebarStatusMessage = BuildSidebarStatusMessage(value);
    }

    partial void OnSelectedLanguageOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            var fallbackIndex = Array.IndexOf(SupportedLanguageCodes, NormalizeLanguageCode(SelectedLanguage));
            if (fallbackIndex < 0)
            {
                fallbackIndex = 0;
            }
            if (SelectedLanguageIndex != fallbackIndex)
            {
                SelectedLanguageIndex = fallbackIndex;
            }

            if (fallbackIndex >= 0 && fallbackIndex < LanguageOptions.Count)
            {
                SelectedLanguageOption = LanguageOptions[fallbackIndex];
            }
            return;
        }

        var selectedIndex = LanguageOptions.IndexOf(value);
        if (selectedIndex >= 0 && SelectedLanguageIndex != selectedIndex)
        {
            SelectedLanguageIndex = selectedIndex;
        }

        if (!string.Equals(SelectedLanguage, value.Value, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLanguage = value.Value;
        }
    }

    partial void OnSelectedConnectionModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(SelectedConnectionMode, value.Value, StringComparison.Ordinal))
        {
            SelectedConnectionMode = value.Value;
        }
    }

    partial void OnSelectedLocationOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(SelectedLocation, value.Value, StringComparison.Ordinal))
        {
            SelectedLocation = value.Value;
        }
    }

    partial void OnSelectedEntryLocationOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(SelectedEntryLocation, value.Value, StringComparison.Ordinal))
        {
            SelectedEntryLocation = value.Value;
        }
    }

    partial void OnSelectedBridgeTypeChanged(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "obfs4" : value.Trim().ToLowerInvariant();
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            SelectedBridgeType = normalized;
            return;
        }

        SelectedBridgeTypeOption = BridgeTypeOptions.FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(UseCustomBridges));
        OnPropertyChanged(nameof(IsSnowflakeBridgeSelected));
    }

    partial void OnSelectedBridgeTypeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(SelectedBridgeType, value.Value, StringComparison.OrdinalIgnoreCase))
        {
            SelectedBridgeType = value.Value;
        }
    }

    partial void OnBridgeSourceModeChanged(string value)
    {
        var normalized = NormalizeBridgeSourceMode(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            BridgeSourceMode = normalized;
            return;
        }

        SelectedBridgeSourceModeOption = BridgeSourceModeOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
    }

    partial void OnSelectedBridgeSourceModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(BridgeSourceMode, value.Value, StringComparison.Ordinal))
        {
            BridgeSourceMode = value.Value;
        }
    }

    partial void OnProxyScopeModeChanged(string value)
    {
        var normalized = NormalizeProxyScopeMode(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            ProxyScopeMode = normalized;
            return;
        }

        SelectedProxyScopeModeOption = ProxyScopeModeOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));

        OnPropertyChanged(nameof(IsSystemProxyScope));
        OnPropertyChanged(nameof(ShowSystemProxyButton));
        CanToggleSystemProxy = ComputeCanToggleSystemProxy();
    }

    partial void OnSelectedProxyScopeModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(ProxyScopeMode, value.Value, StringComparison.Ordinal))
        {
            ProxyScopeMode = value.Value;
        }
    }

    partial void OnTunCoreModeChanged(string value)
    {
        var normalized = NormalizeTunCoreMode(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            TunCoreMode = normalized;
            return;
        }

        SelectedTunCoreModeOption = TunCoreModeOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
        OnPropertyChanged(nameof(TunEngineLogTabHeader));
        OnPropertyChanged(nameof(VpnLogTabHeader));
    }

    partial void OnSelectedTunCoreModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(TunCoreMode, value.Value, StringComparison.Ordinal))
        {
            TunCoreMode = value.Value;
        }
    }

    partial void OnTunStackModeChanged(string value)
    {
        var normalized = NormalizeTunStackMode(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            TunStackMode = normalized;
            return;
        }

        SelectedTunStackModeOption = TunStackModeOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
    }

    partial void OnSelectedTunStackModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(TunStackMode, value.Value, StringComparison.Ordinal))
        {
            TunStackMode = value.Value;
        }
    }

    partial void OnUseTorBridgesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectEntryLocation));
    }

    partial void OnSelectedAutoStartModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(AutoStartMode, value.Value, StringComparison.OrdinalIgnoreCase))
        {
            AutoStartMode = value.Value;
        }
    }

    partial void OnThemeModeChanged(string value)
    {
        var normalized = NormalizeThemeMode(value);
        if (!string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
        {
            ThemeMode = normalized;
            return;
        }

        SelectedThemeModeOption = ThemeModeOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase));

        var shouldBeDark = string.Equals(normalized, ThemeModeDark, StringComparison.OrdinalIgnoreCase);
        if (IsDarkMode != shouldBeDark)
        {
            IsDarkMode = shouldBeDark;
        }
    }

    partial void OnSelectedThemeModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(ThemeMode, value.Value, StringComparison.OrdinalIgnoreCase))
        {
            ThemeMode = value.Value;
        }
    }

    // Order must match the entries added in RefreshLanguageOptions (en, de, fr, ru, zh).
    private static readonly string[] SupportedLanguageCodes = { "en", "de", "fr", "ru", "zh" };

    private static string NormalizeLanguageCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "en";
        }

        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("de", StringComparison.Ordinal)) return "de";
        if (trimmed.StartsWith("fr", StringComparison.Ordinal)) return "fr";
        if (trimmed.StartsWith("ru", StringComparison.Ordinal)) return "ru";
        if (trimmed.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        return "en";
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        var normalized = NormalizeLanguageCode(value);
        if (!string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLanguage = normalized;
            return;
        }

        var languageIndex = Array.IndexOf(SupportedLanguageCodes, normalized);
        if (languageIndex < 0)
        {
            languageIndex = 0;
        }
        if (SelectedLanguageIndex != languageIndex)
        {
            SelectedLanguageIndex = languageIndex;
        }

        LocalizationService.ApplyLanguage(normalized);
        RefreshLocalizedOptions();
        RefreshLanguageOptions();
        ConnectionStatus = LocalizeRuntimeText(ConnectionStatus);
        StatusMessage = LocalizeRuntimeText(StatusMessage);
        DependencyDownloadStatus = LocalizeRuntimeText(DependencyDownloadStatus);
        OnPropertyChanged(nameof(BridgeDataLastUpdateText));
        OnPropertyChanged(nameof(TunEngineLogTabHeader));
        OnPropertyChanged(nameof(VpnLogTabHeader));
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var language = value >= 0 && value < SupportedLanguageCodes.Length ? SupportedLanguageCodes[value] : "en";
        if (!string.Equals(SelectedLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLanguage = language;
            return;
        }

        if (value >= 0 && value < LanguageOptions.Count)
        {
            var option = LanguageOptions[value];
            if (!ReferenceEquals(SelectedLanguageOption, option))
            {
                SelectedLanguageOption = option;
            }
        }
    }

    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowConnectButton));
        OnPropertyChanged(nameof(ShowDisconnectButton));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(CanUpdateBridgeData));
    }

    partial void OnIsPreparingConnectionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowConnectButton));
        OnPropertyChanged(nameof(ShowDisconnectButton));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(CanUpdateBridgeData));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowConnectButton));
        OnPropertyChanged(nameof(ShowDisconnectButton));
        OnPropertyChanged(nameof(CanUpdateBridgeData));

        if (value)
        {
            StartConnectionElapsedTimer();
            _ = RefreshBridgeDataAsync();
        }
        else
        {
            StopConnectionElapsedTimer();
        }
    }

    partial void OnIsDisconnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowDisconnectButton));
        OnPropertyChanged(nameof(CanUpdateBridgeData));
    }

    partial void OnIsDependencyDownloadInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowConnectButton));
    }

    partial void OnIsBridgeDataUpdateInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpdateBridgeData));
    }

    partial void OnLastBridgeDataUpdateUtcChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(BridgeDataLastUpdateText));
    }

    partial void OnSelectedConnectionModeChanged(string value)
    {
        SelectedConnectionModeOption = ConnectionModeOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal));
        OnPropertyChanged(nameof(IsTunMode));
        OnPropertyChanged(nameof(IsProxyMode));
        OnPropertyChanged(nameof(CanUseKillSwitch));
        OnPropertyChanged(nameof(CanConfigureSplitTunneling));
        OnPropertyChanged(nameof(ShowSystemProxyButton));
        CanToggleSystemProxy = ComputeCanToggleSystemProxy();
    }

    partial void OnSelectedLocationChanged(string value)
    {
        if (IsManualExitNodeFingerprintSet &&
            !string.Equals(value, AutomaticLocationLabel, StringComparison.Ordinal))
        {
            SelectedLocation = AutomaticLocationLabel;
            return;
        }

        SelectedLocationOption = LocationOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal));

        // Live-update exit country via Tor control port when already connected
        if (IsConnected)
        {
            var countryCode = string.Equals(value, AutomaticLocationLabel, StringComparison.Ordinal)
                ? null
                : value;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _client.ChangeExitCountryAsync(countryCode, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppendLog($"Failed to change exit country: {ex.Message}");
                }
            });
        }
    }

    partial void OnSelectedEntryLocationChanged(string value)
    {
        if (IsManualEntryNodeFingerprintSet &&
            !string.Equals(value, AutomaticLocationLabel, StringComparison.Ordinal))
        {
            SelectedEntryLocation = AutomaticLocationLabel;
            return;
        }

        SelectedEntryLocationOption = LocationOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal));
    }

    partial void OnEntryNodeFingerprintChanged(string value)
    {
        var normalized = NormalizeExitNodeFingerprint(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            EntryNodeFingerprint = normalized;
            return;
        }

        OnPropertyChanged(nameof(IsManualEntryNodeFingerprintSet));
        OnPropertyChanged(nameof(CanSelectEntryLocation));
        OnPropertyChanged(nameof(ManualEntryFingerprintSummary));
        OnPropertyChanged(nameof(AdvancedConnectionSummary));
        if (IsManualEntryNodeFingerprintSet &&
            !string.Equals(SelectedEntryLocation, AutomaticLocationLabel, StringComparison.Ordinal))
        {
            SelectedEntryLocation = AutomaticLocationLabel;
        }
    }

    partial void OnMiddleNodeFingerprintChanged(string value)
    {
        var normalized = NormalizeExitNodeFingerprint(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            MiddleNodeFingerprint = normalized;
            return;
        }

        OnPropertyChanged(nameof(IsManualMiddleNodeFingerprintSet));
        OnPropertyChanged(nameof(ManualMiddleFingerprintSummary));
        OnPropertyChanged(nameof(AdvancedConnectionSummary));
    }

    partial void OnExitNodeFingerprintChanged(string value)
    {
        var normalized = NormalizeExitNodeFingerprint(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            ExitNodeFingerprint = normalized;
            return;
        }

        OnPropertyChanged(nameof(IsManualExitNodeFingerprintSet));
        OnPropertyChanged(nameof(CanSelectExitLocation));
        OnPropertyChanged(nameof(ManualExitFingerprintSummary));
        OnPropertyChanged(nameof(AdvancedConnectionSummary));
        if (IsManualExitNodeFingerprintSet &&
            !string.Equals(SelectedLocation, AutomaticLocationLabel, StringComparison.Ordinal))
        {
            SelectedLocation = AutomaticLocationLabel;
        }
    }

    public async Task ApplyPreferredMiddleFingerprintAsync(string fingerprint, string relayName, CancellationToken token)
    {
        var normalized = NormalizeExitNodeFingerprint(fingerprint);
        var relayDisplayName = string.IsNullOrWhiteSpace(relayName)
            ? BuildFingerprintSummary(normalized)
            : relayName.Trim();

        if (string.IsNullOrWhiteSpace(normalized) || !ExitFingerprintRegex.IsMatch(normalized))
        {
            StatusMessage = "Selected middle relay fingerprint is invalid.";
            AppendLog($"Preferred middle was not applied because relay '{relayDisplayName}' has an invalid fingerprint.");
            return;
        }

        MiddleNodeFingerprint = normalized;
        StrictManualMiddleNodeFingerprint = true;

        var fingerprintSummary = BuildFingerprintSummary(normalized);
        if (!IsConnected)
        {
            StatusMessage = $"Preferred middle set to {relayDisplayName}. It will apply on the next connection.";
            AppendLog($"Preferred middle set to {relayDisplayName} ({fingerprintSummary}). Reconnect to use it.");
            return;
        }

        try
        {
            var liveApplied = await _client
                .ChangeMiddleFingerprintAsync(normalized, strict: true, token)
                .ConfigureAwait(true);

            if (liveApplied)
            {
                StatusMessage = $"Preferred middle set to {relayDisplayName}. Requested a new circuit.";
                AppendLog($"Preferred middle set to {relayDisplayName} ({fingerprintSummary}).");
                return;
            }

            StatusMessage = $"Preferred middle set to {relayDisplayName}. Reconnect if the live circuit did not change.";
            AppendLog($"Preferred middle set to {relayDisplayName} ({fingerprintSummary}); live update was not available.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preferred middle saved, but live update failed: {ex.Message}";
            AppendLog($"Preferred middle live update failed: {ex.Message}");
        }
    }

    public async Task ApplyPreferredExitFingerprintAsync(string fingerprint, string relayName, CancellationToken token)
    {
        var normalized = NormalizeExitNodeFingerprint(fingerprint);
        var relayDisplayName = string.IsNullOrWhiteSpace(relayName)
            ? BuildFingerprintSummary(normalized)
            : relayName.Trim();

        if (string.IsNullOrWhiteSpace(normalized) || !ExitFingerprintRegex.IsMatch(normalized))
        {
            StatusMessage = "Selected relay fingerprint is invalid.";
            AppendLog($"Preferred exit was not applied because relay '{relayDisplayName}' has an invalid fingerprint.");
            return;
        }

        ExitNodeFingerprint = normalized;
        StrictManualExitNodeFingerprint = true;
        SelectedLocation = AutomaticLocationLabel;

        var fingerprintSummary = BuildFingerprintSummary(normalized);
        if (!IsConnected)
        {
            StatusMessage = $"Preferred exit set to {relayDisplayName}. It will apply on the next connection.";
            AppendLog($"Preferred exit set to {relayDisplayName} ({fingerprintSummary}). Reconnect to use it.");
            return;
        }

        try
        {
            var liveApplied = await _client
                .ChangeExitFingerprintAsync(normalized, strict: true, token)
                .ConfigureAwait(true);

            if (liveApplied)
            {
                StatusMessage = $"Preferred exit set to {relayDisplayName}. Requested a new circuit.";
                AppendLog($"Preferred exit set to {relayDisplayName} ({fingerprintSummary}).");
                return;
            }

            StatusMessage = $"Preferred exit set to {relayDisplayName}. Reconnect if the live circuit did not change.";
            AppendLog($"Preferred exit set to {relayDisplayName} ({fingerprintSummary}); live update was not available.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preferred exit saved, but live update failed: {ex.Message}";
            AppendLog($"Preferred exit live update failed: {ex.Message}");
        }
    }

    public async Task ApplyPreferredGuardFingerprintAsync(string fingerprint, string relayName, CancellationToken token)
    {
        var normalized = NormalizeExitNodeFingerprint(fingerprint);
        var relayDisplayName = string.IsNullOrWhiteSpace(relayName)
            ? BuildFingerprintSummary(normalized)
            : relayName.Trim();

        if (string.IsNullOrWhiteSpace(normalized) || !ExitFingerprintRegex.IsMatch(normalized))
        {
            StatusMessage = "Selected guard relay fingerprint is invalid.";
            AppendLog($"Preferred guard was not applied because relay '{relayDisplayName}' has an invalid fingerprint.");
            return;
        }

        EntryNodeFingerprint = normalized;
        StrictManualEntryNodeFingerprint = true;
        SelectedEntryLocation = AutomaticLocationLabel;

        var fingerprintSummary = BuildFingerprintSummary(normalized);
        if (!IsConnected)
        {
            StatusMessage = $"Preferred guard set to {relayDisplayName}. It will apply on the next connection.";
            AppendLog($"Preferred guard set to {relayDisplayName} ({fingerprintSummary}). Reconnect to use it.");
            return;
        }

        try
        {
            var liveApplied = await _client
                .ChangeEntryFingerprintAsync(normalized, strict: true, token)
                .ConfigureAwait(true);

            if (liveApplied)
            {
                StatusMessage = $"Preferred guard set to {relayDisplayName}. Requested a new circuit.";
                AppendLog($"Preferred guard set to {relayDisplayName} ({fingerprintSummary}).");
                return;
            }

            StatusMessage = $"Preferred guard set to {relayDisplayName}. Reconnect if the live circuit did not change.";
            AppendLog($"Preferred guard set to {relayDisplayName} ({fingerprintSummary}); live update was not available.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preferred guard saved, but live update failed: {ex.Message}";
            AppendLog($"Preferred guard live update failed: {ex.Message}");
        }
    }

    partial void OnUseHybridRoutingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseKillSwitch));
        OnPropertyChanged(nameof(CanConfigureSplitTunneling));
    }

    partial void OnSelectedDnsProviderChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomDoh));
    }

    partial void OnTorIpv6ModeChanged(string value)
    {
        SelectedTorIpv6ModeOption = TorOptionModeOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))
                                    ?? TorOptionModeOptions.FirstOrDefault();
    }

    partial void OnHardwareAccelerationModeChanged(string value)
    {
        SelectedHardwareAccelerationModeOption = TorOptionModeOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))
                                                 ?? TorOptionModeOptions.FirstOrDefault();
    }

    partial void OnConnectionPaddingModeChanged(string value)
    {
        SelectedConnectionPaddingModeOption = ConnectionPaddingModeOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))
                                              ?? ConnectionPaddingModeOptions.FirstOrDefault();
    }

    partial void OnMaxCircuitInactivityMinutesChanged(int value)
    {
        var clamped = Math.Clamp(value, 5, 120);
        if (clamped != value)
        {
            MaxCircuitInactivityMinutes = clamped;
        }
    }

    partial void OnEnableDiscordStatusChanged(bool value)
    {
        _discordPresence.SetEnabled(value, AppendLog);
        var exitLabel = SelectedLocationOption?.Label
                        ?? LocationOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedLocation, StringComparison.Ordinal))?.Label
                        ?? LocalizationService.Get("Home.Automatic");
        _discordPresence.Update(IsConnected, exitLabel, AppendLog);
    }

    partial void OnSelectedTorIpv6ModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(TorIpv6Mode, value.Value, StringComparison.Ordinal))
        {
            TorIpv6Mode = value.Value;
        }
    }

    partial void OnSelectedHardwareAccelerationModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(HardwareAccelerationMode, value.Value, StringComparison.Ordinal))
        {
            HardwareAccelerationMode = value.Value;
        }
    }

    partial void OnSelectedConnectionPaddingModeOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            return;
        }

        if (!string.Equals(ConnectionPaddingMode, value.Value, StringComparison.Ordinal))
        {
            ConnectionPaddingMode = value.Value;
        }
    }

    partial void OnUseNativeThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(UseCustomChrome));
        OnPropertyChanged(nameof(UseNativeMacChrome));
    }

    partial void OnAutoStartModeChanged(string value)
    {
        SelectedAutoStartModeOption = AutoStartModeOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
                                    ?? AutoStartModeOptions.FirstOrDefault();
        StartWithWindows = !string.Equals(value, AutoStartModeOff, StringComparison.OrdinalIgnoreCase);
        StartMinimized = string.Equals(value, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase);

        if (_loadingSettings || _disposed)
        {
            return;
        }

        UpdateAutoStartRegistration();
    }

    public Task InitializeAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        Dispatcher.UIThread.Post(StartSpeedMonitor);
        Dispatcher.UIThread.Post(StartIpAutoRefresh);

        CurrentIp = LocalizationService.Get("Status.Resolving");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var countries = await _nodeDatabaseService.GetCountryStatsAsync(AppendLog, CancellationToken.None).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => ApplyCountryStats(countries));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Country DB update failed: {ex.Message}"));
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                await _client.RefreshIpAsync(updateStatusMessage: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Startup IP lookup failed: {ex.Message}"));
            }
        });

        _ = Task.Run(() =>
        {
            try
            {
                _client.LoadCachedBridgeMetadata();
                var bridgeTypes = _client.GetBridgeTypes();
                Dispatcher.UIThread.Post(() =>
                {
                    BridgeTypes.Clear();
                    foreach (var type in bridgeTypes)
                    {
                        BridgeTypes.Add(type);
                    }

                    if (!BridgeTypes.Any(type => string.Equals(type, BridgeTypeAutomatic, StringComparison.OrdinalIgnoreCase)))
                    {
                        BridgeTypes.Insert(0, BridgeTypeAutomatic);
                    }

                    if (!BridgeTypes.Any(type => string.Equals(type, BridgeTypeVanilla, StringComparison.OrdinalIgnoreCase)))
                    {
                        BridgeTypes.Insert(Math.Min(1, BridgeTypes.Count), BridgeTypeVanilla);
                    }

                    RefreshBridgeTypeOptions();

                    var recommended = _client.GetRecommendedBridgeType();
                    if (!BridgeTypes.Contains(SelectedBridgeType) &&
                        !string.IsNullOrWhiteSpace(recommended) &&
                        BridgeTypes.Contains(recommended))
                    {
                        SelectedBridgeType = recommended;
                    }
                    else if (!BridgeTypes.Contains(SelectedBridgeType))
                    {
                        SelectedBridgeType = BridgeTypes.FirstOrDefault() ?? "obfs4";
                    }
                });

                if (AutoConnect)
                {
                    Dispatcher.UIThread.Post(async () => await ConnectAsync());
                }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Initialization failed: {ex.Message}"));
            }
        });

        StartV3BackgroundTasks();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PropertyChanged -= OnAnyPropertyChanged;

        try
        {
            if (_speedTimer != null)
            {
                _speedTimer.Stop();
                _speedTimer.Tick -= OnSpeedTimerTick;
                _speedTimer = null;
            }
        }
        catch
        {
        }

        try
        {
            if (_ipRefreshTimer != null)
            {
                _ipRefreshTimer.Stop();
                _ipRefreshTimer = null;
            }
        }
        catch
        {
        }

        try
        {
            if (_logFlushTimer != null)
            {
                _logFlushTimer.Stop();
                _logFlushTimer.Tick -= OnLogFlushTimerTick;
                _logFlushTimer = null;
            }
        }
        catch
        {
        }

        try
        {
            if (_bridgeRefreshTimer != null)
            {
                _bridgeRefreshTimer.Stop();
                _bridgeRefreshTimer = null;
            }
        }
        catch
        {
        }

        try
        {
            StopConnectionElapsedTimer();
        }
        catch
        {
        }

        try
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;
        }
        catch
        {
        }

        try
        {
            _settingsSaveCts?.Cancel();
            _settingsSaveCts?.Dispose();
            _settingsSaveCts = null;
        }
        catch
        {
        }

        try
        {
            SaveSettings(allowDuringDispose: true);
        }
        catch
        {
        }

        _discordPresence.Dispose();
        _client.Dispose();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsDependencyDownloadInProgress)
        {
            StatusMessage = LocalizationService.Get("Status.DownloadingComponentsWait");
            return;
        }

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        if (!TryValidateConnectInputs(out var validationError))
        {
            StatusMessage = validationError;
            AppendLog(validationError);
            return;
        }

        var baseOptions = BuildConnectOptions();
        BeginConnectionPreparation(SmartConnectEnabled);

        try
        {
            UpdatePreparationStatus(
                SmartConnectEnabled
                    ? "Smart Connect is analyzing the network and preparing the best route..."
                    : "Preparing Tor connection...",
                0.03);
            var strategies = await BuildConnectStrategiesAsync(baseOptions, _connectCts.Token);
            for (var index = 0; index < strategies.Count; index++)
            {
                _connectCts.Token.ThrowIfCancellationRequested();

                var strategy = strategies[index];
                var attemptOptions = strategy.Options;

                UpdatePreparationStatus(
                    SmartConnectEnabled
                        ? $"Smart Connect selected strategy {index + 1}/{strategies.Count}: {strategy.Name}."
                        : "Checking connectivity and local Tor components...",
                    Math.Min(0.08 + (index * 0.04), 0.18));

                if (SmartConnectEnabled &&
                    attemptOptions.OnionDnsProxyEnabled &&
                    !PlatformHelper.IsAdministrator() &&
                    !OperatingSystem.IsWindows() &&
                    !OperatingSystem.IsMacOS())
                {
                    attemptOptions = attemptOptions with { OnionDnsProxyEnabled = false };
                    AppendLog("Smart Connect: disabled .onion DNS proxy for this attempt because elevated privileges are required.");
                }

                if (SmartConnectEnabled)
                {
                    AppendLog($"Smart Connect attempt {index + 1}/{strategies.Count}: {strategy.Name} ({strategy.Reason})");
                }

                if (!await EnsureAdminRequirementsForConnectAsync(attemptOptions))
                {
                    return;
                }

                OnionHopV3.Core.Services.StartupLogger.Write("ConnectAsync: Calling _client.ConnectAsync...");
                await _client.ConnectAsync(attemptOptions, _connectCts.Token);
                OnionHopV3.Core.Services.StartupLogger.Write("ConnectAsync: _client.ConnectAsync completed");

                if (IsConnected || index >= strategies.Count - 1)
                {
                    break;
                }

                if (!_connectCts.IsCancellationRequested)
                {
                    AppendLog("Smart Connect: attempt did not connect. Trying next strategy...");
                }
            }

            if (SmartConnectEnabled && !IsConnected && !_connectCts.IsCancellationRequested)
            {
                StatusMessage = "Smart Connect exhausted all fallback strategies. Try manual settings if needed.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsConnected && !IsConnecting && !IsDisconnecting)
            {
                ConnectionStatus = LocalizationService.Get("Status.Disconnected");
                StatusMessage = "Connection canceled.";
                ConnectionProgress = 0;
            }
        }
        catch (Exception ex)
        {
            OnionHopV3.Core.Services.StartupLogger.Write($"ConnectAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            OnionHopV3.Core.Services.StartupLogger.Write($"Stack trace: {ex.StackTrace}");
            ConnectionStatus = LocalizationService.Get("Status.Disconnected");
            // Full detail (which can be many lines of engine output) goes to the in-app log; the Home
            // hero only shows a short, single-line reason so it does not turn into a wall of text.
            AppendLog($"Connection failed: {ex.Message}");
            StatusMessage = BuildShortConnectionError(ex.Message);
            ConnectionProgress = 0;
        }
        finally
        {
            IsPreparingConnection = false;
        }
    }

    // Collapse a possibly multi-line, engine-dumped error into one short line for the Home status.
    // The complete text is always written to the log; this just keeps the hero readable.
    private static string BuildShortConnectionError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Connection failed. See the Logs tab for details.";
        }

        var firstLine = raw
            .Split('\n', '\r')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? raw.Trim();

        // Some engines append a "Recent output:" dump after the reason; drop it from the hero line.
        foreach (var marker in new[] { "Recent output:", " output:" })
        {
            var idx = firstLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                firstLine = firstLine[..idx].TrimEnd(' ', '.', ':');
                break;
            }
        }

        const int maxLength = 200;
        if (firstLine.Length > maxLength)
        {
            firstLine = firstLine[..maxLength].TrimEnd() + "...";
        }

        return $"Connection failed: {firstLine}";
    }

    private void BeginConnectionPreparation(bool smartConnect)
    {
        IsPreparingConnection = true;
        ConnectionStatus = LocalizationService.Get("Status.Connecting");
        StatusMessage = smartConnect
            ? "Smart Connect is preparing a connection strategy..."
            : "Preparing Tor connection...";
        ConnectionProgress = 0.02;
    }

    private void UpdatePreparationStatus(string message, double progress)
    {
        if (!IsPreparingConnection || IsConnecting || IsConnected || IsDisconnecting)
        {
            return;
        }

        ConnectionStatus = LocalizationService.Get("Status.Connecting");
        StatusMessage = message;
        ConnectionProgress = Math.Max(ConnectionProgress, progress);
    }

    private async Task<IReadOnlyList<SmartConnectAdvisor.Strategy>> BuildConnectStrategiesAsync(
        OnionHopConnectOptions baseOptions,
        CancellationToken token)
    {
        if (!SmartConnectEnabled)
        {
            return [new SmartConnectAdvisor.Strategy("manual", "Smart Connect disabled.", baseOptions)];
        }

        try
        {
            var plan = await _smartConnectAdvisor.BuildPlanAsync(baseOptions, AppendLog, token);
            if (plan.Strategies.Count > 0)
            {
                return plan.Strategies;
            }

            AppendLog("Smart Connect planner returned no strategies. Falling back to generic profile.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"Smart Connect planner failed: {ex.Message}. Falling back to generic profile.");
        }

        var genericFallback = SmartConnectAdvisor.BuildStrategiesForRisk(baseOptions, SmartConnectAdvisor.RiskLevel.Unknown);
        if (genericFallback.Count > 0)
        {
            return genericFallback;
        }

        return [new SmartConnectAdvisor.Strategy("manual", "Fallback to current settings.", baseOptions)];
    }

    private async Task<bool> EnsureAdminRequirementsForConnectAsync(OnionHopConnectOptions options)
    {
        var needsAdmin = options.OnionDnsProxyEnabled || IsTunModeOption(options);
        if (!needsAdmin || PlatformHelper.IsAdministrator())
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            StatusMessage = LocalizationService.Get("Status.AdminRequiredRequesting");

            if (needsAdmin)
            {
                OnionHopV3.Core.Services.StartupLogger.Write("ConnectAsync: Calling EnsureAdminHelperAsync...");
                if (!await _client.EnsureAdminHelperAsync())
                {
                    OnionHopV3.Core.Services.StartupLogger.Write("ConnectAsync: EnsureAdminHelperAsync returned false");
                    StatusMessage = LocalizationService.Get("Status.AdminRequiredCanceled");
                    return false;
                }

                OnionHopV3.Core.Services.StartupLogger.Write("ConnectAsync: EnsureAdminHelperAsync succeeded.");
            }

            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            var usesTunMode = IsTunModeOption(options);
            var usesNetworkExtension = usesTunMode && _client.CanUseMacNetworkExtension();
            var needsMacAdmin = options.OnionDnsProxyEnabled || (usesTunMode && !usesNetworkExtension);

            if (!needsMacAdmin)
            {
                if (usesNetworkExtension)
                {
                    AppendLog("Using configured macOS Network Extension profile for TUN mode (no admin prompt required before connect).");
                }

                return true;
            }

            var adminReason = usesTunMode && options.OnionDnsProxyEnabled
                ? "tunnel and .onion DNS setup"
                : usesTunMode
                    ? "tunnel setup"
                    : ".onion DNS setup";

            AppendLog($"macOS will request administrator privileges before starting {adminReason}. The GUI stays in the normal user session.");
            return true;
        }

        StatusMessage = "This mode requires root privileges on this platform. Please relaunch OnionHop with elevated permissions.";
        return false;
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        await _client.DisconnectAsync();
    }

    // Runtime state for the "System Proxy ON/OFF" toggle. SystemProxyEnabled reflects whether
    // the system proxy currently points at Tor; CanToggleSystemProxy gates the button so it only
    // shows when toggling is meaningful (connected, Proxy Mode, system scope). Both are synced
    // from the client in ApplyClientStatus and are NOT persisted settings.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemProxyButtonText))]
    private bool _systemProxyEnabled;
    [ObservableProperty] private bool _canToggleSystemProxy;

    public string SystemProxyButtonText => SystemProxyEnabled
        ? LocalizationService.Get("Home.SystemProxyOn")
        : LocalizationService.Get("Home.SystemProxyOff");

    // The system proxy only means something in Proxy Mode with a system scope. It does nothing in
    // TUN/VPN mode (the OS-level tunnel already captures all traffic) or in local-only scope (no
    // system proxy is ever installed), so the button is hidden there.
    public bool IsSystemProxyScope => !string.Equals(ProxyScopeMode, ProxyScopeLocalOnly, StringComparison.Ordinal);
    public bool ShowSystemProxyButton => !IsTunMode && IsSystemProxyScope;

    // Enabled when there is something to do: live-toggle while connected in a system-scope Proxy
    // Mode, or pre-set the desired post-connect behavior while disconnected.
    private bool ComputeCanToggleSystemProxy()
    {
        if (IsTunMode || !IsSystemProxyScope)
        {
            return false;
        }

        return _client.CanToggleSystemProxy || (!IsConnected && !IsConnecting);
    }

    [RelayCommand]
    private void ToggleSystemProxy()
    {
        if (IsTunMode || !IsSystemProxyScope)
        {
            return;
        }

        if (_client.CanToggleSystemProxy)
        {
            // Connected in a system-scope Proxy Mode: flip the live OS proxy without touching Tor.
            _client.SetSystemProxyEnabled(!_client.IsSystemProxyEnabled);
            SystemProxyEnabled = _client.IsSystemProxyEnabled;
        }
        else
        {
            // Disconnected (or not live-toggleable): record the desired post-connect behavior. It is
            // applied on the next connect via OnionHopConnectOptions.ApplySystemProxyOnConnect.
            SystemProxyEnabled = !SystemProxyEnabled;
        }

        SaveSettings();
        CanToggleSystemProxy = ComputeCanToggleSystemProxy();
    }

    // --- Snowflake proxy (volunteer as a Snowflake bridge) ------------------------------------
    // SnowflakeProxyAutoStart + SnowflakeProxyCapacity are persisted settings; the rest is runtime
    // status. The on/off toggle is intentionally NOT persisted (off each session unless auto-start).
    private bool _suppressSnowflakeToggle;

    [ObservableProperty] private bool _snowflakeProxyEnabled;
    [ObservableProperty] private bool _snowflakeProxyAutoStart;
    [ObservableProperty] private int _snowflakeProxyCapacity;
    [ObservableProperty] private bool _snowflakeProxyRunning;
    [ObservableProperty] private string _snowflakeProxyNatType = "unknown";
    [ObservableProperty] private long _snowflakeProxyConnectionsServed;
    [ObservableProperty] private string _snowflakeProxyStatusText = string.Empty;

    partial void OnSnowflakeProxyEnabledChanged(bool value)
    {
        if (_suppressSnowflakeToggle || _loadingSettings)
        {
            return;
        }

        _ = ToggleSnowflakeProxyAsync(value);
    }

    private async Task ToggleSnowflakeProxyAsync(bool enable)
    {
        try
        {
            if (enable)
            {
                var capacity = SnowflakeProxyCapacity > 0 ? (uint)SnowflakeProxyCapacity : 0u;
                var started = await _client.StartSnowflakeProxyAsync(capacity, CancellationToken.None).ConfigureAwait(true);
                if (!started)
                {
                    _suppressSnowflakeToggle = true;
                    SnowflakeProxyEnabled = false;
                    _suppressSnowflakeToggle = false;
                    SnowflakeProxyStatusText = LocalizationService.Get("Snowflake.StatusUnavailable");
                }
            }
            else
            {
                _client.StopSnowflakeProxy();
            }
        }
        catch
        {
        }
    }

    private void ApplySnowflakeProxyStatus(OnionHopV3.Core.Services.SnowflakeProxyStatus status)
    {
        SnowflakeProxyRunning = status.IsRunning;
        SnowflakeProxyNatType = status.NatType;
        SnowflakeProxyConnectionsServed = status.ConnectionsServed;
        SnowflakeProxyStatusText = BuildSnowflakeStatusText(status);

        if (SnowflakeProxyEnabled != status.IsRunning)
        {
            _suppressSnowflakeToggle = true;
            SnowflakeProxyEnabled = status.IsRunning;
            _suppressSnowflakeToggle = false;
        }
    }

    private static string BuildSnowflakeStatusText(OnionHopV3.Core.Services.SnowflakeProxyStatus status)
    {
        if (!status.IsRunning)
        {
            return LocalizationService.Get("Snowflake.StatusStopped");
        }

        var nat = string.IsNullOrWhiteSpace(status.NatType) ? "unknown" : status.NatType;
        var text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationService.Get("Snowflake.StatusRunning"),
            nat,
            status.ConnectionsServed);
        if (!string.IsNullOrWhiteSpace(status.TrafficSummary))
        {
            text += $" — {status.TrafficSummary}";
        }

        return text;
    }

    private void StartSnowflakeProxyIfAutoStart()
    {
        if (!SnowflakeProxyAutoStart)
        {
            return;
        }

        _suppressSnowflakeToggle = true;
        SnowflakeProxyEnabled = true;
        _suppressSnowflakeToggle = false;
        _ = ToggleSnowflakeProxyAsync(true);
    }

    [RelayCommand]
    private void CancelConnect()
    {
        if (!IsPreparingConnection && !IsConnecting)
        {
            return;
        }

        StatusMessage = LocalizationService.Get("Status.CancelingConnect");
        _connectCts?.Cancel();
    }

    [RelayCommand]
    private async Task RefreshIpAsync()
    {
        await _client.RefreshIpAsync(updateStatusMessage: true, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshBridgeDataAsync()
    {
        if (_disposed || IsBridgeDataUpdateInProgress)
        {
            return;
        }

        IsBridgeDataUpdateInProgress = true;
        try
        {
            StatusMessage = "Updating Tor bridge data...";
            var result = await _client.RefreshBridgeDistributionAsync(BuildConnectOptions(), CancellationToken.None);
            LastBridgeDataUpdateUtc = result.LastUpdatedUtc;

            if (result.UpdatedTypes > 0)
            {
                StatusMessage = "Tor bridge data updated.";
            }
            else if (result.AttemptedTypes > 0)
            {
                StatusMessage = "Bridge data refresh finished with no new usable bridges.";
            }
            else
            {
                StatusMessage = "Bridge data refresh skipped.";
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Bridge data refresh failed: {ex.Message}");
            StatusMessage = $"Bridge data refresh failed: {ex.Message}";
        }
        finally
        {
            IsBridgeDataUpdateInProgress = false;
        }
    }

    [RelayCommand]
    private async Task ChangeIdentityAsync()
    {
        if (await _client.ChangeIdentityAsync(CancellationToken.None))
        {
            RegisterSessionIdentityChange();
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        if (_disposed)
        {
            return;
        }

        _loadingSettings = true;
        try
        {
            AutoConnect = false;
            AutoStartMode = AutoStartModeOff;
            MinimizeToTray = false;
            AutoUpdate = false;

            KillSwitchEnabled = false;
            UseHybridRouting = false;
            SmartConnectEnabled = true;

            SelectedLocation = AutomaticLocationLabel;
            SelectedEntryLocation = AutomaticLocationLabel;
            EntryNodeFingerprint = string.Empty;
            MiddleNodeFingerprint = string.Empty;
            ExitNodeFingerprint = string.Empty;
            SelectedConnectionMode = ConnectionModeProxy;

            UseTorBridges = false;
            UseCensoredMode = false;
            SelectedBridgeType = "obfs4";
            BridgeSourceMode = BridgeSourceAuto;
            CustomBridges = string.Empty;
            CustomSniHosts = string.Empty;
            UseSnowflakeAmp = false;
            SnowflakeAmpCache = string.Empty;

            TorIpv6Mode = OnionHopConnectOptions.ToggleModeDefault;
            HardwareAccelerationMode = OnionHopConnectOptions.ToggleModeDefault;
            ConnectionPaddingMode = OnionHopConnectOptions.ConnectionPaddingAuto;

            SelectedDnsProvider = DnsProviderCloudflare;
            CustomDohHost = string.Empty;
            CustomDohPath = "/dns-query";
            ProxyScopeMode = ProxyScopeSystem;
            PreferredSocksPort = OnionHopConnectOptions.DefaultSocksPort.ToString();
            PreferredHttpPort = OnionHopConnectOptions.DefaultHttpPort.ToString();
            AllowLanProxyAccess = false;
            TunCoreMode = TunCoreSingBox;
            TunStackMode = IsMacOS ? TunStackSystem : TunStackMixed;
            TunMtu = string.Empty;
            TunStrictRoute = true;
            ConnectionTimeoutSeconds = string.Empty;
            RestrictedFirewallMode = false;
            AllowedPorts = DefaultAllowedPorts;
            OnionDnsProxyEnabled = false;
            StrictManualEntryNodeFingerprint = true;
            StrictManualMiddleNodeFingerprint = true;
            StrictManualExitNodeFingerprint = true;
            ShowAdvancedHomeConnectionDetails = false;
            MaxCircuitInactivityMinutes = 10;
            OpenConnectedPageEnabled = false;
            ConnectedPageUrl = DefaultConnectedPageUrl;
            OpenDisconnectedPageEnabled = false;
            DisconnectedPageUrl = DefaultDisconnectedPageUrl;
            EnableDiscordStatus = false;

            HybridRouteAllWebTraffic = true;
            HybridBlockQuicForTorApps = true;
            BlockUdpTraffic = true;
            HybridTorApps = string.Empty;
            HybridBypassApps = string.Empty;

            ThemeMode = ThemeModeSystem;
            UseNativeTheme = false;
            ResetV3Settings();
        }
        finally
        {
            _loadingSettings = false;
        }

        UpdateAutoStartRegistration();

        ApplyTheme();
        SaveSettings();
        StatusMessage = LocalizationService.Get("Status.DefaultsRestored");
    }

    private bool TryValidateConnectInputs(out string error)
    {
        error = string.Empty;

        if (!SmartConnectEnabled && IsManualExitNodeFingerprintSet && !ExitFingerprintRegex.IsMatch(ExitNodeFingerprint))
        {
            error = "Manual exit fingerprint must be exactly 40 hexadecimal characters.";
            return false;
        }

        if (!SmartConnectEnabled && IsManualEntryNodeFingerprintSet && !ExitFingerprintRegex.IsMatch(EntryNodeFingerprint))
        {
            error = "Manual guard fingerprint must be exactly 40 hexadecimal characters.";
            return false;
        }

        if (!SmartConnectEnabled && IsManualMiddleNodeFingerprintSet && !ExitFingerprintRegex.IsMatch(MiddleNodeFingerprint))
        {
            error = "Manual middle fingerprint must be exactly 40 hexadecimal characters.";
            return false;
        }

        if (!TryParsePreferredProxyPort(PreferredSocksPort, out var socksPort))
        {
            error = "Preferred SOCKS port is invalid. Use a port between 1 and 65535.";
            return false;
        }

        if (!TryParsePreferredProxyPort(PreferredHttpPort, out var httpPort))
        {
            error = "Preferred HTTP port is invalid. Use a port between 1 and 65535.";
            return false;
        }

        if (socksPort == httpPort)
        {
            error = "Preferred SOCKS and HTTP ports must be different.";
            return false;
        }

        if (!TryParseTunMtu(TunMtu, out _))
        {
            error = "TUN MTU is invalid. Leave empty for default, or set a value between 576 and 9000.";
            return false;
        }

        if (!TryParseConnectionTimeoutSeconds(ConnectionTimeoutSeconds, out _))
        {
            error = "Connection timeout is invalid. Leave empty for automatic, use 0 to disable, or set 1-3600 seconds.";
            return false;
        }

        return true;
    }

    private void UpdateAutoStartRegistration()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                LinuxAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                MacAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to update startup registration: {ex.Message}");
        }
    }

    private static bool TryParsePreferredProxyPort(string raw, out int port)
    {
        port = 0;
        if (!int.TryParse(raw, out var parsed))
        {
            return false;
        }

        if (parsed is < 1 or > 65535)
        {
            return false;
        }

        port = parsed;
        return true;
    }

    private static int ParsePreferredProxyPort(string raw, int fallback)
    {
        return TryParsePreferredProxyPort(raw, out var port) ? port : fallback;
    }

    private static bool TryParseTunMtu(string raw, out int? mtu)
    {
        mtu = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, out var parsed))
        {
            return false;
        }

        if (parsed is < 576 or > 9000)
        {
            return false;
        }

        mtu = parsed;
        return true;
    }

    private static int? ParseTunMtu(string raw)
    {
        return TryParseTunMtu(raw, out var mtu) ? mtu : null;
    }

    private static bool TryParseConnectionTimeoutSeconds(string raw, out int? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, out var parsed))
        {
            return false;
        }

        if (parsed is < 0 or > 3600)
        {
            return false;
        }

        seconds = parsed;
        return true;
    }

    private static int? ParseConnectionTimeoutSeconds(string raw)
    {
        return TryParseConnectionTimeoutSeconds(raw, out var seconds) ? seconds : null;
    }

    private OnionHopConnectOptions BuildConnectOptions()
    {
        return new OnionHopConnectOptions
        {
            SelectedLocation = SelectedLocation,
            SelectedEntryLocation = SelectedEntryLocation,
            EntryNodeFingerprint = EntryNodeFingerprint,
            MiddleNodeFingerprint = MiddleNodeFingerprint,
            ExitNodeFingerprint = ExitNodeFingerprint,
            SelectedConnectionMode = SelectedConnectionMode,
            TorEngineMode = SelectedTorEngineMode,
            UseHybridRouting = UseHybridRouting,
            KillSwitchEnabled = KillSwitchEnabled,
            UseTorBridges = UseTorBridges,
            UseCensoredMode = UseCensoredMode,
            SelectedBridgeType = SelectedBridgeType,
            BridgeSourceMode = BridgeSourceMode,
            CustomBridges = CustomBridges,
            CustomSniHosts = CustomSniHosts,
            UseSnowflakeAmp = UseSnowflakeAmp,
            SnowflakeAmpCache = SnowflakeAmpCache,
            TorIpv6Mode = TorIpv6Mode,
            HardwareAccelerationMode = HardwareAccelerationMode,
            ConnectionPaddingMode = ConnectionPaddingMode,
            SelectedDnsProvider = SelectedDnsProvider,
            CustomDohHost = CustomDohHost,
            CustomDohPath = CustomDohPath,
            ProxyScopeMode = ProxyScopeMode,
            ApplySystemProxyOnConnect = SystemProxyEnabled,
            PreferredSocksPort = ParsePreferredProxyPort(PreferredSocksPort, OnionHopConnectOptions.DefaultSocksPort),
            PreferredHttpPort = ParsePreferredProxyPort(PreferredHttpPort, OnionHopConnectOptions.DefaultHttpPort),
            AllowLanProxyAccess = AllowLanProxyAccess,
            TunCoreMode = TunCoreMode,
            TunStackMode = IsMacOS ? TunStackSystem : TunStackMode,
            TunMtu = ParseTunMtu(TunMtu),
            TunStrictRoute = TunStrictRoute,
            ConnectionTimeoutSeconds = ParseConnectionTimeoutSeconds(ConnectionTimeoutSeconds),
            RestrictedFirewallMode = RestrictedFirewallMode,
            AllowedPorts = AllowedPorts,
            OnionDnsProxyEnabled = OnionDnsProxyEnabled || DnsLeakProtectionEnabled,
            // "DNS leak protection" now routes the whole system's DNS through Tor (Proxy Mode),
            // not just .onion. OnionDnsProxyEnabled above keeps Tor's DNSPort listening.
            FullDnsOverTor = DnsLeakProtectionEnabled,
            StrictManualEntryNodeFingerprint = StrictManualEntryNodeFingerprint,
            StrictManualMiddleNodeFingerprint = StrictManualMiddleNodeFingerprint,
            StrictManualExitNodeFingerprint = StrictManualExitNodeFingerprint,
            MaxCircuitInactivityMinutes = MaxCircuitInactivityMinutes,
            OpenConnectedPageEnabled = OpenConnectedPageEnabled,
            ConnectedPageUrl = ConnectedPageUrl,
            OpenDisconnectedPageEnabled = OpenDisconnectedPageEnabled,
            DisconnectedPageUrl = DisconnectedPageUrl,
            EnableDiscordStatus = EnableDiscordStatus,
            HybridRouteAllWebTraffic = HybridRouteAllWebTraffic,
            HybridBlockQuicForTorApps = HybridBlockQuicForTorApps,
            BlockUdpTraffic = BlockUdpTraffic,
            HybridTorApps = HybridTorApps,
            HybridBypassApps = HybridBypassApps
        };
    }

    private static bool IsTunModeOption(OnionHopConnectOptions options)
    {
        return string.Equals(options.SelectedConnectionMode, ConnectionModeTun, StringComparison.Ordinal);
    }

    private void ApplyClientStatus(OnionHopClient.StatusUpdate update)
    {
        var previouslyConnected = _hasStatusSnapshot && _wasConnected;

        IsConnecting = update.IsConnecting;
        IsConnected = update.IsConnected;
        IsDisconnecting = update.IsDisconnecting;
        ConnectionStatus = LocalizeRuntimeText(update.ConnectionStatus);
        StatusMessage = LocalizeRuntimeText(update.StatusMessage);
        ConnectionProgress = update.ConnectionProgress;
        CurrentIp = update.CurrentIp;
        SocksProxyPort = update.SocksPort.ToString();
        HttpProxyPort = update.HttpPort.HasValue ? update.HttpPort.Value.ToString() : "--";
        // While connected, mirror the live OS proxy state. While disconnected, keep the user's
        // desired (persisted) value so the Home toggle shows what will happen on the next connect.
        if (update.IsConnected)
        {
            SystemProxyEnabled = _client.IsSystemProxyEnabled;
        }
        CanToggleSystemProxy = ComputeCanToggleSystemProxy();
        var latestBridgeDataUpdate = _client.GetLastBridgeDataUpdateUtc();
        if (latestBridgeDataUpdate != LastBridgeDataUpdateUtc)
        {
            LastBridgeDataUpdateUtc = latestBridgeDataUpdate;
        }

        _hasStatusSnapshot = true;
        _wasConnected = IsConnected;

        if (!previouslyConnected && IsConnected && OpenConnectedPageEnabled)
        {
            OpenLaunchPage(ConnectedPageUrl);
        }
        else if (previouslyConnected && !IsConnected && OpenDisconnectedPageEnabled)
        {
            OpenLaunchPage(DisconnectedPageUrl);
        }

        ApplyV3ClientStatus(previouslyConnected, update);
        var exitLabel = SelectedLocationOption?.Label
                        ?? LocationOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedLocation, StringComparison.Ordinal))?.Label
                        ?? LocalizationService.Get("Home.Automatic");
        _discordPresence.Update(IsConnected, exitLabel, AppendLog);
    }

    private void ApplyDependencyUpdate(OnionHopClient.DependencyUpdate update)
    {
        IsDependencyDownloadInProgress = update.InProgress;
        DependencyDownloadStatus = LocalizeRuntimeText(update.Status);
        DependencyDownloadProgress = update.Progress;
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingSettings || _disposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        if (SettingsProperties.Contains(e.PropertyName))
        {
            ScheduleSave();
        }

        if (e.PropertyName == nameof(ThemeMode) || e.PropertyName == nameof(UseNativeTheme) || e.PropertyName == nameof(IsDarkMode))
        {
            ApplyTheme();
        }

        HandleV3PropertyChange(e.PropertyName);
    }

    private void ScheduleSave()
    {
        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();

        var cts = new CancellationTokenSource();
        _settingsSaveCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // SaveSettings reads UI-thread-only state (e.g. Application.ActualThemeVariant),
                // so the debounce waits on a background thread but the save itself must run on the
                // UI thread — otherwise we get "Call from invalid thread" (seen when toggling TUN mode).
                await Dispatcher.UIThread.InvokeAsync(() => SaveSettings());
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Settings save failed: {ex.Message}"));
            }
        }, token);
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = _settingsService.Load();
            if (settings == null)
            {
                return;
            }

            AutoConnect = settings.AutoConnect;
            AutoStartMode = ResolveAutoStartMode(settings);
            MinimizeToTray = settings.MinimizeToTray;
            AutoUpdate = settings.AutoUpdate;
            KillSwitchEnabled = settings.KillSwitchEnabled;
            ThemeMode = string.IsNullOrWhiteSpace(settings.ThemeMode)
                ? (settings.IsDarkMode ? ThemeModeDark : ThemeModeLight)
                : NormalizeThemeMode(settings.ThemeMode);
            UseNativeTheme = settings.UiSchemaVersion >= 3 && settings.UseNativeTheme;

            SelectedLocation = string.IsNullOrWhiteSpace(settings.SelectedLocation)
                ? AutomaticLocationLabel
                : ResolveLocationCodeFromSelection(settings.SelectedLocation);
            if (string.IsNullOrWhiteSpace(SelectedLocation))
            {
                SelectedLocation = AutomaticLocationLabel;
            }

            SelectedEntryLocation = string.IsNullOrWhiteSpace(settings.SelectedEntryLocation)
                ? AutomaticLocationLabel
                : ResolveLocationCodeFromSelection(settings.SelectedEntryLocation);
            if (string.IsNullOrWhiteSpace(SelectedEntryLocation))
            {
                SelectedEntryLocation = AutomaticLocationLabel;
            }

            EntryNodeFingerprint = settings.EntryNodeFingerprint ?? string.Empty;
            MiddleNodeFingerprint = settings.MiddleNodeFingerprint ?? string.Empty;
            ExitNodeFingerprint = settings.ExitNodeFingerprint ?? string.Empty;

            SelectedConnectionMode = string.IsNullOrWhiteSpace(settings.SelectedConnectionMode) ? ConnectionModeProxy : settings.SelectedConnectionMode;
            if (!ConnectionModes.Contains(SelectedConnectionMode))
            {
                SelectedConnectionMode = ConnectionModeProxy;
            }

            UseHybridRouting = settings.UseHybridRouting;
            SmartConnectEnabled = settings.SmartConnectEnabled ?? true;
            UseTorBridges = settings.UseTorBridges;
            UseCensoredMode = settings.UseCensoredMode;
            SelectedBridgeType = string.IsNullOrWhiteSpace(settings.SelectedBridgeType)
                ? SelectedBridgeType
                : settings.SelectedBridgeType!.Trim().ToLowerInvariant();
            BridgeSourceMode = NormalizeBridgeSourceMode(settings.BridgeSourceMode);
            CustomBridges = settings.CustomBridges ?? string.Empty;
            CustomSniHosts = settings.CustomSniHosts ?? string.Empty;
            UseSnowflakeAmp = settings.UseSnowflakeAmp;
            SnowflakeAmpCache = settings.SnowflakeAmpCache ?? string.Empty;

            TorIpv6Mode = string.IsNullOrWhiteSpace(settings.TorIpv6Mode)
                ? OnionHopConnectOptions.ToggleModeDefault
                : settings.TorIpv6Mode;
            if (!TorOptionModes.Contains(TorIpv6Mode))
            {
                TorIpv6Mode = OnionHopConnectOptions.ToggleModeDefault;
            }

            HardwareAccelerationMode = string.IsNullOrWhiteSpace(settings.HardwareAccelerationMode)
                ? OnionHopConnectOptions.ToggleModeDefault
                : settings.HardwareAccelerationMode;
            if (!TorOptionModes.Contains(HardwareAccelerationMode))
            {
                HardwareAccelerationMode = OnionHopConnectOptions.ToggleModeDefault;
            }

            ConnectionPaddingMode = string.IsNullOrWhiteSpace(settings.ConnectionPaddingMode)
                ? OnionHopConnectOptions.ConnectionPaddingAuto
                : settings.ConnectionPaddingMode;
            if (!ConnectionPaddingModes.Contains(ConnectionPaddingMode))
            {
                ConnectionPaddingMode = OnionHopConnectOptions.ConnectionPaddingAuto;
            }

            SelectedDnsProvider = string.IsNullOrWhiteSpace(settings.SelectedDnsProvider) ? DnsProviderCloudflare : settings.SelectedDnsProvider;
            if (!DnsProviders.Contains(SelectedDnsProvider))
            {
                SelectedDnsProvider = DnsProviderCloudflare;
            }

            CustomDohHost = settings.CustomDohHost ?? string.Empty;
            CustomDohPath = string.IsNullOrWhiteSpace(settings.CustomDohPath) ? "/dns-query" : settings.CustomDohPath;
            ProxyScopeMode = NormalizeProxyScopeMode(settings.ProxyScopeMode);
            // Desired post-connect system-proxy state (defaults to enabled, matching prior behavior).
            SystemProxyEnabled = settings.SystemProxyEnabledByDefault ?? true;
            PreferredSocksPort = (settings.PreferredSocksPort is >= 1 and <= 65535
                ? settings.PreferredSocksPort.Value
                : OnionHopConnectOptions.DefaultSocksPort).ToString();
            PreferredHttpPort = (settings.PreferredHttpPort is >= 1 and <= 65535
                ? settings.PreferredHttpPort.Value
                : OnionHopConnectOptions.DefaultHttpPort).ToString();
            AllowLanProxyAccess = settings.AllowLanProxyAccess;
            TunCoreMode = NormalizeTunCoreMode(settings.TunCoreMode);
            TunStackMode = NormalizeTunStackMode(settings.TunStackMode);
            if (IsMacOS)
            {
                TunStackMode = TunStackSystem;
            }
            TunMtu = settings.TunMtu is >= 576 and <= 9000
                ? settings.TunMtu.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            TunStrictRoute = settings.TunStrictRoute ?? true;
            ConnectionTimeoutSeconds = settings.ConnectionTimeoutSeconds switch
            {
                null => string.Empty,
                < 0 => string.Empty,
                > 3600 => "3600",
                _ => settings.ConnectionTimeoutSeconds.Value.ToString(CultureInfo.InvariantCulture)
            };
            RestrictedFirewallMode = settings.RestrictedFirewallMode;
            AllowedPorts = string.IsNullOrWhiteSpace(settings.AllowedPorts) ? DefaultAllowedPorts : settings.AllowedPorts;
            OnionDnsProxyEnabled = settings.OnionDnsProxyEnabled;
            StrictManualEntryNodeFingerprint = settings.StrictManualEntryNodeFingerprint ?? true;
            StrictManualMiddleNodeFingerprint = settings.StrictManualMiddleNodeFingerprint ?? true;
            StrictManualExitNodeFingerprint = settings.StrictManualExitNodeFingerprint ?? true;
            ShowAdvancedHomeConnectionDetails = settings.ShowAdvancedHomeConnectionDetails;
            MaxCircuitInactivityMinutes = settings.MaxCircuitInactivityMinutes ?? 10;
            OpenConnectedPageEnabled = settings.OpenConnectedPageEnabled;
            ConnectedPageUrl = string.IsNullOrWhiteSpace(settings.ConnectedPageUrl) ? DefaultConnectedPageUrl : settings.ConnectedPageUrl;
            OpenDisconnectedPageEnabled = settings.OpenDisconnectedPageEnabled;
            DisconnectedPageUrl = string.IsNullOrWhiteSpace(settings.DisconnectedPageUrl) ? DefaultDisconnectedPageUrl : settings.DisconnectedPageUrl;
            EnableDiscordStatus = settings.EnableDiscordStatus;

            HybridRouteAllWebTraffic = settings.HybridRouteAllWebTraffic ?? true;
            HybridBlockQuicForTorApps = settings.HybridBlockQuicForTorApps ?? true;
            BlockUdpTraffic = settings.BlockUdpTraffic ?? true;
            HybridTorApps = settings.HybridTorApps ?? string.Empty;
            HybridBypassApps = settings.HybridBypassApps ?? string.Empty;
            SelectedLanguage = NormalizeLanguageCode(settings.LanguageCode);
            LoadV3Settings(settings);
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void StartConnectionElapsedTimer()
    {
        _connectionStartedUtc = DateTime.UtcNow;
        ConnectionElapsed = "00:00:00";
        ShowConnectionElapsed = true;

        _connectionElapsedTimer?.Stop();
        _connectionElapsedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _connectionElapsedTimer.Tick += OnConnectionElapsedTimerTick;
        _connectionElapsedTimer.Start();
    }

    private void StopConnectionElapsedTimer()
    {
        _connectionElapsedTimer?.Stop();
        _connectionElapsedTimer = null;
        _connectionStartedUtc = null;
        ShowConnectionElapsed = false;
        ConnectionElapsed = string.Empty;
    }

    private void OnConnectionElapsedTimerTick(object? sender, EventArgs e)
    {
        if (_connectionStartedUtc is null)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _connectionStartedUtc.Value;
        ConnectionElapsed = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : elapsed.ToString(@"mm\:ss");
    }

    private void StartIpAutoRefresh()
    {
        _ipRefreshTimer?.Stop();
        _ipRefreshTimer = new DispatcherTimer
        {
            // "every few seconds" but without spamming the endpoint
            Interval = TimeSpan.FromSeconds(15)
        };

        _ipRefreshTimer.Tick += async (_, _) =>
        {
            if (_disposed || _loadingSettings)
            {
                return;
            }

            // Refresh in the background; avoid disturbing status text during connects/disconnects.
            if (IsBusy)
            {
                return;
            }

            try
            {
                await _client.RefreshIpAsync(updateStatusMessage: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Auto IP refresh failed: {ex.Message}"));
            }
        };

        _ipRefreshTimer.Start();
    }

    private void SaveSettings(bool allowDuringDispose = false)
    {
        if (_disposed && !allowDuringDispose)
        {
            return;
        }

        var startWithWindows = !string.Equals(AutoStartMode, AutoStartModeOff, StringComparison.OrdinalIgnoreCase);
        var startMinimized = string.Equals(AutoStartMode, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase);

        var settings = new UserSettings
        {
            UiSchemaVersion = 3,
            AutoConnect = AutoConnect,
            AutoStartMode = AutoStartMode,
            StartWithWindows = startWithWindows,
            StartMinimized = startMinimized,
            MinimizeToTray = MinimizeToTray,
            AutoUpdate = AutoUpdate,
            KillSwitchEnabled = KillSwitchEnabled,
            ThemeMode = ThemeMode,
            IsDarkMode = string.Equals(ThemeMode, ThemeModeDark, StringComparison.OrdinalIgnoreCase) ||
                         (string.Equals(ThemeMode, ThemeModeSystem, StringComparison.OrdinalIgnoreCase) &&
                          Application.Current?.ActualThemeVariant == ThemeVariant.Dark),
            UseNativeTheme = UseNativeTheme,
            SelectedLocation = SelectedLocation,
            SelectedEntryLocation = SelectedEntryLocation,
            EntryNodeFingerprint = EntryNodeFingerprint,
            MiddleNodeFingerprint = MiddleNodeFingerprint,
            ExitNodeFingerprint = ExitNodeFingerprint,
            SelectedConnectionMode = SelectedConnectionMode,
            UseHybridRouting = UseHybridRouting,
            SmartConnectEnabled = SmartConnectEnabled,
            UseTorBridges = UseTorBridges,
            UseCensoredMode = UseCensoredMode,
            SelectedBridgeType = SelectedBridgeType,
            BridgeSourceMode = BridgeSourceMode,
            CustomBridges = CustomBridges,
            CustomSniHosts = CustomSniHosts,
            UseSnowflakeAmp = UseSnowflakeAmp,
            SnowflakeAmpCache = SnowflakeAmpCache,
            TorIpv6Mode = TorIpv6Mode,
            HardwareAccelerationMode = HardwareAccelerationMode,
            ConnectionPaddingMode = ConnectionPaddingMode,
            SelectedDnsProvider = SelectedDnsProvider,
            CustomDohHost = CustomDohHost,
            CustomDohPath = CustomDohPath,
            ProxyScopeMode = ProxyScopeMode,
            SystemProxyEnabledByDefault = SystemProxyEnabled,
            PreferredSocksPort = ParsePreferredProxyPort(PreferredSocksPort, OnionHopConnectOptions.DefaultSocksPort),
            PreferredHttpPort = ParsePreferredProxyPort(PreferredHttpPort, OnionHopConnectOptions.DefaultHttpPort),
            AllowLanProxyAccess = AllowLanProxyAccess,
            TunCoreMode = TunCoreMode,
            TunStackMode = IsMacOS ? TunStackSystem : TunStackMode,
            TunMtu = ParseTunMtu(TunMtu),
            TunStrictRoute = TunStrictRoute,
            ConnectionTimeoutSeconds = ParseConnectionTimeoutSeconds(ConnectionTimeoutSeconds),
            RestrictedFirewallMode = RestrictedFirewallMode,
            AllowedPorts = AllowedPorts,
            OnionDnsProxyEnabled = OnionDnsProxyEnabled,
            StrictManualEntryNodeFingerprint = StrictManualEntryNodeFingerprint,
            StrictManualMiddleNodeFingerprint = StrictManualMiddleNodeFingerprint,
            StrictManualExitNodeFingerprint = StrictManualExitNodeFingerprint,
            ShowAdvancedHomeConnectionDetails = ShowAdvancedHomeConnectionDetails,
            MaxCircuitInactivityMinutes = MaxCircuitInactivityMinutes,
            OpenConnectedPageEnabled = OpenConnectedPageEnabled,
            ConnectedPageUrl = ConnectedPageUrl,
            OpenDisconnectedPageEnabled = OpenDisconnectedPageEnabled,
            DisconnectedPageUrl = DisconnectedPageUrl,
            EnableDiscordStatus = EnableDiscordStatus,
            HybridRouteAllWebTraffic = HybridRouteAllWebTraffic,
            HybridBlockQuicForTorApps = HybridBlockQuicForTorApps,
            BlockUdpTraffic = BlockUdpTraffic,
            HybridTorApps = HybridTorApps,
            HybridBypassApps = HybridBypassApps,
            LanguageCode = SelectedLanguage
        };

        SaveV3Settings(settings);
        _settingsService.Save(settings);
    }

    private static string ResolveAutoStartMode(UserSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AutoStartMode))
        {
            return settings.StartWithWindows
                ? settings.StartMinimized ? AutoStartModeMinimized : AutoStartModeOn
                : AutoStartModeOff;
        }

        if (string.Equals(settings.AutoStartMode, AutoStartModeOn, StringComparison.OrdinalIgnoreCase))
        {
            return AutoStartModeOn;
        }

        if (string.Equals(settings.AutoStartMode, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase))
        {
            return AutoStartModeMinimized;
        }

        return AutoStartModeOff;
    }

    private void ApplyTheme()
    {
        if (Application.Current == null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = ThemeMode switch
        {
            ThemeModeDark => ThemeVariant.Dark,
            ThemeModeLight => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };

        ApplyV3ThemeResources();
    }

    private void RefreshLanguageOptions()
    {
        if (LanguageOptions.Count == 0)
        {
            LanguageOptions.Add(new LocalizedOption("en", "English"));
            LanguageOptions.Add(new LocalizedOption("de", "Deutsch"));
            LanguageOptions.Add(new LocalizedOption("fr", "Français"));
            LanguageOptions.Add(new LocalizedOption("ru", "Русский"));
            LanguageOptions.Add(new LocalizedOption("zh", "中文"));
        }

        var lang = (SelectedLanguage ?? "en").Trim().ToLowerInvariant();
        var targetIndex = 0;
        for (var i = 0; i < LanguageOptions.Count; i++)
        {
            if (string.Equals(LanguageOptions[i].Value, lang, StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = i;
                break;
            }
        }

        if (SelectedLanguageIndex != targetIndex)
        {
            SelectedLanguageIndex = targetIndex;
        }

        if (targetIndex >= 0 && targetIndex < LanguageOptions.Count)
        {
            var selectedOption = LanguageOptions[targetIndex];
            if (!ReferenceEquals(SelectedLanguageOption, selectedOption))
            {
                SelectedLanguageOption = selectedOption;
            }
        }
    }

    private void RefreshLocalizedOptions()
    {
        RefreshAutoStartModeOptions();
        RefreshThemeModeOptions();
        RefreshTorAdvancedModeOptions();

        ConnectionModeOptions.Clear();
        ConnectionModeOptions.Add(new LocalizedOption(ConnectionModeProxy, LocalizationService.Get("Home.ModeProxy")));
        ConnectionModeOptions.Add(new LocalizedOption(ConnectionModeTun, LocalizationService.Get("Home.ModeTun")));
        SelectedConnectionModeOption = ConnectionModeOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedConnectionMode, StringComparison.Ordinal))
                                     ?? ConnectionModeOptions.FirstOrDefault();
        RefreshProxyScopeOptions();
        RefreshTunCoreOptions();
        RefreshTunStackOptions();

        RefreshLocationOptions();

        RefreshBridgeSourceOptions();
        RefreshBridgeTypeOptions();
        RefreshV3LocalizedOptions();
    }

    /// <summary>
    /// Rebuilds only the exit/entry location options. Called on its own when the country database
    /// loads asynchronously (<see cref="ApplyCountryStats"/>) so the engine/mode/bridge combos are
    /// NOT torn down and rebuilt — rebuilding a ComboBox's ItemsSource drops its selection (the
    /// previously selected option instance is no longer in the new list), which left those combos
    /// blank. Only the location combos rebuild here, and their selection is re-asserted.
    /// </summary>
    private void RefreshLocationOptions()
    {
        LocationOptions.Clear();
        foreach (var location in Locations)
        {
            LocationOptions.Add(new LocalizedOption(location, GetLocationLabel(location)));
        }

        ReselectLocationOptions();

        // The ComboBox writes its selection back to null when its ItemsSource is reset, and that
        // write-back can arrive after this method returns. Re-assert on a later dispatcher pass so
        // the selection sticks instead of ending up blank.
        Dispatcher.UIThread.Post(ReselectLocationOptions, DispatcherPriority.Background);
    }

    private void ReselectLocationOptions()
    {
        SelectedLocationOption = LocationOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedLocation, StringComparison.Ordinal))
                              ?? LocationOptions.FirstOrDefault();
        SelectedEntryLocationOption = LocationOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedEntryLocation, StringComparison.Ordinal))
                                   ?? LocationOptions.FirstOrDefault();
    }

    private static LocalizedOption? PickOption(System.Collections.ObjectModel.ObservableCollection<LocalizedOption> options, string? value, bool ignoreCase = false)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return options.FirstOrDefault(option => string.Equals(option.Value, value, comparison)) ?? options.FirstOrDefault();
    }

    /// <summary>
    /// Re-resolves every dropdown's selected option from its authoritative string value. A ComboBox
    /// resets its bound SelectedItem to null when its page is unloaded (navigation / tab switch),
    /// which leaves the box blank on return; calling this after a page change restores the selection.
    /// Each assignment is a no-op when the option is already correct, so it only fixes the blanked ones.
    /// </summary>
    public void RestoreSelectedOptions()
    {
        SelectedConnectionModeOption = PickOption(ConnectionModeOptions, SelectedConnectionMode);
        SelectedLocationOption = PickOption(LocationOptions, SelectedLocation);
        SelectedEntryLocationOption = PickOption(LocationOptions, SelectedEntryLocation);
        SelectedBridgeTypeOption = PickOption(BridgeTypeOptions, SelectedBridgeType, ignoreCase: true);
        SelectedBridgeSourceModeOption = PickOption(BridgeSourceModeOptions, BridgeSourceMode);
        SelectedProxyScopeModeOption = PickOption(ProxyScopeModeOptions, ProxyScopeMode);
        SelectedTunCoreModeOption = PickOption(TunCoreModeOptions, TunCoreMode);
        SelectedTunStackModeOption = PickOption(TunStackModeOptions, TunStackMode);
        SelectedAutoStartModeOption = PickOption(AutoStartModeOptions, AutoStartMode, ignoreCase: true);
        SelectedThemeModeOption = PickOption(ThemeModeOptions, NormalizeThemeMode(ThemeMode), ignoreCase: true);
        SelectedTorIpv6ModeOption = PickOption(TorOptionModeOptions, TorIpv6Mode);
        SelectedHardwareAccelerationModeOption = PickOption(TorOptionModeOptions, HardwareAccelerationMode);
        SelectedConnectionPaddingModeOption = PickOption(ConnectionPaddingModeOptions, ConnectionPaddingMode);
        SelectedAccentColorOption = PickOption(AccentColorOptions, SelectedAccentColor);
        SelectedTorEngineOption = PickOption(TorEngineOptions, SelectedTorEngineMode);
        SelectedRelayRefreshIntervalOption = PickOption(RelayRefreshIntervalOptions, SelectedRelayRefreshInterval);
        SelectedUpdateChannelOption = PickOption(UpdateChannelOptions, SelectedUpdateChannel);
        SelectedLanguageOption = PickOption(LanguageOptions, SelectedLanguage, ignoreCase: true);
    }

    private void RefreshBridgeTypeOptions()
    {
        BridgeTypeOptions.Clear();
        foreach (var bridgeType in BridgeTypes)
        {
            BridgeTypeOptions.Add(new LocalizedOption(bridgeType, LocalizeBridgeType(bridgeType)));
        }

        SelectedBridgeTypeOption = BridgeTypeOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedBridgeType, StringComparison.OrdinalIgnoreCase))
                                 ?? BridgeTypeOptions.FirstOrDefault();
    }

    private void RefreshBridgeSourceOptions()
    {
        BridgeSourceModeOptions.Clear();
        foreach (var bridgeSource in BridgeSourceModes)
        {
            BridgeSourceModeOptions.Add(new LocalizedOption(bridgeSource, LocalizeBridgeSourceMode(bridgeSource)));
        }

        SelectedBridgeSourceModeOption = BridgeSourceModeOptions.FirstOrDefault(option => string.Equals(option.Value, BridgeSourceMode, StringComparison.Ordinal))
                                       ?? BridgeSourceModeOptions.FirstOrDefault();
    }

    private void RefreshProxyScopeOptions()
    {
        ProxyScopeModeOptions.Clear();
        foreach (var mode in ProxyScopeModes)
        {
            ProxyScopeModeOptions.Add(new LocalizedOption(mode, LocalizeProxyScopeMode(mode)));
        }

        SelectedProxyScopeModeOption = ProxyScopeModeOptions.FirstOrDefault(option => string.Equals(option.Value, ProxyScopeMode, StringComparison.Ordinal))
                                   ?? ProxyScopeModeOptions.FirstOrDefault();
    }

    private void RefreshTunStackOptions()
    {
        TunStackModeOptions.Clear();
        foreach (var mode in TunStackModes)
        {
            TunStackModeOptions.Add(new LocalizedOption(mode, LocalizeTunStackMode(mode)));
        }

        SelectedTunStackModeOption = TunStackModeOptions.FirstOrDefault(option => string.Equals(option.Value, TunStackMode, StringComparison.Ordinal))
                                  ?? TunStackModeOptions.FirstOrDefault();
    }

    private void RefreshTunCoreOptions()
    {
        TunCoreModeOptions.Clear();
        foreach (var mode in TunCoreModes)
        {
            TunCoreModeOptions.Add(new LocalizedOption(mode, LocalizeTunCoreMode(mode)));
        }

        SelectedTunCoreModeOption = TunCoreModeOptions.FirstOrDefault(option => string.Equals(option.Value, TunCoreMode, StringComparison.Ordinal))
                                  ?? TunCoreModeOptions.FirstOrDefault();
    }

    private void RefreshAutoStartModeOptions()
    {
        AutoStartModeOptions.Clear();
        AutoStartModeOptions.Add(new LocalizedOption(AutoStartModeOff, LocalizationService.Get("Settings.AutoStartOff")));
        AutoStartModeOptions.Add(new LocalizedOption(AutoStartModeOn, LocalizationService.Get("Settings.AutoStartOn")));
        AutoStartModeOptions.Add(new LocalizedOption(AutoStartModeMinimized, LocalizationService.Get("Settings.AutoStartMinimized")));
        SelectedAutoStartModeOption = AutoStartModeOptions.FirstOrDefault(option => string.Equals(option.Value, AutoStartMode, StringComparison.OrdinalIgnoreCase))
                                    ?? AutoStartModeOptions.FirstOrDefault();
    }

    private void RefreshThemeModeOptions()
    {
        ThemeModeOptions.Clear();
        foreach (var mode in ThemeModes)
        {
            ThemeModeOptions.Add(new LocalizedOption(mode, LocalizeThemeMode(mode)));
        }

        var normalizedThemeMode = NormalizeThemeMode(ThemeMode);
        SelectedThemeModeOption = ThemeModeOptions.FirstOrDefault(option => string.Equals(option.Value, normalizedThemeMode, StringComparison.OrdinalIgnoreCase))
                                  ?? ThemeModeOptions.FirstOrDefault();
    }

    private void RefreshTorAdvancedModeOptions()
    {
        TorOptionModeOptions.Clear();
        TorOptionModeOptions.Add(new LocalizedOption(OnionHopConnectOptions.ToggleModeDefault, LocalizationService.Get("Settings.OptionDefault")));
        TorOptionModeOptions.Add(new LocalizedOption(OnionHopConnectOptions.ToggleModeEnabled, LocalizationService.Get("Settings.OptionEnabled")));
        TorOptionModeOptions.Add(new LocalizedOption(OnionHopConnectOptions.ToggleModeDisabled, LocalizationService.Get("Settings.OptionDisabled")));

        ConnectionPaddingModeOptions.Clear();
        ConnectionPaddingModeOptions.Add(new LocalizedOption(OnionHopConnectOptions.ConnectionPaddingAuto, LocalizationService.Get("Settings.ConnectionPaddingAuto")));
        ConnectionPaddingModeOptions.Add(new LocalizedOption(OnionHopConnectOptions.ConnectionPaddingEnabled, LocalizationService.Get("Settings.OptionEnabled")));
        ConnectionPaddingModeOptions.Add(new LocalizedOption(OnionHopConnectOptions.ConnectionPaddingDisabled, LocalizationService.Get("Settings.OptionDisabled")));

        SelectedTorIpv6ModeOption = TorOptionModeOptions.FirstOrDefault(option => string.Equals(option.Value, TorIpv6Mode, StringComparison.Ordinal))
                                    ?? TorOptionModeOptions.FirstOrDefault();
        SelectedHardwareAccelerationModeOption = TorOptionModeOptions.FirstOrDefault(option => string.Equals(option.Value, HardwareAccelerationMode, StringComparison.Ordinal))
                                                 ?? TorOptionModeOptions.FirstOrDefault();
        SelectedConnectionPaddingModeOption = ConnectionPaddingModeOptions.FirstOrDefault(option => string.Equals(option.Value, ConnectionPaddingMode, StringComparison.Ordinal))
                                              ?? ConnectionPaddingModeOptions.FirstOrDefault();
    }

    private string ResolveLocationCodeFromSelection(string? selection)
    {
        return TorNodeDatabaseService.NormalizeSelectionToCountryCode(selection, _countryStatsByCode.Values.ToList());
    }

    private static string NormalizeExitNodeFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal);

        if (normalized.StartsWith("$", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(1);
        }

        return normalized.ToUpperInvariant();
    }

    private static string BuildFingerprintSummary(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return "--";
        }

        var normalized = NormalizeExitNodeFingerprint(fingerprint);
        if (normalized.Length <= 16)
        {
            return normalized;
        }

        return $"{normalized[..8]}...{normalized[^8..]}";
    }

    private string GetLocationLabel(string location)
    {
        if (string.Equals(location, AutomaticLocationLabel, StringComparison.Ordinal))
        {
            return LocalizationService.Get("Home.Automatic");
        }

        if (_countryStatsByCode.TryGetValue(location, out var stats))
        {
            return $"{stats.CountryName} ({stats.CountryCode.ToUpperInvariant()} - {stats.TotalNodes})";
        }

        return location.ToUpperInvariant();
    }

    private void ApplyCountryStats(IReadOnlyList<TorCountryNodeStats> stats)
    {
        _countryStatsByCode = stats
            .Where(item => !string.IsNullOrWhiteSpace(item.CountryCode))
            .GroupBy(item => item.CountryCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(item => item.CountryCode, StringComparer.OrdinalIgnoreCase);

        var previousExit = SelectedLocation;
        var previousEntry = SelectedEntryLocation;

        Locations.Clear();
        Locations.Add(AutomaticLocationLabel);
        foreach (var country in stats.OrderBy(item => item.CountryName, StringComparer.OrdinalIgnoreCase))
        {
            Locations.Add(country.CountryCode);
        }

        var normalizedExit = string.IsNullOrWhiteSpace(previousExit) ? string.Empty : ResolveLocationCodeFromSelection(previousExit);
        var normalizedEntry = string.IsNullOrWhiteSpace(previousEntry) ? string.Empty : ResolveLocationCodeFromSelection(previousEntry);
        SelectedLocation = string.IsNullOrWhiteSpace(normalizedExit) ? AutomaticLocationLabel : normalizedExit;
        SelectedEntryLocation = string.IsNullOrWhiteSpace(normalizedEntry) ? AutomaticLocationLabel : normalizedEntry;

        // Only the location lists depend on the country DB. Rebuilding everything here (the old
        // behavior) tore down the engine/mode/bridge combos and left them blank, so refresh just
        // the location options.
        RefreshLocationOptions();
    }

    private static string LocalizeBridgeType(string bridgeType)
    {
        return bridgeType.ToLowerInvariant() switch
        {
            "automatic" => LocalizationService.Get("BridgeType.Automatic"),
            "vanilla" => LocalizationService.Get("BridgeType.Vanilla"),
            "obfs4" => LocalizationService.Get("BridgeType.Obfs4"),
            "snowflake" => LocalizationService.Get("BridgeType.Snowflake"),
            "conjure" => LocalizationService.Get("BridgeType.Conjure"),
            "webtunnel" => LocalizationService.Get("BridgeType.Webtunnel"),
            "meek" => LocalizationService.Get("BridgeType.MeekAzure"),
            "meek-azure" => LocalizationService.Get("BridgeType.MeekAzure"),
            "dnstt" => LocalizationService.Get("BridgeType.Dnstt"),
            "custom" => LocalizationService.Get("BridgeType.Custom"),
            _ => bridgeType
        };
    }

    private static string LocalizeBridgeSourceMode(string sourceMode)
    {
        return sourceMode switch
        {
            BridgeSourceAuto => LocalizationService.Get("BridgeSource.Auto"),
            BridgeSourceOnlineOnly => LocalizationService.Get("BridgeSource.OnlineOnly"),
            BridgeSourceOfflineOnly => LocalizationService.Get("BridgeSource.OfflineOnly"),
            BridgeSourceCollectorOnly => LocalizationService.Get("BridgeSource.CollectorOnly"),
            _ => sourceMode
        };
    }

    private static string LocalizeProxyScopeMode(string mode)
    {
        return mode switch
        {
            ProxyScopeSystem => LocalizationService.Get("ProxyScope.System"),
            ProxyScopeSystemSocks => LocalizationService.Get("ProxyScope.SystemSocks"),
            ProxyScopeLocalOnly => LocalizationService.Get("ProxyScope.LocalOnly"),
            _ => mode
        };
    }

    private static string LocalizeTunCoreMode(string mode)
    {
        return NormalizeTunCoreMode(mode) switch
        {
            TunCoreXray => LocalizationService.Get("TunCore.Xray"),
            _ => LocalizationService.Get("TunCore.SingBox")
        };
    }

    private static string LocalizeTunStackMode(string mode)
    {
        return mode switch
        {
            TunStackMixed => LocalizationService.Get("TunStack.Mixed"),
            TunStackSystem => LocalizationService.Get("TunStack.System"),
            TunStackGvisor => LocalizationService.Get("TunStack.Gvisor"),
            _ => mode
        };
    }

    private static string LocalizeThemeMode(string mode)
    {
        return NormalizeThemeMode(mode) switch
        {
            ThemeModeSystem => LocalizationService.Get("Settings.AppearanceSystemDefault"),
            ThemeModeDark => LocalizationService.Get("Settings.AppearanceDark"),
            ThemeModeLight => LocalizationService.Get("Settings.AppearanceLight"),
            _ => LocalizationService.Get("Settings.AppearanceSystemDefault")
        };
    }

    private static string NormalizeBridgeSourceMode(string? sourceMode)
    {
        if (string.IsNullOrWhiteSpace(sourceMode))
        {
            return BridgeSourceAuto;
        }

        if (string.Equals(sourceMode, BridgeSourceOnlineOnly, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceMode, "Bridge" + "DB only", StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourceOnlineOnly;
        }

        if (string.Equals(sourceMode, BridgeSourceOfflineOnly, StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourceOfflineOnly;
        }

        if (string.Equals(sourceMode, BridgeSourceCollectorOnly, StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourceCollectorOnly;
        }

        return BridgeSourceAuto;
    }

    private static string NormalizeProxyScopeMode(string? mode)
    {
        if (string.Equals(mode, ProxyScopeSystemSocks, StringComparison.OrdinalIgnoreCase))
        {
            return ProxyScopeSystemSocks;
        }

        if (string.Equals(mode, ProxyScopeLocalOnly, StringComparison.OrdinalIgnoreCase))
        {
            return ProxyScopeLocalOnly;
        }

        return ProxyScopeSystem;
    }

    private static string NormalizeTunCoreMode(string? mode)
    {
        // Xray TUN is not supported on macOS (no process matching → routing loops).
        if (OperatingSystem.IsMacOS())
        {
            return TunCoreSingBox;
        }

        if (string.Equals(mode, TunCoreXray, StringComparison.OrdinalIgnoreCase))
        {
            return TunCoreXray;
        }

        return TunCoreSingBox;
    }

    private static string NormalizeTunStackMode(string? mode)
    {
        if (string.Equals(mode, TunStackSystem, StringComparison.OrdinalIgnoreCase))
        {
            return TunStackSystem;
        }

        if (string.Equals(mode, TunStackGvisor, StringComparison.OrdinalIgnoreCase))
        {
            return TunStackGvisor;
        }

        return TunStackMixed;
    }

    private static string NormalizeThemeMode(string? mode)
    {
        if (string.Equals(mode, ThemeModeDark, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeModeDark;
        }

        if (string.Equals(mode, ThemeModeLight, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeModeLight;
        }

        return ThemeModeSystem;
    }

    private static string LocalizeRuntimeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        if (RuntimeStatusResourceMap.TryGetValue(normalized, out var key))
        {
            return LocalizationService.Get(key);
        }

        // Check if the value is already a localized string from a previous language switch.
        // Reverse-lookup: find any resource key whose current localized value matches.
        foreach (var entry in RuntimeStatusResourceMap.Values)
        {
            if (string.Equals(LocalizationService.Get(entry), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return LocalizationService.Get(entry);
            }
        }

        return value;
    }

    private static string BuildSidebarStatusMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lastLogsIndex = normalized.IndexOf("\nLast logs:", StringComparison.OrdinalIgnoreCase);
        if (lastLogsIndex >= 0)
        {
            normalized = normalized[..lastLogsIndex];
        }

        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        if (normalized.Length <= 180)
        {
            return normalized;
        }

        return normalized[..180].TrimEnd() + "...";
    }

    private static Version GetCurrentAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var semanticVersion = informationalVersion?
            .Split('+', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        var parsedVersion = UpdateService.ParseVersionFromTag(semanticVersion);
        if (HasKnownVersion(parsedVersion))
        {
            return parsedVersion;
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion != null && HasKnownVersion(assemblyVersion))
        {
            return assemblyVersion;
        }

        return new Version(0, 0, 0);
    }

    private static bool IsVersionNewer(Version latest, Version current)
    {
        var latestParts = new[]
        {
            GetVersionPart(latest.Major),
            GetVersionPart(latest.Minor),
            GetVersionPart(latest.Build),
            GetVersionPart(latest.Revision)
        };
        var currentParts = new[]
        {
            GetVersionPart(current.Major),
            GetVersionPart(current.Minor),
            GetVersionPart(current.Build),
            GetVersionPart(current.Revision)
        };

        for (var index = 0; index < latestParts.Length; index++)
        {
            if (latestParts[index] > currentParts[index])
            {
                return true;
            }

            if (latestParts[index] < currentParts[index])
            {
                return false;
            }
        }

        return false;
    }

    private static bool HasKnownVersion(Version version)
    {
        return GetVersionPart(version.Major) > 0
               || GetVersionPart(version.Minor) > 0
               || GetVersionPart(version.Build) > 0
               || GetVersionPart(version.Revision) > 0;
    }

    private static string FormatVersion(Version version)
    {
        var major = GetVersionPart(version.Major);
        var minor = GetVersionPart(version.Minor);

        if (version.Revision >= 0)
        {
            return $"{major}.{minor}.{GetVersionPart(version.Build)}.{version.Revision}";
        }

        if (version.Build >= 0)
        {
            return $"{major}.{minor}.{GetVersionPart(version.Build)}";
        }

        return $"{major}.{minor}";
    }

    private static int GetVersionPart(int value) => value < 0 ? 0 : value;
    private void OpenLaunchPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            AppendLog($"Ignoring invalid launch URL: {url}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to open launch URL '{uri}': {ex.Message}");
        }
    }

    public void AppendLog(string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            lock (_pendingLogsLock)
            {
                EnqueueWithLimit(_pendingAppLogs, message, ref _droppedAppLogLines);
            }

            return;
        }

        AppendRawLogLine(LogLines, message);
    }

    public void ClearAppLogs()
    {
        lock (_pendingLogsLock)
        {
            _pendingAppLogs.Clear();
            _droppedAppLogLines = 0;
        }

        LogLines.Clear();
        AppendRawLogLine(LogLines, "App logs cleared.");
    }

    public void AppendDnsLog(string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            lock (_pendingLogsLock)
            {
                EnqueueWithLimit(_pendingDnsLogs, message, ref _droppedDnsLogLines);
            }

            return;
        }

        AppendRawLogLine(DnsLogLines, message);
    }

    public void ClearDnsLogs()
    {
        lock (_pendingLogsLock)
        {
            _pendingDnsLogs.Clear();
            _droppedDnsLogLines = 0;
        }

        DnsLogLines.Clear();
        AppendRawLogLine(DnsLogLines, "DNS logs cleared.");
    }

    public void AppendXrayLog(string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            lock (_pendingLogsLock)
            {
                EnqueueWithLimit(_pendingXrayLogs, message, ref _droppedXrayLogLines);
            }

            return;
        }

        AppendRawLogLine(XrayLogLines, message);
    }

    public void ClearXrayLogs()
    {
        lock (_pendingLogsLock)
        {
            _pendingXrayLogs.Clear();
            _droppedXrayLogLines = 0;
        }

        XrayLogLines.Clear();
        var sourceName = string.Equals(NormalizeTunCoreMode(TunCoreMode), TunCoreXray, StringComparison.Ordinal)
            ? "Xray"
            : "sing-box";
        AppendRawLogLine(XrayLogLines, $"{sourceName} logs cleared.");
    }

    public void AppendVpnLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        VpnLogLines.Add(line);
        TrimLogs(VpnLogLines);
    }

    public void ClearVpnLogs()
    {
        VpnLogLines.Clear();
        AppendVpnLog("VPN engine logs cleared.");
    }

    private static void TrimLogs(ObservableCollection<string> list)
    {
        const int max = 2000;
        const int batch = 200;
        if (list.Count <= max + batch)
        {
            return;
        }

        var toRemove = list.Count - max;
        var remaining = new List<string>(max);
        for (var i = toRemove; i < list.Count; i++)
        {
            remaining.Add(list[i]);
        }

        list.Clear();
        foreach (var item in remaining)
        {
            list.Add(item);
        }
    }

    private void StartLogPump()
    {
        _logFlushTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };

        _logFlushTimer.Tick -= OnLogFlushTimerTick;
        _logFlushTimer.Tick += OnLogFlushTimerTick;
        _logFlushTimer.Start();
    }

    private void OnLogFlushTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            FlushQueuedLogs();
        }
        catch (Exception ex)
        {
            try
            {
                AppendRawLogLine(LogLines, $"Log pump error: {ex.Message}");
            }
            catch
            {
                // Last-resort guard: never crash the UI loop because logging failed.
            }
        }
    }

    private void FlushQueuedLogs()
    {
        List<string> appBatch = [];
        List<string> dnsBatch = [];
        List<string> xrayBatch = [];
        var droppedApp = 0;
        var droppedDns = 0;
        var droppedXray = 0;

        lock (_pendingLogsLock)
        {
            droppedApp = _droppedAppLogLines;
            droppedDns = _droppedDnsLogLines;
            droppedXray = _droppedXrayLogLines;
            _droppedAppLogLines = 0;
            _droppedDnsLogLines = 0;
            _droppedXrayLogLines = 0;

            DrainQueue(_pendingAppLogs, appBatch, LogFlushBatchPerQueue);
            DrainQueue(_pendingDnsLogs, dnsBatch, LogFlushBatchPerQueue);
            DrainQueue(_pendingXrayLogs, xrayBatch, LogFlushBatchPerQueue);
        }

        if (droppedApp > 0)
        {
            AppendRawLogLine(LogLines, $"Dropped {droppedApp} app log lines to keep the UI responsive.");
        }

        if (droppedDns > 0)
        {
            AppendRawLogLine(DnsLogLines, $"Dropped {droppedDns} DNS log lines to keep the UI responsive.");
        }

        if (droppedXray > 0)
        {
            AppendRawLogLine(XrayLogLines, $"Dropped {droppedXray} Xray log lines to keep the UI responsive.");
        }

        foreach (var line in appBatch)
        {
            AppendRawLogLine(LogLines, line);
        }

        foreach (var line in dnsBatch)
        {
            AppendRawLogLine(DnsLogLines, line);
        }

        foreach (var line in xrayBatch)
        {
            AppendRawLogLine(XrayLogLines, line);
        }
    }

    private void EnqueueClientLog(string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_pendingLogsLock)
        {
            if (IsXrayLogLine(message))
            {
                EnqueueWithLimit(_pendingXrayLogs, message, ref _droppedXrayLogLines);
                if (IsImportantXrayLogLine(message))
                {
                    EnqueueWithLimit(_pendingAppLogs, message, ref _droppedAppLogLines);
                }

                return;
            }

            EnqueueWithLimit(_pendingAppLogs, message, ref _droppedAppLogLines);
        }
    }

    private void EnqueueDnsLog(string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_pendingLogsLock)
        {
            EnqueueWithLimit(_pendingDnsLogs, message, ref _droppedDnsLogLines);
        }
    }

    private static void EnqueueWithLimit(Queue<string> queue, string message, ref int droppedCounter)
    {
        if (queue.Count >= MaxBufferedLogEntries)
        {
            queue.Dequeue();
            droppedCounter++;
        }

        queue.Enqueue(message);
    }

    private static void DrainQueue(Queue<string> source, List<string> destination, int maxLines)
    {
        while (source.Count > 0 && destination.Count < maxLines)
        {
            destination.Add(source.Dequeue());
        }
    }

    private static bool IsXrayLogLine(string message)
    {
        return message.StartsWith("xray:", StringComparison.OrdinalIgnoreCase)
               || message.StartsWith("sing-box:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImportantXrayLogLine(string message)
    {
        return message.Contains("[warn", StringComparison.OrdinalIgnoreCase)
               || message.Contains("[error", StringComparison.OrdinalIgnoreCase)
               || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("panic", StringComparison.OrdinalIgnoreCase)
               || message.Contains("fatal", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendRawLogLine(ObservableCollection<string> target, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        target.Add(line);
        TrimLogs(target);
    }

    private void StartSpeedMonitor()
    {
        if (_disposed)
        {
            return;
        }

        _speedTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _speedTimer.Tick -= OnSpeedTimerTick;
        _speedTimer.Tick += OnSpeedTimerTick;

        _lastSpeedSampleUtc = DateTime.UtcNow;
        _lastBytesReceived = 0;
        _lastBytesSent = 0;

        OnSpeedTimerTick(this, EventArgs.Empty);
        _speedTimer.Start();
    }

    private async void OnSpeedTimerTick(object? sender, EventArgs e)
    {
        if (_speedUpdateInProgress || _disposed)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedSampleUtc).TotalSeconds;
        if (elapsed <= 0.2)
        {
            return;
        }

        _speedUpdateInProgress = true;
        try
        {
            var traffic = await _client.TryGetTorTrafficBytesAsync(CancellationToken.None);
            if (!traffic.HasValue)
            {
                DownloadSpeed = "--";
                UploadSpeed = "--";
                DownloadSpeedGauge = 0;
                UploadSpeedGauge = 0;
                _lastSpeedSampleUtc = now;
                _lastBytesReceived = 0;
                _lastBytesSent = 0;
                return;
            }

            var rx = traffic.Value.BytesRead;
            var tx = traffic.Value.BytesWritten;

            if (_lastBytesReceived == 0 && _lastBytesSent == 0)
            {
                _lastBytesReceived = rx;
                _lastBytesSent = tx;
                _lastSpeedSampleUtc = now;
                DownloadSpeed = "0 B/s";
                UploadSpeed = "0 B/s";
                DownloadSpeedGauge = 0;
                UploadSpeedGauge = 0;
                return;
            }

            var downBytesPerSecond = Math.Max(0, rx - _lastBytesReceived) / elapsed;
            var upBytesPerSecond = Math.Max(0, tx - _lastBytesSent) / elapsed;

            _lastBytesReceived = rx;
            _lastBytesSent = tx;
            _lastSpeedSampleUtc = now;
            UpdateSessionTrafficTotals(rx, tx);

            DownloadSpeed = FormatRate(downBytesPerSecond);
            UploadSpeed = FormatRate(upBytesPerSecond);
            DownloadSpeedGauge = NormalizeGauge(downBytesPerSecond);
            UploadSpeedGauge = NormalizeGauge(upBytesPerSecond);
        }
        catch
        {
            DownloadSpeed = "--";
            UploadSpeed = "--";
            DownloadSpeedGauge = 0;
            UploadSpeedGauge = 0;
        }
        finally
        {
            _speedUpdateInProgress = false;
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        const double kilo = 1024;
        const double mega = 1024 * 1024;

        if (bytesPerSecond < kilo)
        {
            return $"{bytesPerSecond:0} B/s";
        }

        if (bytesPerSecond < mega)
        {
            return $"{bytesPerSecond / kilo:0.0} KB/s";
        }

        return $"{bytesPerSecond / mega:0.00} MB/s";
    }

    private static double NormalizeGauge(double bytesPerSecond)
    {
        // Display up to ~50 MB/s on the gauge.
        const double max = 50d * 1024 * 1024;
        var value = bytesPerSecond / max;
        if (value < 0)
        {
            return 0;
        }

        return value > 1 ? 1 : value;
    }
}
