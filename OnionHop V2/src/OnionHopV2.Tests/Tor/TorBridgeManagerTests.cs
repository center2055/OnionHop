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
}
