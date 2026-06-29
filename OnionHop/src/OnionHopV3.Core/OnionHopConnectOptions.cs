namespace OnionHopV3.Core;

public sealed record OnionHopConnectOptions
{
    public const string AutomaticLocationLabel = "Automatic";

    public const string ConnectionModeProxy = "Proxy Mode (Recommended)";
    public const string ConnectionModeTun = "TUN/VPN Mode (Admin)";
    public const string ProxyScopeSystem = "System proxy (all apps)";
    public const string ProxyScopeSystemSocks = "System proxy (SOCKS browser/.onion)";
    public const string ProxyScopeLocalOnly = "Local proxy only (manual apps)";

    // Upstream (outbound) proxy that Tor itself dials through — lets OnionHop sit *behind* another
    // proxy (a corporate/SOCKS gateway, Proxifier's output, a Psiphon-style front, etc.).
    public const string UpstreamProxyKindSocks5 = "socks5";
    public const string UpstreamProxyKindHttps = "https";

    public const string ToggleModeDefault = "Default";
    public const string ToggleModeEnabled = "Enabled";
    public const string ToggleModeDisabled = "Disabled";

    public const string ConnectionPaddingAuto = "Auto (recommended)";
    public const string ConnectionPaddingEnabled = "Enabled";
    public const string ConnectionPaddingDisabled = "Disabled";
    public const string TunStackMixed = "Mixed (recommended)";
    public const string TunStackSystem = "System";
    public const string TunStackGvisor = "gVisor";
    public const string TunCoreSingBox = "sing-box";
    public const string TunCoreXray = "xray";
    public const string TorEngineAutomatic = "automatic";
    public const string TorEngineClassic = "classic";
    public const string TorEngineArti = "arti";

    // ArtiHop: a separate Arti-based SOCKS runtime that uses shortened 2-hop (Guard -> Exit)
    // circuits for lower latency. Faster than standard 3-hop Tor, but weaker anonymity.
    public const string TorEngineArtiHop = "artihop";
    // ArtiHop circuit mode passed via --mode. "short-2" = 2 relays (Guard -> Exit).
    public const string ArtiHopShortMode = "short-2";
    // Standard 3-hop (Guard -> Middle -> Exit). REQUIRED when using bridges: a bridge isn't a
    // consensus relay, so a 2-hop "bridge -> exit" circuit is rejected by the exit with a TORPROTOCOL
    // stream error (Tor refuses exit streams whose immediate predecessor isn't a known relay). The
    // bridge must be the guard with a normal middle relay before the exit, i.e. at least 3 hops.
    public const string ArtiHopNormalMode = "normal";
    public const string BridgeSourceAuto = "Auto (Tor bridge service -> Offline)";
    public const string BridgeSourceOnlineOnly = "Tor bridge service only";
    public const string BridgeSourceOfflineOnly = "Offline only";
    public const string BridgeSourceCollectorOnly = "OnionHop collector only";

    public const string DnsProviderCloudflare = "Cloudflare (DoH)";
    public const string DnsProviderGoogle = "Google (DoH)";
    public const string DnsProviderQuad9 = "Quad9 (DoH)";
    public const string DnsProviderAuto = "Auto (best available DoH)";
    public const string DnsProviderAdGuard = "AdGuard (DoH)";
    public const string DnsProviderMullvad = "Mullvad (DoH)";
    public const string DnsProviderOpenDns = "OpenDNS (DoH)";
    public const string DnsProviderCustom = "Custom (DoH)";
    public const int DefaultSocksPort = 9050;
    public const int DefaultHttpPort = 9080;

    public string SelectedLocation { get; init; } = AutomaticLocationLabel;
    public string SelectedEntryLocation { get; init; } = AutomaticLocationLabel;
    public string? EntryNodeFingerprint { get; init; }
    public string? MiddleNodeFingerprint { get; init; }
    public string? ExitNodeFingerprint { get; init; }
    public string SelectedConnectionMode { get; init; } = ConnectionModeProxy;
    public string TorEngineMode { get; init; } = TorEngineAutomatic;

    public bool UseHybridRouting { get; init; }
    public bool KillSwitchEnabled { get; init; }

    public bool UseTorBridges { get; init; }
    public bool UseCensoredMode { get; init; }

