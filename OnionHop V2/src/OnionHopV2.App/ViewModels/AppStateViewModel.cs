using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnionHopV2.App.Services;
using OnionHopV2.Core;
using OnionHopV2.Core.Models;
using OnionHopV2.Core.Platform.Windows;
using OnionHopV2.Core.Services;

namespace OnionHopV2.App.ViewModels;

public sealed partial class AppStateViewModel : ViewModelBase, IDisposable
{
    private const string DefaultAllowedPorts = "80,443";
    private const string DefaultConnectedPageUrl = "https://check.torproject.org/";
    private const string DefaultDisconnectedPageUrl = "https://support.torproject.org/";

    public const string AutomaticLocationLabel = "Automatic";
    public const string ConnectionModeProxy = "Proxy Mode (Recommended)";
    public const string ConnectionModeTun = "TUN/VPN Mode (Admin)";
    public const string AutoStartModeOff = "Off";
    public const string AutoStartModeOn = "On";
    public const string AutoStartModeMinimized = "On (Minimized)";

    public const string DnsProviderCloudflare = "Cloudflare (DoH)";
    public const string DnsProviderGoogle = "Google (DoH)";
    public const string DnsProviderQuad9 = "Quad9 (DoH)";
    public const string DnsProviderCustom = "Custom (DoH)";
    public const string BridgeTypeAutomatic = "automatic";
    public const string BridgeSourceAuto = OnionHopConnectOptions.BridgeSourceAuto;
    public const string BridgeSourceBridgeDbOnly = OnionHopConnectOptions.BridgeSourceBridgeDbOnly;
    public const string BridgeSourceOfflineOnly = OnionHopConnectOptions.BridgeSourceOfflineOnly;
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
        ["TUN/VPN mode requires Administrator. Requesting elevation..."] = "Status.AdminRequiredRequesting",
        ["TUN/VPN-Modus benötigt Administratorrechte. Erhöhe Berechtigungen..."] = "Status.AdminRequiredRequesting",
        ["Administrator access is required for TUN/VPN mode. Connection canceled."] = "Status.AdminRequiredCanceled",
        ["Administratorrechte sind für den TUN/VPN-Modus erforderlich. Verbindung abgebrochen."] = "Status.AdminRequiredCanceled",
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
        nameof(IsDarkMode),
        nameof(UseNativeTheme),
        nameof(SelectedLocation),
        nameof(SelectedEntryLocation),
        nameof(ExitNodeFingerprint),
        nameof(SelectedConnectionMode),
        nameof(UseHybridRouting),
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
        nameof(RestrictedFirewallMode),
        nameof(AllowedPorts),
        nameof(OnionDnsProxyEnabled),
        nameof(MaxCircuitInactivityMinutes),
        nameof(OpenConnectedPageEnabled),
        nameof(ConnectedPageUrl),
        nameof(OpenDisconnectedPageEnabled),
        nameof(DisconnectedPageUrl),
        nameof(EnableDiscordStatus),
        nameof(HybridRouteAllWebTraffic),
        nameof(HybridBlockQuicForTorApps),
        nameof(HybridTorApps),
        nameof(HybridBypassApps),
        nameof(SelectedLanguage)
    };

    private readonly OnionHopClient _client;
    private readonly SettingsService _settingsService = new();
    private readonly TorNodeDatabaseService _nodeDatabaseService = new();
    private readonly DiscordPresenceService _discordPresence = new();
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _settingsSaveCts;
    private bool _loadingSettings;
    private bool _disposed;
    private bool _hasStatusSnapshot;
    private bool _wasConnected;
    private Dictionary<string, TorCountryNodeStats> _countryStatsByCode = new(StringComparer.OrdinalIgnoreCase);

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

        DnsProviders =
        [
            DnsProviderCloudflare,
            DnsProviderGoogle,
            DnsProviderQuad9,
            DnsProviderCustom
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
            BridgeSourceBridgeDbOnly,
            BridgeSourceOfflineOnly
        ];

        RefreshLanguageOptions();
        RefreshLocalizedOptions();

        BridgeTypes.Add(BridgeTypeAutomatic);
        BridgeTypes.Add("obfs4");
        BridgeTypes.Add("snowflake");
        BridgeTypes.Add("conjure");
        BridgeTypes.Add("meek-azure");
        BridgeTypes.Add("webtunnel");
        BridgeTypes.Add("custom");

        _client = new OnionHopClient();
        _client.Log += (_, message) => Dispatcher.UIThread.Post(() => AppendLog(message));
        _client.DnsLog += (_, message) => Dispatcher.UIThread.Post(() => AppendDnsLog(message));
        _client.StatusUpdated += (_, update) => Dispatcher.UIThread.Post(() => ApplyClientStatus(update));
        _client.DependencyUpdated += (_, update) => Dispatcher.UIThread.Post(() => ApplyDependencyUpdate(update));

        LoadSettings();
        if (OperatingSystem.IsWindows() &&
            !string.Equals(AutoStartMode, AutoStartModeOff, StringComparison.OrdinalIgnoreCase))
        {
            WindowsAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
        }
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
    public ObservableCollection<string> BridgeTypes { get; } = [];
    public ObservableCollection<LocalizedOption> BridgeTypeOptions { get; } = [];
    public ObservableCollection<string> BridgeSourceModes { get; }
    public ObservableCollection<LocalizedOption> BridgeSourceModeOptions { get; } = [];
    public ObservableCollection<string> DnsProviders { get; }
    public ObservableCollection<string> TorOptionModes { get; }
    public ObservableCollection<string> ConnectionPaddingModes { get; }
    public ObservableCollection<LocalizedOption> LanguageOptions { get; } = [];
    public ObservableCollection<LocalizedOption> TorOptionModeOptions { get; } = [];
    public ObservableCollection<LocalizedOption> ConnectionPaddingModeOptions { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<string> DnsLogLines { get; } = [];

    [ObservableProperty] private string _downloadSpeed = "--";
    [ObservableProperty] private string _uploadSpeed = "--";
    [ObservableProperty] private double _downloadSpeedGauge;
    [ObservableProperty] private double _uploadSpeedGauge;

    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isDisconnecting;

    [ObservableProperty] private string _selectedLocation = AutomaticLocationLabel;
    [ObservableProperty] private string _selectedEntryLocation = AutomaticLocationLabel;
    [ObservableProperty] private string _exitNodeFingerprint = string.Empty;
    [ObservableProperty] private string _selectedConnectionMode = ConnectionModeProxy;
    [ObservableProperty] private bool _useHybridRouting;
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
    [ObservableProperty] private bool _restrictedFirewallMode;
    [ObservableProperty] private string _allowedPorts = DefaultAllowedPorts;
    [ObservableProperty] private bool _onionDnsProxyEnabled;
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
    [ObservableProperty] private LocalizedOption? _selectedLanguageOption;
    [ObservableProperty] private LocalizedOption? _selectedAutoStartModeOption;
    [ObservableProperty] private LocalizedOption? _selectedTorIpv6ModeOption;
    [ObservableProperty] private LocalizedOption? _selectedHardwareAccelerationModeOption;
    [ObservableProperty] private LocalizedOption? _selectedConnectionPaddingModeOption;

    [ObservableProperty] private bool _hybridRouteAllWebTraffic = true;
    [ObservableProperty] private bool _hybridBlockQuicForTorApps = true;
    [ObservableProperty] private string _hybridTorApps = string.Empty;
    [ObservableProperty] private string _hybridBypassApps = string.Empty;

    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private string _autoStartMode = AutoStartModeOff;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private bool _isDarkMode;
    [ObservableProperty] private bool _useNativeTheme;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _connectionStatus = string.Empty;
    [ObservableProperty] private string _currentIp = "--.--.--.--";
    [ObservableProperty] private string _socksProxyPort = OnionHopClient.DefaultSocksPort.ToString();
    [ObservableProperty] private string _httpProxyPort = "--";
    [ObservableProperty] private double _connectionProgress;

    [ObservableProperty] private bool _isDependencyDownloadInProgress;
    [ObservableProperty] private double _dependencyDownloadProgress;
    [ObservableProperty] private string _dependencyDownloadStatus = string.Empty;

    private DispatcherTimer? _speedTimer;
    private DispatcherTimer? _ipRefreshTimer;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSpeedSampleUtc;
    private bool _speedUpdateInProgress;

    public bool IsBusy => IsConnecting || IsDisconnecting || IsDependencyDownloadInProgress;

    public bool ShowConnectButton => !IsConnected && !IsConnecting;
    public bool ShowDisconnectButton => IsConnected && !IsDisconnecting;
    public bool ShowCancelButton => IsConnecting;

    public bool IsTunMode => string.Equals(SelectedConnectionMode, ConnectionModeTun, StringComparison.Ordinal);
    public bool IsProxyMode => !IsTunMode;
    public bool CanUseKillSwitch => IsTunMode && !UseHybridRouting;
    public bool IsManualExitNodeFingerprintSet => !string.IsNullOrWhiteSpace(ExitNodeFingerprint);
    public bool CanSelectExitLocation => !IsManualExitNodeFingerprintSet;
    public bool CanSelectEntryLocation => !UseTorBridges;
    public bool IsCustomDoh => string.Equals(SelectedDnsProvider, DnsProviderCustom, StringComparison.Ordinal);
    public bool UseCustomBridges => string.Equals(SelectedBridgeType, "custom", StringComparison.OrdinalIgnoreCase);
    public bool IsSnowflakeBridgeSelected => string.Equals(SelectedBridgeType, "snowflake", StringComparison.OrdinalIgnoreCase);
    public bool CanUseOnionDnsProxy => OperatingSystem.IsWindows() && WindowsAdmin.IsAdministrator();
    public bool UseCustomChrome => !UseNativeTheme;
    public bool SupportsNativeWindowChrome => true;
    public bool CanConfigureSplitTunneling => IsTunMode && UseHybridRouting;
    public sealed record LocalizedOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    partial void OnSelectedLanguageOptionChanged(LocalizedOption? value)
    {
        if (value == null)
        {
            var fallbackIndex = string.Equals(SelectedLanguage, "de", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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

    partial void OnSelectedLanguageChanged(string value)
    {
        var normalized = value.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        if (!string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLanguage = normalized;
            return;
        }

        var languageIndex = string.Equals(normalized, "de", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var language = value == 1 ? "de" : "en";
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
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowConnectButton));
        OnPropertyChanged(nameof(ShowDisconnectButton));
    }

    partial void OnIsDisconnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowDisconnectButton));
    }

    partial void OnIsDependencyDownloadInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowConnectButton));
    }

    partial void OnSelectedConnectionModeChanged(string value)
    {
        SelectedConnectionModeOption = ConnectionModeOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal));
        OnPropertyChanged(nameof(IsTunMode));
        OnPropertyChanged(nameof(IsProxyMode));
        OnPropertyChanged(nameof(CanUseKillSwitch));
        OnPropertyChanged(nameof(CanConfigureSplitTunneling));
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
    }

    partial void OnSelectedEntryLocationChanged(string value)
    {
        SelectedEntryLocationOption = LocationOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal));
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
        if (IsManualExitNodeFingerprintSet &&
            !string.Equals(SelectedLocation, AutomaticLocationLabel, StringComparison.Ordinal))
        {
            SelectedLocation = AutomaticLocationLabel;
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

        if (OperatingSystem.IsWindows())
        {
            WindowsAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
        }
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
                await _client.RefreshIpAsync(updateStatusMessage: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Startup IP lookup failed: {ex.Message}"));
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _client.EnsureDependenciesAsync().ConfigureAwait(false);
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
            SaveSettings();
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

        var options = BuildConnectOptions();

        try
        {
            if (OperatingSystem.IsWindows() && OnionDnsProxyEnabled && !WindowsAdmin.IsAdministrator())
            {
                StatusMessage = LocalizationService.Get("Status.AdminRequiredRequesting");
                if (!WindowsUacHelper.TryElevate())
                {
                    StatusMessage = LocalizationService.Get("Status.AdminRequiredCanceled");
                }
                return;
            }

            // Only prompt for elevation when the user actually tries to connect in TUN mode.
            if (OperatingSystem.IsWindows() && IsTunMode && !WindowsAdmin.IsAdministrator())
            {
                StatusMessage = LocalizationService.Get("Status.AdminRequiredRequesting");
                OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: Calling EnsureAdminHelperAsync...");
                
                if (!await _client.EnsureAdminHelperAsync().ConfigureAwait(false))
                {
                    OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: EnsureAdminHelperAsync returned false");
                    StatusMessage = LocalizationService.Get("Status.AdminRequiredCanceled");
                    return;
                }
                
                OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: EnsureAdminHelperAsync succeeded, calling ConnectAsync...");
            }

            OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: Calling _client.ConnectAsync...");
            await _client.ConnectAsync(options, _connectCts.Token);
            OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: _client.ConnectAsync completed");
        }
        catch (Exception ex)
        {
            OnionHopV2.Core.Services.StartupLogger.Write($"ConnectAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            OnionHopV2.Core.Services.StartupLogger.Write($"Stack trace: {ex.StackTrace}");
            StatusMessage = $"Connection failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        await _client.DisconnectAsync();
    }

    [RelayCommand]
    private void CancelConnect()
    {
        if (!IsConnecting)
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
    private async Task ChangeIdentityAsync()
    {
        await _client.ChangeIdentityAsync(CancellationToken.None);
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

            SelectedLocation = AutomaticLocationLabel;
            SelectedEntryLocation = AutomaticLocationLabel;
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
            RestrictedFirewallMode = false;
            AllowedPorts = DefaultAllowedPorts;
            OnionDnsProxyEnabled = false;
            MaxCircuitInactivityMinutes = 10;
            OpenConnectedPageEnabled = false;
            ConnectedPageUrl = DefaultConnectedPageUrl;
            OpenDisconnectedPageEnabled = false;
            DisconnectedPageUrl = DefaultDisconnectedPageUrl;
            EnableDiscordStatus = false;

            HybridRouteAllWebTraffic = true;
            HybridBlockQuicForTorApps = true;
            HybridTorApps = string.Empty;
            HybridBypassApps = string.Empty;

            IsDarkMode = true;
            UseNativeTheme = false;
        }
        finally
        {
            _loadingSettings = false;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                WindowsAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to update Windows startup after reset: {ex.Message}");
            }
        }

        ApplyTheme();
        SaveSettings();
        StatusMessage = LocalizationService.Get("Status.DefaultsRestored");
    }

    private OnionHopConnectOptions BuildConnectOptions()
    {
        return new OnionHopConnectOptions
        {
            SelectedLocation = SelectedLocation,
            SelectedEntryLocation = SelectedEntryLocation,
            ExitNodeFingerprint = ExitNodeFingerprint,
            SelectedConnectionMode = SelectedConnectionMode,
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
            RestrictedFirewallMode = RestrictedFirewallMode,
            AllowedPorts = AllowedPorts,
            OnionDnsProxyEnabled = OnionDnsProxyEnabled,
            MaxCircuitInactivityMinutes = MaxCircuitInactivityMinutes,
            OpenConnectedPageEnabled = OpenConnectedPageEnabled,
            ConnectedPageUrl = ConnectedPageUrl,
            OpenDisconnectedPageEnabled = OpenDisconnectedPageEnabled,
            DisconnectedPageUrl = DisconnectedPageUrl,
            EnableDiscordStatus = EnableDiscordStatus,
            HybridRouteAllWebTraffic = HybridRouteAllWebTraffic,
            HybridBlockQuicForTorApps = HybridBlockQuicForTorApps,
            HybridTorApps = HybridTorApps,
            HybridBypassApps = HybridBypassApps
        };
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

        if (e.PropertyName == nameof(IsDarkMode) || e.PropertyName == nameof(UseNativeTheme))
        {
            ApplyTheme();
        }
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
                SaveSettings();
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
            IsDarkMode = settings.IsDarkMode;
            UseNativeTheme = settings.UseNativeTheme;

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

            ExitNodeFingerprint = settings.ExitNodeFingerprint ?? string.Empty;

            SelectedConnectionMode = string.IsNullOrWhiteSpace(settings.SelectedConnectionMode) ? ConnectionModeProxy : settings.SelectedConnectionMode;
            if (!ConnectionModes.Contains(SelectedConnectionMode))
            {
                SelectedConnectionMode = ConnectionModeProxy;
            }

            UseHybridRouting = settings.UseHybridRouting;
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
            RestrictedFirewallMode = settings.RestrictedFirewallMode;
            AllowedPorts = string.IsNullOrWhiteSpace(settings.AllowedPorts) ? DefaultAllowedPorts : settings.AllowedPorts;
            OnionDnsProxyEnabled = settings.OnionDnsProxyEnabled;
            MaxCircuitInactivityMinutes = settings.MaxCircuitInactivityMinutes ?? 10;
            OpenConnectedPageEnabled = settings.OpenConnectedPageEnabled;
            ConnectedPageUrl = string.IsNullOrWhiteSpace(settings.ConnectedPageUrl) ? DefaultConnectedPageUrl : settings.ConnectedPageUrl;
            OpenDisconnectedPageEnabled = settings.OpenDisconnectedPageEnabled;
            DisconnectedPageUrl = string.IsNullOrWhiteSpace(settings.DisconnectedPageUrl) ? DefaultDisconnectedPageUrl : settings.DisconnectedPageUrl;
            EnableDiscordStatus = settings.EnableDiscordStatus;

            HybridRouteAllWebTraffic = settings.HybridRouteAllWebTraffic ?? true;
            HybridBlockQuicForTorApps = settings.HybridBlockQuicForTorApps ?? true;
            HybridTorApps = settings.HybridTorApps ?? string.Empty;
            HybridBypassApps = settings.HybridBypassApps ?? string.Empty;
            var language = string.IsNullOrWhiteSpace(settings.LanguageCode) ? "en" : settings.LanguageCode!;
            SelectedLanguage = language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        }
        finally
        {
            _loadingSettings = false;
        }
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

    private void SaveSettings()
    {
        if (_disposed)
        {
            return;
        }

        var startWithWindows = !string.Equals(AutoStartMode, AutoStartModeOff, StringComparison.OrdinalIgnoreCase);
        var startMinimized = string.Equals(AutoStartMode, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase);

        var settings = new UserSettings
        {
            AutoConnect = AutoConnect,
            AutoStartMode = AutoStartMode,
            StartWithWindows = startWithWindows,
            StartMinimized = startMinimized,
            MinimizeToTray = MinimizeToTray,
            AutoUpdate = AutoUpdate,
            KillSwitchEnabled = KillSwitchEnabled,
            IsDarkMode = IsDarkMode,
            UseNativeTheme = UseNativeTheme,
            SelectedLocation = SelectedLocation,
            SelectedEntryLocation = SelectedEntryLocation,
            ExitNodeFingerprint = ExitNodeFingerprint,
            SelectedConnectionMode = SelectedConnectionMode,
            UseHybridRouting = UseHybridRouting,
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
            RestrictedFirewallMode = RestrictedFirewallMode,
            AllowedPorts = AllowedPorts,
            OnionDnsProxyEnabled = OnionDnsProxyEnabled,
            MaxCircuitInactivityMinutes = MaxCircuitInactivityMinutes,
            OpenConnectedPageEnabled = OpenConnectedPageEnabled,
            ConnectedPageUrl = ConnectedPageUrl,
            OpenDisconnectedPageEnabled = OpenDisconnectedPageEnabled,
            DisconnectedPageUrl = DisconnectedPageUrl,
            EnableDiscordStatus = EnableDiscordStatus,
            HybridRouteAllWebTraffic = HybridRouteAllWebTraffic,
            HybridBlockQuicForTorApps = HybridBlockQuicForTorApps,
            HybridTorApps = HybridTorApps,
            HybridBypassApps = HybridBypassApps,
            LanguageCode = SelectedLanguage
        };

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

        Application.Current.RequestedThemeVariant = IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void RefreshLanguageOptions()
    {
        if (LanguageOptions.Count == 0)
        {
            LanguageOptions.Add(new LocalizedOption("en", "English"));
            LanguageOptions.Add(new LocalizedOption("de", "Deutsch"));
        }

        var targetIndex = string.Equals(SelectedLanguage, "de", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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
        RefreshTorAdvancedModeOptions();

        ConnectionModeOptions.Clear();
        ConnectionModeOptions.Add(new LocalizedOption(ConnectionModeProxy, LocalizationService.Get("Home.ModeProxy")));
        ConnectionModeOptions.Add(new LocalizedOption(ConnectionModeTun, LocalizationService.Get("Home.ModeTun")));
        SelectedConnectionModeOption = ConnectionModeOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedConnectionMode, StringComparison.Ordinal))
                                     ?? ConnectionModeOptions.FirstOrDefault();

        LocationOptions.Clear();
        foreach (var location in Locations)
        {
            var label = GetLocationLabel(location);
            LocationOptions.Add(new LocalizedOption(location, label));
        }

        SelectedLocationOption = LocationOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedLocation, StringComparison.Ordinal))
                              ?? LocationOptions.FirstOrDefault();
        SelectedEntryLocationOption = LocationOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedEntryLocation, StringComparison.Ordinal))
                                   ?? LocationOptions.FirstOrDefault();

        RefreshBridgeSourceOptions();
        RefreshBridgeTypeOptions();
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

    private void RefreshAutoStartModeOptions()
    {
        AutoStartModeOptions.Clear();
        AutoStartModeOptions.Add(new LocalizedOption(AutoStartModeOff, LocalizationService.Get("Settings.AutoStartOff")));
        AutoStartModeOptions.Add(new LocalizedOption(AutoStartModeOn, LocalizationService.Get("Settings.AutoStartOn")));
        AutoStartModeOptions.Add(new LocalizedOption(AutoStartModeMinimized, LocalizationService.Get("Settings.AutoStartMinimized")));
        SelectedAutoStartModeOption = AutoStartModeOptions.FirstOrDefault(option => string.Equals(option.Value, AutoStartMode, StringComparison.OrdinalIgnoreCase))
                                    ?? AutoStartModeOptions.FirstOrDefault();
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

        RefreshLocalizedOptions();
    }

    private static string LocalizeBridgeType(string bridgeType)
    {
        return bridgeType.ToLowerInvariant() switch
        {
            "automatic" => LocalizationService.Get("BridgeType.Automatic"),
            "obfs4" => LocalizationService.Get("BridgeType.Obfs4"),
            "snowflake" => LocalizationService.Get("BridgeType.Snowflake"),
            "conjure" => LocalizationService.Get("BridgeType.Conjure"),
            "webtunnel" => LocalizationService.Get("BridgeType.Webtunnel"),
            "meek-azure" => LocalizationService.Get("BridgeType.MeekAzure"),
            "custom" => LocalizationService.Get("BridgeType.Custom"),
            _ => bridgeType
        };
    }

    private static string LocalizeBridgeSourceMode(string sourceMode)
    {
        return sourceMode switch
        {
            BridgeSourceAuto => LocalizationService.Get("BridgeSource.Auto"),
            BridgeSourceBridgeDbOnly => LocalizationService.Get("BridgeSource.BridgeDbOnly"),
            BridgeSourceOfflineOnly => LocalizationService.Get("BridgeSource.OfflineOnly"),
            _ => sourceMode
        };
    }

    private static string NormalizeBridgeSourceMode(string? sourceMode)
    {
        if (string.IsNullOrWhiteSpace(sourceMode))
        {
            return BridgeSourceAuto;
        }

        if (string.Equals(sourceMode, BridgeSourceBridgeDbOnly, StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourceBridgeDbOnly;
        }

        if (string.Equals(sourceMode, BridgeSourceOfflineOnly, StringComparison.OrdinalIgnoreCase))
        {
            return BridgeSourceOfflineOnly;
        }

        return BridgeSourceAuto;
    }

    private static string LocalizeRuntimeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        return RuntimeStatusResourceMap.TryGetValue(normalized, out var key)
            ? LocalizationService.Get(key)
            : value;
    }

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
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        LogLines.Add(line);
        TrimLogs(LogLines);
    }

    public void AppendDnsLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        DnsLogLines.Add(line);
        TrimLogs(DnsLogLines);
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
        for (var i = 0; i < toRemove; i++)
        {
            list.RemoveAt(0);
        }
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
