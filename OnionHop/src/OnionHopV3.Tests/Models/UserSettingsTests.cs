using System.Text.Json;
using OnionHopV3.Core;
using OnionHopV3.Core.Models;
using Xunit;

namespace OnionHopV3.Tests.Models;

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
            TunCoreMode = OnionHopConnectOptions.TunCoreXray,
            TunStackMode = OnionHopConnectOptions.TunStackGvisor,
            TunMtu = 1420,
            TunStrictRoute = false,
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
        Assert.Equal(original.TunCoreMode, deserialized.TunCoreMode);
        Assert.Equal(original.TunStackMode, deserialized.TunStackMode);
        Assert.Equal(original.TunMtu, deserialized.TunMtu);
        Assert.Equal(original.TunStrictRoute, deserialized.TunStrictRoute);
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

    [Fact]
    public void PersistentAdminHelperEnabled_defaults_to_false()
    {
        // The persistent admin helper is opt-in: a fresh settings object and missing-key JSON must
        // both leave it off so no startup task is ever installed by default.
        Assert.False(new UserSettings().PersistentAdminHelperEnabled);

        var loaded = JsonSerializer.Deserialize<UserSettings>("{}", LoadOptions);
        Assert.NotNull(loaded);
        Assert.False(loaded.PersistentAdminHelperEnabled);

        // The connect option that drives the gate also defaults off.
        Assert.False(new OnionHopConnectOptions().PersistentAdminHelperEnabled);
    }

    [Fact]
    public void PersistentAdminHelperEnabled_round_trips()
    {
        var original = new UserSettings { PersistentAdminHelperEnabled = true };
        var json = JsonSerializer.Serialize(original, SaveOptions);
        var deserialized = JsonSerializer.Deserialize<UserSettings>(json, LoadOptions);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.PersistentAdminHelperEnabled);
    }
}
