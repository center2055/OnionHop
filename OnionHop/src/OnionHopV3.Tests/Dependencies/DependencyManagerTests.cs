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
    public void TrySeedBundledRuntime_replaces_files_that_differ_from_the_bundle()
    {
        var bundleDir = CreateTempDir();
        var runtimeDir = CreateTempDir();

        try
        {
            var bundleTorDir = Path.Combine(bundleDir, "tor");
            var bundlePtDir = Path.Combine(bundleTorDir, "pluggable_transports");
            var bundleVpnDir = Path.Combine(bundleDir, "vpn");
            var runtimeTorDir = Path.Combine(runtimeDir, "tor");
            var runtimePtDir = Path.Combine(runtimeTorDir, "pluggable_transports");
            var runtimeVpnDir = Path.Combine(runtimeDir, "vpn");
            Directory.CreateDirectory(bundlePtDir);
            Directory.CreateDirectory(bundleVpnDir);
            Directory.CreateDirectory(runtimePtDir);
            Directory.CreateDirectory(runtimeVpnDir);

            // The upgrade case: the runtime folder still holds what an earlier version shipped (#93
            // showed Tor 0.4.9.6 running two releases after the app had moved on).
            File.WriteAllText(Path.Combine(bundleTorDir, "tor.exe"), "tor 0.4.9.14");
            File.WriteAllText(Path.Combine(bundlePtDir, "lyrebird.exe"), "lyrebird-new");
            File.WriteAllText(Path.Combine(bundlePtDir, "bridges-webtunnel.txt"), "webtunnel NEW");
            File.WriteAllText(Path.Combine(bundleVpnDir, "sing-box.exe"), "sing-box 1.13.13");
            File.WriteAllText(Path.Combine(runtimeTorDir, "tor.exe"), "tor 0.4.9.6");
            File.WriteAllText(Path.Combine(runtimePtDir, "lyrebird.exe"), "lyrebird-old");
            File.WriteAllText(Path.Combine(runtimePtDir, "bridges-webtunnel.txt"), "webtunnel OLD STALE");
            File.WriteAllText(Path.Combine(runtimeVpnDir, "sing-box.exe"), "sing-box 1.12.0");
            // Files only the runtime folder has (caches, state) stay.
            File.WriteAllText(Path.Combine(runtimeTorDir, "cached-bridges.txt"), "runtime only");

            var copied = DependencyManager.TrySeedBundledRuntime(
                runtimeDir,
                includeVpnDependencies: true,
                _ => { },
                [bundleDir]);

            Assert.True(copied);
            Assert.Equal("tor 0.4.9.14", File.ReadAllText(Path.Combine(runtimeTorDir, "tor.exe")));
            Assert.Equal("lyrebird-new", File.ReadAllText(Path.Combine(runtimePtDir, "lyrebird.exe")));
            Assert.Equal("webtunnel NEW", File.ReadAllText(Path.Combine(runtimePtDir, "bridges-webtunnel.txt")));
            Assert.Equal("sing-box 1.13.13", File.ReadAllText(Path.Combine(runtimeVpnDir, "sing-box.exe")));
            Assert.Equal("runtime only", File.ReadAllText(Path.Combine(runtimeTorDir, "cached-bridges.txt")));
            Assert.Empty(Directory.GetFiles(runtimeDir, "*.new", SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(bundleDir, true); } catch { }
            try { Directory.Delete(runtimeDir, true); } catch { }
        }
    }

    [Fact]
    public void TrySeedBundledRuntime_leaves_the_runtime_copy_alone_while_the_bundle_is_unchanged()
    {
        var bundleDir = CreateTempDir();
        var runtimeDir = CreateTempDir();

        try
        {
            var bundleTorDir = Path.Combine(bundleDir, "tor");
            var runtimeTorDir = Path.Combine(runtimeDir, "tor");
            Directory.CreateDirectory(bundleTorDir);
            File.WriteAllText(Path.Combine(bundleTorDir, "tor.exe"), "tor bundled");

            Assert.True(DependencyManager.TrySeedBundledRuntime(runtimeDir, includeVpnDependencies: false, _ => { }, [bundleDir]));

            // Changed after the sync, for example by hand: with the same bundle, nothing is compared or
            // copied again on the next start.
            File.WriteAllText(Path.Combine(runtimeTorDir, "tor.exe"), "tor replaced by hand");
            Assert.False(DependencyManager.TrySeedBundledRuntime(runtimeDir, includeVpnDependencies: false, _ => { }, [bundleDir]));
            Assert.Equal("tor replaced by hand", File.ReadAllText(Path.Combine(runtimeTorDir, "tor.exe")));

            // A new bundle (an app update) is followed again.
            File.WriteAllText(Path.Combine(bundleTorDir, "tor.exe"), "tor from the update");
            File.SetLastWriteTimeUtc(Path.Combine(bundleTorDir, "tor.exe"), DateTime.UtcNow.AddMinutes(1));
            Assert.True(DependencyManager.TrySeedBundledRuntime(runtimeDir, includeVpnDependencies: false, _ => { }, [bundleDir]));
            Assert.Equal("tor from the update", File.ReadAllText(Path.Combine(runtimeTorDir, "tor.exe")));
        }
        finally
        {
            try { Directory.Delete(bundleDir, true); } catch { }
            try { Directory.Delete(runtimeDir, true); } catch { }
        }
    }

    [Fact]
    public void TrySeedBundledRuntime_retries_a_file_that_was_in_use()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows refuses to replace a file that is open; elsewhere the rename goes through.
            return;
        }

        var bundleDir = CreateTempDir();
        var runtimeDir = CreateTempDir();

        try
        {
            var bundleTorDir = Path.Combine(bundleDir, "tor");
            var runtimeTorDir = Path.Combine(runtimeDir, "tor");
            Directory.CreateDirectory(bundleTorDir);
            Directory.CreateDirectory(runtimeTorDir);
            File.WriteAllText(Path.Combine(bundleTorDir, "tor.exe"), "tor new");
            File.WriteAllText(Path.Combine(bundleTorDir, "geoip"), "geoip new");
            File.WriteAllText(Path.Combine(runtimeTorDir, "tor.exe"), "tor old");
            File.WriteAllText(Path.Combine(runtimeTorDir, "geoip"), "geoip old");

            var log = new System.Collections.Generic.List<string>();
            using (new FileStream(Path.Combine(runtimeTorDir, "tor.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                DependencyManager.TrySeedBundledRuntime(runtimeDir, includeVpnDependencies: false, log.Add, [bundleDir]);
            }

            Assert.Equal("tor old", File.ReadAllText(Path.Combine(runtimeTorDir, "tor.exe")));
            Assert.Equal("geoip new", File.ReadAllText(Path.Combine(runtimeTorDir, "geoip")));
            Assert.Contains(log, line => line.Contains("tor.exe") && line.Contains("next start"));

            // No stamp was written, so the next start tries again and succeeds.
            DependencyManager.TrySeedBundledRuntime(runtimeDir, includeVpnDependencies: false, _ => { }, [bundleDir]);
            Assert.Equal("tor new", File.ReadAllText(Path.Combine(runtimeTorDir, "tor.exe")));
            Assert.Empty(Directory.GetFiles(runtimeDir, "*.new", SearchOption.AllDirectories));
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
