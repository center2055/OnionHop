using System;
using System.IO;
using OnionHopV2.Core.Dependencies;
using Xunit;

namespace OnionHopV2.Tests.Dependencies;

public sealed class DependencyManagerTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "OnionHopV2.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void TrySeedBundledRuntime_restores_missing_tor_and_vpn_files_from_bundle()
    {
        var bundleDir = CreateTempDir();
        var runtimeDir = CreateTempDir();

        try
        {
            var bundleTorDir = Path.Combine(bundleDir, "tor");
            var bundlePtDir = Path.Combine(bundleTorDir, "pluggable_transports");
            var bundleVpnDir = Path.Combine(bundleDir, "vpn");
            Directory.CreateDirectory(bundlePtDir);
            Directory.CreateDirectory(bundleVpnDir);

            File.WriteAllText(Path.Combine(bundleTorDir, "tor.exe"), "tor");
            File.WriteAllText(Path.Combine(bundleTorDir, "geoip"), "geoip");
            File.WriteAllText(Path.Combine(bundleTorDir, "geoip6"), "geoip6");
            File.WriteAllText(Path.Combine(bundlePtDir, "pt_config.json"), "{}");
            File.WriteAllText(Path.Combine(bundlePtDir, "lyrebird.exe"), "lyrebird");
            File.WriteAllText(Path.Combine(bundlePtDir, "snowflake-client.exe"), "snowflake");
            File.WriteAllText(Path.Combine(bundlePtDir, "webtunnel-client.exe"), "webtunnel");
            File.WriteAllText(Path.Combine(bundleVpnDir, "sing-box.exe"), "sing-box");

            var copied = DependencyManager.TrySeedBundledRuntime(
                runtimeDir,
                includeVpnDependencies: true,
                _ => { },
                [bundleDir]);

            Assert.True(copied);
            Assert.True(File.Exists(Path.Combine(runtimeDir, "tor", "tor.exe")));
            Assert.True(File.Exists(Path.Combine(runtimeDir, "tor", "geoip")));
            Assert.True(File.Exists(Path.Combine(runtimeDir, "tor", "geoip6")));
            Assert.True(File.Exists(Path.Combine(runtimeDir, "tor", "pluggable_transports", "lyrebird.exe")));
            Assert.True(File.Exists(Path.Combine(runtimeDir, "tor", "pluggable_transports", "snowflake-client.exe")));
            Assert.True(File.Exists(Path.Combine(runtimeDir, "tor", "pluggable_transports", "webtunnel-client.exe")));
            Assert.True(File.Exists(Path.Combine(runtimeDir, "vpn", "sing-box.exe")));
        }
        finally
        {
            try { Directory.Delete(bundleDir, true); } catch { }
            try { Directory.Delete(runtimeDir, true); } catch { }
        }
    }

    [Fact]
    public void TrySeedBundledRuntime_preserves_existing_pt_config()
    {
        var bundleDir = CreateTempDir();
        var runtimeDir = CreateTempDir();

        try
        {
            var bundlePtDir = Path.Combine(bundleDir, "tor", "pluggable_transports");
            var runtimePtDir = Path.Combine(runtimeDir, "tor", "pluggable_transports");
            Directory.CreateDirectory(bundlePtDir);
            Directory.CreateDirectory(runtimePtDir);

            File.WriteAllText(Path.Combine(bundlePtDir, "pt_config.json"), "{\"bundled\":true}");
            File.WriteAllText(Path.Combine(bundlePtDir, "lyrebird.exe"), "lyrebird");
            File.WriteAllText(Path.Combine(runtimePtDir, "pt_config.json"), "{\"local\":true}");

            DependencyManager.TrySeedBundledRuntime(
                runtimeDir,
                includeVpnDependencies: false,
                _ => { },
                [bundleDir]);

            Assert.Equal("{\"local\":true}", File.ReadAllText(Path.Combine(runtimePtDir, "pt_config.json")));
            Assert.Equal("lyrebird", File.ReadAllText(Path.Combine(runtimePtDir, "lyrebird.exe")));
        }
        finally
        {
            try { Directory.Delete(bundleDir, true); } catch { }
            try { Directory.Delete(runtimeDir, true); } catch { }
        }
    }
}
