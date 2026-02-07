namespace OnionHopV2.Core.Models;

public sealed class UserSettings
{
    public bool AutoConnect { get; set; }
    public string? AutoStartMode { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool AutoUpdate { get; set; }
    public bool KillSwitchEnabled { get; set; }
    public bool IsDarkMode { get; set; }
    public bool UseNativeTheme { get; set; }
    public string? SelectedLocation { get; set; }
    public string? SelectedEntryLocation { get; set; }
    public string? SelectedConnectionMode { get; set; }
    public bool UseHybridRouting { get; set; }
    public bool UseTorBridges { get; set; }
    public bool UseCensoredMode { get; set; }
    public string? SelectedBridgeType { get; set; }
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
    public bool? HybridRouteAllWebTraffic { get; set; }
    public bool? HybridBlockQuicForTorApps { get; set; }
    public string? HybridTorApps { get; set; }
    public string? HybridBypassApps { get; set; }
    public string? LanguageCode { get; set; }
}