    public string SelectedBridgeType { get; init; } = "obfs4";
    public string BridgeSourceMode { get; init; } = BridgeSourceAuto;
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
    public string ProxyScopeMode { get; init; } = ProxyScopeSystem;
    public int PreferredSocksPort { get; init; } = DefaultSocksPort;
    public int PreferredHttpPort { get; init; } = DefaultHttpPort;
    public bool AllowLanProxyAccess { get; init; }
    // Proxy Mode only. When false, connecting does NOT point the OS system proxy at Tor (apps can
    // still use the SOCKS port directly). Lets the user pre-decide post-connect behavior from Home.
    public bool ApplySystemProxyOnConnect { get; init; } = true;
    public string TunCoreMode { get; init; } = TunCoreSingBox;
    public string TunStackMode { get; init; } = TunStackMixed;
    public int? TunMtu { get; init; }
    public bool TunStrictRoute { get; init; } = true;
    public int? ConnectionTimeoutSeconds { get; init; }
    // Smart Connect sets this to fail a single strategy fast and move to the next one (a vetted,
    // reachable bridge bootstraps in well under this). It overrides the longer automatic default but
    // never overrides an explicit user-configured ConnectionTimeoutSeconds. Null = not set.
    public int? SmartConnectAttemptTimeoutSeconds { get; init; }

    public bool RestrictedFirewallMode { get; init; }
    public string? AllowedPorts { get; init; }

    public bool OnionDnsProxyEnabled { get; init; }

    // When true (Proxy Mode only), route ALL system DNS through Tor's DNSPort, not just
    // the .onion namespace. This closes the DNS leak where normal lookups (e.g. netflix.com)
    // would otherwise go to the system/ISP resolver. Requires the Tor DNSPort to be active
    // (driven by OnionDnsProxyEnabled) and elevated privileges to install the DNS rule.
    public bool FullDnsOverTor { get; init; }
    public bool StrictManualEntryNodeFingerprint { get; init; } = true;
    public bool StrictManualMiddleNodeFingerprint { get; init; } = true;
    public bool StrictManualExitNodeFingerprint { get; init; } = true;

    public int MaxCircuitInactivityMinutes { get; init; } = 10;

    public bool OpenConnectedPageEnabled { get; init; }
    public string? ConnectedPageUrl { get; init; }
    public bool OpenDisconnectedPageEnabled { get; init; }
    public string? DisconnectedPageUrl { get; init; }

    public bool EnableDiscordStatus { get; init; }

    // Hybrid mode split-tunneling (per-app routing) for TUN mode.
    public bool HybridRouteAllWebTraffic { get; init; } = true;
    public bool HybridBlockQuicForTorApps { get; init; } = true;
    public bool BlockUdpTraffic { get; init; } = true;
    public string? HybridTorApps { get; init; }
    public string? HybridBypassApps { get; init; }
    // User routing rules for TUN/VPN mode (issue #55): domains / IP ranges to send direct (bypass Tor)
    // or block. One entry per line or comma-separated; '#' lines ignored.
    public string? BypassRoutingRules { get; init; }
    public string? BlockRoutingRules { get; init; }
    // Country routing for TUN/VPN mode (issue #55): ISO country codes to keep direct (bypass Tor) or
    // block, matched by IP via sing-box geoip rule-sets. One code per line or comma-separated.
    public string? BypassCountries { get; init; }
    public string? BlockCountries { get; init; }
    // Domain-category routing for TUN/VPN mode (issue #55): sing-geosite categories to keep direct
    // (bypass Tor) or block, e.g. category-ads-all. One per line or comma-separated.
    public string? BypassSiteCategories { get; init; }
    public string? BlockSiteCategories { get; init; }

    // Opt-in (default false). When true, TUN/VPN connects may install an at-logon elevated helper task
    // so subsequent connects skip the UAC prompt. When false, no startup task is installed and any
    // leftover task from a prior version is removed.
    public bool PersistentAdminHelperEnabled { get; init; }

    // Upstream proxy: when enabled, Tor dials all of its outbound connections through this proxy.
    // Requires the Classic Tor engine. Kind is socks5 (default) or https (HTTP CONNECT).
    public bool UpstreamProxyEnabled { get; init; }
    public string UpstreamProxyKind { get; init; } = UpstreamProxyKindSocks5;
    public string? UpstreamProxyHost { get; init; }
    public int UpstreamProxyPort { get; init; }
    public string? UpstreamProxyUsername { get; init; }
    public string? UpstreamProxyPassword { get; init; }
}
