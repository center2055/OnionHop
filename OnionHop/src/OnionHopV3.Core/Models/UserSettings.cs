namespace OnionHopV3.Core.Models;

public sealed class UserSettings
{
    public int UiSchemaVersion { get; set; }
    public bool AutoConnect { get; set; }
    public string? AutoStartMode { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool AutoUpdate { get; set; }
    public bool KillSwitchEnabled { get; set; }
    public string? ThemeMode { get; set; }
    public bool IsDarkMode { get; set; }
    public bool UseNativeTheme { get; set; }
    public string? SelectedLocation { get; set; }
    public string? SelectedEntryLocation { get; set; }
    public string? EntryNodeFingerprint { get; set; }
    public string? MiddleNodeFingerprint { get; set; }
    public string? ExitNodeFingerprint { get; set; }
    public string? SelectedConnectionMode { get; set; }
    public bool UseHybridRouting { get; set; }
    public bool? SmartConnectEnabled { get; set; }
    public bool UseTorBridges { get; set; }
    public bool UseCensoredMode { get; set; }
    public string? SelectedBridgeType { get; set; }
    public string? BridgeSourceMode { get; set; }
    public string? CustomBridges { get; set; }
    public string? CustomSniHosts { get; set; }
    public bool UseSnowflakeAmp { get; set; }
    public string? SnowflakeAmpCache { get; set; }
    public string? TorIpv6Mode { get; set; }
    public string? HardwareAccelerationMode { get; set; }
    public string? ConnectionPaddingMode { get; set; }
    public string? SelectedDnsProvider { get; set; }
    public string? CustomDohHost { get; set; }
    public string? CustomDohPath { get; set; }
    public string? ProxyScopeMode { get; set; }
    // Upstream proxy that Tor dials through (lets OnionHop run behind another SOCKS5/HTTPS proxy).
    public bool? UpstreamProxyEnabled { get; set; }
    public bool? UpstreamProxyUseHttps { get; set; }
    public string? UpstreamProxyHost { get; set; }
    public string? UpstreamProxyPort { get; set; }
    public string? UpstreamProxyUsername { get; set; }
    public string? UpstreamProxyPassword { get; set; }
    // Desired post-connect system-proxy state for Proxy Mode (null defaults to enabled).
    public bool? SystemProxyEnabledByDefault { get; set; }
    public int? PreferredSocksPort { get; set; }
    public int? PreferredHttpPort { get; set; }
    public bool AllowLanProxyAccess { get; set; }
    public string? TunCoreMode { get; set; }
    public string? TunStackMode { get; set; }
    public int? TunMtu { get; set; }
    public bool? TunStrictRoute { get; set; }
    public int? ConnectionTimeoutSeconds { get; set; }
    public bool RestrictedFirewallMode { get; set; }
    public string? AllowedPorts { get; set; }
    public bool OnionDnsProxyEnabled { get; set; }
    public bool? StrictManualEntryNodeFingerprint { get; set; }
    public bool? StrictManualMiddleNodeFingerprint { get; set; }
    public bool? StrictManualExitNodeFingerprint { get; set; }
    public bool ShowAdvancedHomeConnectionDetails { get; set; }
    public int? MaxCircuitInactivityMinutes { get; set; }
    public bool OpenConnectedPageEnabled { get; set; }
    public string? ConnectedPageUrl { get; set; }
    public bool OpenDisconnectedPageEnabled { get; set; }
    public string? DisconnectedPageUrl { get; set; }
    public bool EnableDiscordStatus { get; set; }
    public bool? HybridRouteAllWebTraffic { get; set; }
    public bool? HybridBlockQuicForTorApps { get; set; }
    public bool? BlockUdpTraffic { get; set; }
    public string? HybridTorApps { get; set; }
    public string? HybridBypassApps { get; set; }
    public string? LanguageCode { get; set; }
    public string? AccentColor { get; set; }
    public string? TorEngineMode { get; set; }
    public string? RelayRefreshInterval { get; set; }
    public string? UpdateChannel { get; set; }
    public bool ClearSessionDataOnDisconnect { get; set; }
    public bool? DnsLeakProtectionEnabled { get; set; }
    public bool ClipboardProtectionEnabled { get; set; }

    // Opt-in (default false): when true, OnionHop installs an at-logon scheduled task that keeps an
    // elevated helper running so TUN/VPN mode does not prompt for UAC on every connect. When false,
    // no startup task is installed and any leftover task is removed.
    public bool PersistentAdminHelperEnabled { get; set; }

    // Snowflake proxy (volunteer as a Snowflake bridge).
    public bool SnowflakeProxyAutoStart { get; set; }
    public int? SnowflakeProxyCapacity { get; set; }
}
