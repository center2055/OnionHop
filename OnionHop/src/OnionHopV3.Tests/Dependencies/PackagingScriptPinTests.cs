using System.Text.RegularExpressions;
using OnionHopV3.Core.Dependencies;
using Xunit;

namespace OnionHopV3.Tests.Dependencies;

/// <summary>
/// The packaging scripts bundle sing-box and Xray. They have to bundle the versions the app pins and
/// tests its generated configs against: the Windows portable zip and the macOS DMGs once took
/// "latest" and shipped a sing-box nobody had run a tunnel with. This keeps the scripts' versions
/// and checksums tied to <see cref="DependencyManager"/>.
/// </summary>
public sealed class PackagingScriptPinTests
{
    private static readonly string[] Scripts = ["download-deps.sh", "download-deps-macos.sh", "download-deps.ps1"];

    private static readonly Regex VersionAssignment = new(
        @"^\s*\$?(?<name>SING_BOX_VERSION|SingBoxVersion|XRAY_VERSION|XrayVersion)\s*=\s*""v?(?<version>[0-9.]+)""",
        RegexOptions.Multiline);

    // "sing-box-1.13.13-linux-amd64.tar.gz bb99..." (bash tables) or "Xray-windows-64.zip" = "d004..." (PowerShell).
    private static readonly Regex PinnedAsset = new(
        @"""?(?<asset>(?:sing-box-[0-9.]+-[a-z0-9-]+\.(?:tar\.gz|zip))|(?:Xray-[a-z0-9-]+\.zip))""?(?:\s+|\s*=\s*"")(?<sha>[0-9a-f]{64})",
        RegexOptions.IgnoreCase);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Scripts.Any(script => File.Exists(Path.Combine(dir.FullName, script))))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No packaging script found above the test output folder.");
    }

    private static IEnumerable<(string Name, string Text)> ExistingScripts()
    {
        var root = RepoRoot();
        return Scripts
            .Select(script => (Name: script, Path: Path.Combine(root, script)))
            .Where(script => File.Exists(script.Path))
            .Select(script => (script.Name, File.ReadAllText(script.Path)));
    }

    [Fact]
    public void Every_script_bundles_the_pinned_versions()
    {
        foreach (var (name, text) in ExistingScripts())
        {
            var versions = VersionAssignment.Matches(text)
                .ToDictionary(match => match.Groups["name"].Value.Replace("_", string.Empty).ToUpperInvariant(), match => match.Groups["version"].Value);

            Assert.True(versions.TryGetValue("SINGBOXVERSION", out var singBox), $"{name} sets no sing-box version");
            Assert.True(versions.TryGetValue("XRAYVERSION", out var xray), $"{name} sets no Xray version");
            Assert.Equal(DependencyManager.SingBoxVersion, singBox);
            Assert.Equal(DependencyManager.XrayVersion, xray);
        }
    }

    [Fact]
    public void Every_checksum_in_the_scripts_is_the_one_the_app_pins()
    {
        var checkedAssets = 0;
        foreach (var (name, text) in ExistingScripts())
        {
            foreach (Match match in PinnedAsset.Matches(text))
            {
                var asset = match.Groups["asset"].Value;
                Assert.True(asset.Contains(DependencyManager.SingBoxVersion) || asset.StartsWith("Xray-", StringComparison.Ordinal),
                    $"{name} pins {asset}, not sing-box {DependencyManager.SingBoxVersion}");
                Assert.Equal(DependencyManager.ExpectedCoreSha256(asset, null), match.Groups["sha"].Value.ToLowerInvariant());
                checkedAssets++;
            }
        }

        Assert.True(checkedAssets > 0);
    }
}
