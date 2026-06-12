using System;
using System.IO;
using System.Text.Json;
using OnionHopV3.Core;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class SettingsServiceTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "OnionHopV3.Tests", Path.GetRandomFileName());
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
                CustomDohHost = "https://example.com/dns-query",
                AllowLanProxyAccess = true,
                TunCoreMode = OnionHopConnectOptions.TunCoreXray,
                TunStackMode = OnionHopConnectOptions.TunStackSystem,
                TunMtu = 1400,
                TunStrictRoute = false,
                ConnectionTimeoutSeconds = 0
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
            Assert.Equal(original.AllowLanProxyAccess, loaded.AllowLanProxyAccess);
            Assert.Equal(original.TunCoreMode, loaded.TunCoreMode);
            Assert.Equal(original.TunStackMode, loaded.TunStackMode);
            Assert.Equal(original.TunMtu, loaded.TunMtu);
            Assert.Equal(original.TunStrictRoute, loaded.TunStrictRoute);
            Assert.Equal(original.ConnectionTimeoutSeconds, loaded.ConnectionTimeoutSeconds);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Load_returns_null_and_quarantines_a_corrupt_file()
    {
        var dir = CreateTempDir();
        try
        {
            var service = new SettingsService(dir);
            File.WriteAllText(service.SettingsPath, "{ this is not valid json");

            var result = service.Load();

            Assert.Null(result);
            // The corrupt file must be moved aside, not left in place to crash the next launch.
            Assert.False(File.Exists(service.SettingsPath));
            Assert.NotEmpty(Directory.GetFiles(dir, "settings.json.corrupt-*"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Load_does_not_throw_on_wrong_typed_value()
    {
        var dir = CreateTempDir();
        try
        {
            var service = new SettingsService(dir);
            // PreferredSocksPort is int? but the file holds a string - must not crash startup.
            File.WriteAllText(service.SettingsPath, "{\"PreferredSocksPort\":\"not-a-number\"}");

            var ex = Record.Exception(() => service.Load());

            Assert.Null(ex);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Save_then_Load_round_trips_upstream_proxy_password_without_storing_plaintext()
    {
        var dir = CreateTempDir();
        try
        {
            var service = new SettingsService(dir);
            service.Save(new UserSettings { UpstreamProxyPassword = "s3cr3t-pw" });

            // On Windows the password is DPAPI-encrypted, so the plaintext must not be on disk.
            var raw = File.ReadAllText(service.SettingsPath);
            if (OperatingSystem.IsWindows())
            {
                Assert.DoesNotContain("s3cr3t-pw", raw);
            }

            // Either way it round-trips back to plaintext in memory.
            var loaded = service.Load();
            Assert.NotNull(loaded);
            Assert.Equal("s3cr3t-pw", loaded!.UpstreamProxyPassword);
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
