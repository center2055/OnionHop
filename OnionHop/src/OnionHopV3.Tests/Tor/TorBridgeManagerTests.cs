using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core;
using OnionHopV3.Core.Platform;
using OnionHopV3.Core.Tor;
using Xunit;

namespace OnionHopV3.Tests.Tor;

public sealed class TorBridgeManagerTests
{
    private static readonly object BundledWebTunnelLock = new();

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "OnionHopV3.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task GetBridgeLinesAsync_preserves_webtunnel_endpoint_in_custom_lines()
    {
        var dir = CreateTempDir();
        try
        {
            var manager = new TorBridgeManager(dir);
            var options = new OnionHopConnectOptions
            {
                SelectedBridgeType = "webtunnel",
                CustomBridges = "webtunnel [2001:db8:e524:683e:fb8d:e6ee:79c0:f397]:443 DA1ECF055635C1A6ED7F5B5F36296A5E3015CE57 url=https://np601p22.xoomlia.com/hlmb69xo/ ver=0.0.3"
            };

            var lines = await manager.GetBridgeLinesAsync(options, null, _ => { }, CancellationToken.None);

            Assert.Single(lines);
            Assert.Contains("webtunnel [2001:db8:e524:683e:fb8d:e6ee:79c0:f397]:443 ", lines[0]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void TryLoadBundledBridgeLines_preserves_webtunnel_endpoint()
    {
        var dir = CreateTempDir();
        try
        {
            var ptDir = Path.Combine(dir, "tor", "pluggable_transports");
            Directory.CreateDirectory(ptDir);
            var bundledPath = Path.Combine(ptDir, "bridges-webtunnel.txt");
            File.WriteAllText(
                bundledPath,
                "webtunnel [2001:db8:e524:683e:fb8d:e6ee:79c0:f397]:443 DA1ECF055635C1A6ED7F5B5F36296A5E3015CE57 url=https://np601p22.xoomlia.com/hlmb69xo/ ver=0.0.3");

            var manager = new TorBridgeManager(dir);
            var method = typeof(TorBridgeManager).GetMethod(
                "TryLoadBundledBridgeLines",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var lines = method!.Invoke(manager, ["webtunnel", (System.Action<string>)(_ => { })]);
            var typedLines = Assert.IsAssignableFrom<IReadOnlyList<string>>(lines);

            Assert.Single(typedLines);
            Assert.Contains("webtunnel [2001:db8:e524:683e:fb8d:e6ee:79c0:f397]:443 ", typedLines[0]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void GetClientTransportPlugins_restores_webtunnel_client_from_bundle()
    {
        lock (BundledWebTunnelLock)
        {
            var dir = CreateTempDir();
            var bundledPtDir = Path.Combine(AppContext.BaseDirectory, "tor", "pluggable_transports");
            var bundledClient = Path.Combine(bundledPtDir, PlatformHelper.WebTunnelClientBinaryName);
            var hadBundledClient = File.Exists(bundledClient);
            byte[]? originalContent = null;

            try
            {
                if (hadBundledClient)
                {
                    originalContent = File.ReadAllBytes(bundledClient);
                }
                else
                {
                    Directory.CreateDirectory(bundledPtDir);
                }

                File.WriteAllText(bundledClient, "test-webtunnel-client");

                var ptDir = Path.Combine(dir, "tor", "pluggable_transports");
                Directory.CreateDirectory(ptDir);

                var manager = new TorBridgeManager(dir);
                var options = new OnionHopConnectOptions
                {
                    SelectedBridgeType = "webtunnel"
                };

                var plugins = manager.GetClientTransportPlugins(
                    options,
                    ["webtunnel 198.51.100.20:443 0123456789ABCDEF0123456789ABCDEF01234567 url=https://example.test/ ver=0.0.3"],
                    Path.Combine(dir, "tor"),
                    null,
                    _ => { });

                var restoredClient = Path.Combine(ptDir, PlatformHelper.WebTunnelClientBinaryName);
                Assert.Single(plugins);
                Assert.True(File.Exists(restoredClient));
                Assert.Equal("test-webtunnel-client", File.ReadAllText(restoredClient));
            }
            finally
            {
                try
                {
                    if (hadBundledClient && originalContent != null)
                    {
                        File.WriteAllBytes(bundledClient, originalContent);
                    }
                    else if (File.Exists(bundledClient))
                    {
                        File.Delete(bundledClient);
                    }
                }
                catch
                {
                }

                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetBridgeLinesAsync_offline_only_uses_community_file_and_strips_alias()
    {
        var dir = CreateTempDir();
        try
        {
            var ptDir = Path.Combine(dir, "tor", "pluggable_transports");
            Directory.CreateDirectory(ptDir);
            var offlinePath = Path.Combine(ptDir, "bridges-community-obfs4.txt");
            File.WriteAllText(
                offlinePath,
                "obfs4 1.2.3.4:443 74FAD13168806246602538555B5521A0383A1875 cert=ssH+9rP8dG2NLDN2XuFw63hIO/9MNNinLmxQDpVa+7kTOa9/m+tGWT1SmSYpQ9uTBGa6Hw iat-mode=0 - DemoName");

            var manager = new TorBridgeManager(dir);
            var options = new OnionHopConnectOptions
            {
                SelectedBridgeType = "obfs4",
                BridgeSourceMode = OnionHopConnectOptions.BridgeSourceOfflineOnly
            };

            var lines = await manager.GetBridgeLinesAsync(options, null, _ => { }, CancellationToken.None);

            Assert.Single(lines);
            Assert.StartsWith("obfs4 ", lines[0]);
            Assert.DoesNotContain(" - ", lines[0]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task GetBridgeLinesAsync_bridge_service_only_does_not_use_offline_files()
    {
        var dir = CreateTempDir();
        try
        {
            var ptDir = Path.Combine(dir, "tor", "pluggable_transports");
            Directory.CreateDirectory(ptDir);
            var offlinePath = Path.Combine(ptDir, "bridges-community-custom.txt");
            File.WriteAllText(offlinePath, "obfs4 1.2.3.4:443 74FAD13168806246602538555B5521A0383A1875 cert=abc iat-mode=0");

            var manager = new TorBridgeManager(dir);
            var options = new OnionHopConnectOptions
            {
                SelectedBridgeType = "custom",
            BridgeSourceMode = OnionHopConnectOptions.BridgeSourceOnlineOnly
        };

        var lines = await manager.GetBridgeLinesAsync(options, null, _ => { }, CancellationToken.None);

        Assert.Empty(lines);
        Assert.Equal("No usable bridge lines were fetched from the Tor bridge service.", manager.BridgeValidationMessage);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task GetBridgeLinesAsync_vanilla_custom_lines_do_not_require_transport_plugins()
    {
        var dir = CreateTempDir();
        try
        {
            var manager = new TorBridgeManager(dir);
            var options = new OnionHopConnectOptions
            {
                SelectedBridgeType = "vanilla",
                CustomBridges = "8.8.8.8:443 74FAD13168806246602538555B5521A0383A1875"
            };

            var lines = await manager.GetBridgeLinesAsync(options, null, _ => { }, CancellationToken.None);
            var plugins = manager.GetClientTransportPlugins(options, lines, Path.Combine(dir, "tor"), null, _ => { });

            Assert.Single(lines);
            Assert.StartsWith("8.8.8.8:443 ", lines[0]);
            Assert.Empty(plugins);
            Assert.False(TorBridgeManager.BridgeLinesNeedClientTransportPlugins(lines));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void GetBridgeTypeKeys_includes_conjure_when_transport_exists()
    {
        var config = new PluggableTransportConfig
        {
            PluggableTransports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["conjure"] = "ClientTransportPlugin conjure exec pluggable_transports\\lyrebird.exe"
            },
            Bridges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["obfs4"] = ["obfs4 1.2.3.4:443 74FAD13168806246602538555B5521A0383A1875 cert=abc iat-mode=0"],
                ["meek"] = ["meek_lite 192.0.2.20:80 url=https://example.test front=www.example.test"]
            }
        };

        var keys = TorBridgeManager.GetBridgeTypeKeys(config);

        Assert.Contains(keys, key => string.Equals(key, "conjure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(keys, key => string.Equals(key, "vanilla", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(keys, key => string.Equals(key, "custom", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("custom", Assert.Single(keys.Skip(Math.Max(0, keys.Count - 1))));
    }

    [Fact]
    public void NormalizeClientTransportPlugin_quotes_executable_paths_with_spaces()
    {
        var plugin = "ClientTransportPlugin snowflake exec /Users/test/Library/Application Support/OnionHop/tor/pluggable_transports/lyrebird -ampcache https://cdn.ampproject.org/";

        var normalized = TorBridgeManager.NormalizeClientTransportPlugin(plugin);

        Assert.Equal(
            "ClientTransportPlugin snowflake exec \"/Users/test/Library/Application Support/OnionHop/tor/pluggable_transports/lyrebird\" -ampcache https://cdn.ampproject.org/",
            normalized);
    }

    [Fact]
    public void NormalizeClientTransportPlugin_preserves_already_quoted_executable_paths()
    {
        var plugin = "ClientTransportPlugin webtunnel exec \"/Users/test/Library/Application Support/OnionHop/tor/pluggable_transports/webtunnel-client\" -url https://example.test/";

        var normalized = TorBridgeManager.NormalizeClientTransportPlugin(plugin);

        Assert.Equal(plugin, normalized);
    }

    [Fact]
    public void NormalizeClientTransportPlugin_collapses_windows_space_paths_to_short_form()
    {
        // Tor's ClientTransportPlugin parser splits the line on whitespace and ignores quotes, so a
        // path with a space (e.g. a Windows user folder "C:\Users\First Last\...") must come out as
        // a space-free token. On Windows we collapse it to its 8.3 short form and emit it unquoted.
        if (!OperatingSystem.IsWindows())
        {
            return; // Short-path collapse is a Windows-only concern.
        }

        var root = Path.Combine(Path.GetTempPath(), "OnionHop Space Test " + Guid.NewGuid().ToString("N"));
        var ptDir = Path.Combine(root, "pluggable transports");
        Directory.CreateDirectory(ptDir);
        var exePath = Path.Combine(ptDir, "webtunnel-client.exe");
        File.WriteAllText(exePath, "stub");

        try
        {
            var normalized = TorBridgeManager.NormalizeClientTransportPlugin(
                $"ClientTransportPlugin webtunnel exec {exePath}");

            var shortExe = TorBridgeManager.MakeExecutablePathTokenSafe(exePath);
            if (shortExe.IndexOf(' ') < 0)
            {
                // 8.3 short names available (the common case): unquoted, space-free token.
                Assert.Equal($"ClientTransportPlugin webtunnel exec {shortExe}", normalized);
                Assert.DoesNotContain('"', normalized);
                var execPart = normalized[(normalized.IndexOf(" exec ", StringComparison.Ordinal) + " exec ".Length)..];
                Assert.DoesNotContain(' ', execPart);
            }
            else
            {
                // 8.3 disabled on this volume: best-effort quoting fallback (unchanged behavior).
                Assert.Equal($"ClientTransportPlugin webtunnel exec \"{exePath}\"", normalized);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NormalizeClientTransportPlugin_symlinks_space_paths_on_unix()
    {
        // On macOS/Linux there is no 8.3 short path, and the data dir
        // ("~/Library/Application Support/OnionHop/...") contains a space. The exec token must still
        // come out space-free and unquoted, via a symlink in the temp dir pointing at the binary.
        if (OperatingSystem.IsWindows())
        {
            return; // Windows uses the 8.3 short-path branch (covered above).
        }

        if (Path.GetTempPath().IndexOf(' ') >= 0)
        {
            return; // Cannot produce a space-free link if even the temp dir has a space.
        }

        var root = Path.Combine(Path.GetTempPath(), "OnionHop Space Test " + Guid.NewGuid().ToString("N"));
        var ptDir = Path.Combine(root, "pluggable transports");
        Directory.CreateDirectory(ptDir);
        var exePath = Path.Combine(ptDir, "webtunnel-client");
        File.WriteAllText(exePath, "stub");

        try
        {
            var normalized = TorBridgeManager.NormalizeClientTransportPlugin(
                $"ClientTransportPlugin webtunnel exec {exePath}");

            var execPart = normalized[(normalized.IndexOf(" exec ", StringComparison.Ordinal) + " exec ".Length)..];
            Assert.DoesNotContain(' ', execPart);   // space-free token
            Assert.DoesNotContain('"', normalized); // unquoted

            // The token is a symlink that resolves back to the real binary.
            var resolved = File.ResolveLinkTarget(execPart, returnFinalTarget: true)?.FullName ?? execPart;
            Assert.Equal(exePath, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            try { File.Delete(Path.Combine(Path.GetTempPath(), "onionhop-pt", "webtunnel-client")); } catch { }
        }
    }
}
