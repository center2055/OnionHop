namespace OnionHopV2.Core;

public sealed class OnionHopConnectOptions
{
    public const string AutomaticLocationLabel = "Automatic";

    public const string ConnectionModeProxy = "Proxy Mode (Recommended)";
    public const string ConnectionModeTun = "TUN/VPN Mode (Admin)";

    public const string ToggleModeDefault = "Default";
    public const string ToggleModeEnabled = "Enabled";
    public const string ToggleModeDisabled = "Disabled";

    public const string ConnectionPaddingAuto = "Auto (recommended)";
    public const string ConnectionPaddingEnabled = "Enabled";
    public const string ConnectionPaddingDisabled = "Disabled";

    public const string DnsProviderCloudflare = "Cloudflare (DoH)";
    public const string DnsProviderGoogle = "Google (DoH)";
    public const string DnsProviderQuad9 = "Quad9 (DoH)";
    public const string DnsProviderCustom = "Custom (DoH)";

    public string SelectedLocation { get; init; } = AutomaticLocationLabel;
    public string SelectedEntryLocation { get; init; } = AutomaticLocationLabel;
    public string? ExitNodeFingerprint { get; init; }
    public string SelectedConnectionMode { get; init; } = ConnectionModeProxy;

    public bool UseHybridRouting { get; init; }
    public bool KillSwitchEnabled { get; init; }

    public bool UseTorBridges { get; init; }
    public bool UseCensoredMode { get; init; }

    public string SelectedBridgeType { get; init; } = "obfs4";
    public string? CustomBridges { get; init; }
    public string? CustomSniHosts { get; init; }

    public bool UseSnowflakeAmp { get; init; }
    public string? SnowflakeAmpCache { get; init; }

    public string TorIpv6Mode { get; init; } = ToggleModeDefault;
    public string HardwareAccelerationMode { get; init; } = ToggleModeDefault;
    public string ConnectionPaddingMode { get; init; } = ConnectionPaddingAuto;

    public string SelectedDnsProvider { get; init; } = DnsProviderCloudflare;
    public string? CustomDohHost { get; init; }
    public string? CustomDohPath { get; init; }

    public bool RestrictedFirewallMode { get; init; }
    public string? AllowedPorts { get; init; }

    public bool OnionDnsProxyEnabled { get; init; }

    public int MaxCircuitInactivityMinutes { get; init; } = 10;

    public bool OpenConnectedPageEnabled { get; init; }
    public string? ConnectedPageUrl { get; init; }
    public bool OpenDisconnectedPageEnabled { get; init; }
    public string? DisconnectedPageUrl { get; init; }

    public bool EnableDiscordStatus { get; init; }

    // Hybrid mode split-tunneling (per-app routing) for TUN mode.
    public bool HybridRouteAllWebTraffic { get; init; } = true;
    public bool HybridBlockQuicForTorApps { get; init; } = true;
    public string? HybridTorApps { get; init; }
    public string? HybridBypassApps { get; init; }
}
