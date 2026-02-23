using System.Text.Json;
using OnionHopV2.Core;
using OnionHopV2.Core.Models;
using Xunit;

namespace OnionHopV2.Tests.Models;

public sealed class UserSettingsTests
{
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions LoadOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Round_trip_serialization_preserves_all_properties()
    {
        var original = new UserSettings
        {
            AutoConnect = true,
            AutoStartMode = "On (Minimized)",
            StartWithWindows = true,
            StartMinimized = true,
            MinimizeToTray = true,
            AutoUpdate = true,
            KillSwitchEnabled = false,
            ThemeMode = "system",
            IsDarkMode = true,
            UseNativeTheme = false,
            SelectedLocation = "United States",
            SelectedEntryLocation = "Germany",
            SelectedConnectionMode = "Proxy Mode (Recommended)",
            UseHybridRouting = true,
            UseTorBridges = true,
            UseCensoredMode = false,
            SelectedBridgeType = "snowflake",
            BridgeSourceMode = OnionHopConnectOptions.BridgeSourceAuto,
            CustomBridges = "snowflake 1.2.3.4:443",
            CustomSniHosts = "sni.example.com",
            UseSnowflakeAmp = true,
            SnowflakeAmpCache = "cache",
            TorIpv6Mode = "auto",
            HardwareAccelerationMode = "auto",
            ConnectionPaddingMode = "auto",
            SelectedDnsProvider = "Quad9 (DoH)",
            CustomDohHost = "https://dns.quad9.net/dns-query",
            CustomDohPath = "/dns-query",
            ProxyScopeMode = OnionHopConnectOptions.ProxyScopeLocalOnly,
            PreferredSocksPort = 19050,
            PreferredHttpPort = 19080,
            AllowLanProxyAccess = true,
            ConnectionTimeoutSeconds = 0,
            StrictManualExitNodeFingerprint = false,
            ShowAdvancedHomeConnectionDetails = true,
            HybridRouteAllWebTraffic = true,
            HybridBlockQuicForTorApps = false,
            HybridTorApps = "firefox.exe",
            HybridBypassApps = "slack.exe"
        };

        var json = JsonSerializer.Serialize(original, SaveOptions);
        var deserialized = JsonSerializer.Deserialize<UserSettings>(json, LoadOptions);
        Assert.NotNull(deserialized);

        Assert.Equal(original.AutoConnect, deserialized.AutoConnect);
        Assert.Equal(original.AutoStartMode, deserialized.AutoStartMode);
        Assert.Equal(original.ThemeMode, deserialized.ThemeMode);
        Assert.Equal(original.SelectedLocation, deserialized.SelectedLocation);
        Assert.Equal(original.CustomBridges, deserialized.CustomBridges);
        Assert.Equal(original.BridgeSourceMode, deserialized.BridgeSourceMode);
        Assert.Equal(original.CustomDohHost, deserialized.CustomDohHost);
        Assert.Equal(original.ProxyScopeMode, deserialized.ProxyScopeMode);
        Assert.Equal(original.PreferredSocksPort, deserialized.PreferredSocksPort);
        Assert.Equal(original.PreferredHttpPort, deserialized.PreferredHttpPort);
        Assert.Equal(original.AllowLanProxyAccess, deserialized.AllowLanProxyAccess);
        Assert.Equal(original.ConnectionTimeoutSeconds, deserialized.ConnectionTimeoutSeconds);
        Assert.Equal(original.StrictManualExitNodeFingerprint, deserialized.StrictManualExitNodeFingerprint);
        Assert.Equal(original.ShowAdvancedHomeConnectionDetails, deserialized.ShowAdvancedHomeConnectionDetails);
        Assert.Equal(original.HybridTorApps, deserialized.HybridTorApps);
    }

    [Fact]
    public void Deserialize_empty_json_produces_defaults()
    {
        var loaded = JsonSerializer.Deserialize<UserSettings>("{}", LoadOptions);
        Assert.NotNull(loaded);
        Assert.False(loaded.AutoConnect);
        Assert.False(loaded.StartWithWindows);
        Assert.Null(loaded.SelectedLocation);
    }
}
