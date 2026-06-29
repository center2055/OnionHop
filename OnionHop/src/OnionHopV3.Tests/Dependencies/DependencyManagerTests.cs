using System;
using System.IO;
using OnionHopV3.Core.Dependencies;
using Xunit;

namespace OnionHopV3.Tests.Dependencies;

public sealed class DependencyManagerTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "OnionHopV3.Tests", Path.GetRandomFileName());
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

    [Fact]
    public void TrySeedBundledRuntime_refreshes_stale_bridge_lists_but_leaves_other_files()
    {
        var bundleDir = CreateTempDir();
        var runtimeDir = CreateTempDir();

        try
        {
            var bundlePtDir = Path.Combine(bundleDir, "tor", "pluggable_transports");
            var runtimePtDir = Path.Combine(runtimeDir, "tor", "pluggable_transports");
            Directory.CreateDirectory(bundlePtDir);
            Directory.CreateDirectory(runtimePtDir);

            // Bundle ships refreshed bridge lists; runtime has stale copies (the upgrade scenario that
            // left users connecting through a months-old, mostly-dead webtunnel list).
            File.WriteAllText(Path.Combine(bundlePtDir, "bridges-community-webtunnel.txt"), "webtunnel NEW");
            File.WriteAllText(Path.Combine(bundlePtDir, "bridges-webtunnel.txt"), "webtunnel NEW");
            File.WriteAllText(Path.Combine(bundlePtDir, "lyrebird.exe"), "lyrebird-new");
            File.WriteAllText(Path.Combine(runtimePtDir, "bridges-community-webtunnel.txt"), "webtunnel OLD STALE");
            File.WriteAllText(Path.Combine(runtimePtDir, "bridges-webtunnel.txt"), "webtunnel OLD STALE");
            File.WriteAllText(Path.Combine(runtimePtDir, "lyrebird.exe"), "lyrebird-old");

            DependencyManager.TrySeedBundledRuntime(
                runtimeDir,
                includeVpnDependencies: false,
                _ => { },
                [bundleDir]);

            // Stale bundled bridge lists are refreshed from the bundle (the bug: they used to be skipped
            // because the file already existed, so the app kept using the old dead set forever).
            Assert.Equal("webtunnel NEW", File.ReadAllText(Path.Combine(runtimePtDir, "bridges-community-webtunnel.txt")));
            Assert.Equal("webtunnel NEW", File.ReadAllText(Path.Combine(runtimePtDir, "bridges-webtunnel.txt")));
            // Only bridge lists are force-refreshed; other existing runtime files are left untouched.
            Assert.Equal("lyrebird-old", File.ReadAllText(Path.Combine(runtimePtDir, "lyrebird.exe")));
        }
        finally
        {
            try { Directory.Delete(bundleDir, true); } catch { }
            try { Directory.Delete(runtimeDir, true); } catch { }
        }
    }

    [Theory]
    // Regression for #42/#65: a stale Windows "lyrebird.exe" line must NOT count as referencing the
    // macOS/Linux "lyrebird" binary (otherwise the per-platform fix-up skips it and obfs4/conjure/
    // snowflake keep pointing at a nonexistent ".exe", so the managed proxy dies and bridges fail).
    [InlineData("ClientTransportPlugin obfs4 exec ${pt_path}lyrebird.exe", "lyrebird", false)]
    [InlineData("ClientTransportPlugin obfs4 exec ${pt_path}lyrebird", "lyrebird", true)]
    [InlineData("ClientTransportPlugin obfs4 exec ${pt_path}lyrebird.exe", "lyrebird.exe", true)]
    [InlineData("ClientTransportPlugin obfs4 exec /Users/x/Application Support/pt/lyrebird", "lyrebird", true)]
    [InlineData("ClientTransportPlugin webtunnel exec ${pt_path}webtunnel-client.exe", "webtunnel-client", false)]
    [InlineData("ClientTransportPlugin webtunnel exec ${pt_path}webtunnel-client", "webtunnel-client", true)]
    // conjure (#64): a stale "conjure -> lyrebird" line must not count as referencing conjure-client,
    // so it gets rewritten; and the correct line (with the -registerURL arg after it) must match.
    [InlineData("ClientTransportPlugin conjure exec ${pt_path}lyrebird.exe", "conjure-client", false)]
    [InlineData("ClientTransportPlugin conjure exec ${pt_path}conjure-client -registerURL https://x/api", "conjure-client", true)]
    public void TransportLineReferencesBinary_matches_only_complete_filename(string line, string binary, bool expected)
    {
        Assert.Equal(expected, DependencyManager.TransportLineReferencesBinary(line, binary));
    }
}
