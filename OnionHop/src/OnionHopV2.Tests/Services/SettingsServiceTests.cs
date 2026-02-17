using System.IO;
using System.Text.Json;
using OnionHopV2.Core.Models;
using OnionHopV2.Core.Services;
using Xunit;

namespace OnionHopV2.Tests.Services;

public sealed class SettingsServiceTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "OnionHopV2.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Load_returns_null_when_file_does_not_exist()
    {
        var dir = CreateTempDir();
        try
        {
            var service = new SettingsService(dir);
            var result = service.Load();
            Assert.Null(result);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Save_and_Load_round_trips_settings()
    {
        var dir = CreateTempDir();
        try
        {
            var service = new SettingsService(dir);
            var original = new UserSettings
            {
                AutoConnect = true,
                AutoStartMode = "On",
                StartWithWindows = true,
                StartMinimized = true,
                MinimizeToTray = true,
                AutoUpdate = false,
                KillSwitchEnabled = true,
                ThemeMode = "dark",
                IsDarkMode = true,
                UseNativeTheme = false,
                SelectedLocation = "Germany",
                SelectedConnectionMode = "TUN/VPN Mode (Admin)",
                UseHybridRouting = true,
                UseTorBridges = true,
                UseCensoredMode = false,
                SelectedBridgeType = "obfs4",
                CustomBridges = "obfs4 1.2.3.4:1234 cert=abc",
                SelectedDnsProvider = "Cloudflare (DoH)",
                CustomDohHost = "https://example.com/dns-query"
            };

            service.Save(original);
            var loaded = service.Load();
            Assert.NotNull(loaded);
            Assert.Equal(original.AutoConnect, loaded.AutoConnect);
            Assert.Equal(original.AutoStartMode, loaded.AutoStartMode);
            Assert.Equal(original.ThemeMode, loaded.ThemeMode);
            Assert.Equal(original.SelectedLocation, loaded.SelectedLocation);
            Assert.Equal(original.CustomBridges, loaded.CustomBridges);
            Assert.Equal(original.CustomDohHost, loaded.CustomDohHost);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsPath_uses_provided_directory()
    {
        var dir = CreateTempDir();
        try
        {
            var service = new SettingsService(dir);
            Assert.StartsWith(dir, service.SettingsPath);
            Assert.EndsWith("settings.json", service.SettingsPath);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
