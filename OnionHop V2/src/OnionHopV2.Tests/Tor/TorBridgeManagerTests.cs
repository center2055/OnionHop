using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV2.Core;
using OnionHopV2.Core.Tor;
using Xunit;

namespace OnionHopV2.Tests.Tor;

public sealed class TorBridgeManagerTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "OnionHopV2.Tests", Path.GetRandomFileName());
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
    public async Task GetBridgeLinesAsync_bridge_db_only_does_not_use_offline_files()
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
                BridgeSourceMode = OnionHopConnectOptions.BridgeSourceBridgeDbOnly
            };

            var lines = await manager.GetBridgeLinesAsync(options, null, _ => { }, CancellationToken.None);

            Assert.Empty(lines);
            Assert.Equal("No usable bridge lines were fetched from BridgeDB.", manager.BridgeValidationMessage);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
