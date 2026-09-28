using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Dependencies;
using Xunit;

namespace OnionHopV3.Tests.Dependencies;

/// <summary>
/// The tunnel cores and wintun run with administrator rights, and used to be run straight from
/// whatever a download returned. They are now checked against a SHA-256 before anything is extracted.
/// </summary>
public sealed class DownloadIntegrityTests
{
    private const string Hex = "c2d8bfff918755808781dfdeeb8581b6c91eb3a243d9a7b55483cfc0c0684d32";

    [Fact]
    public void Reads_a_github_asset_digest()
    {
        Assert.Equal(Hex, DependencyManager.ParseGitHubDigest("sha256:" + Hex));
    }

    [Fact]
    public void Normalises_case_so_comparison_is_not_fooled_by_it()
    {
        Assert.Equal(Hex, DependencyManager.ParseGitHubDigest("SHA256:" + Hex.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:tooshort")]
    [InlineData("sha256:zzd8bfff918755808781dfdeeb8581b6c91eb3a243d9a7b55483cfc0c0684d32")]
    public void Ignores_anything_that_is_not_a_sha256_digest(string? digest)
    {
        Assert.Null(DependencyManager.ParseGitHubDigest(digest));
    }

    [Fact]
    public async Task Accepts_a_file_whose_hash_matches()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "sing-box");
            var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

            await DependencyManager.VerifySha256Async(path, expected, "sing-box", CancellationToken.None);

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Rejects_and_deletes_a_file_whose_hash_does_not_match()
    {
        // A tampered or truncated core must never reach the extract step, let alone be run as admin.
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "not the real sing-box");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DependencyManager.VerifySha256Async(path, Hex, "sing-box", CancellationToken.None));

        Assert.Contains("integrity", error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_pinned_hash_wins_over_whatever_the_api_reports()
    {
        // The pinned value must not be replaceable by the same API that serves the download link.
        Assert.Equal(
            "aea1fa983134a2e2d0600581d1178e98bd6bb93ae12ad8c333eaacae68a1694c",
            DependencyManager.ExpectedCoreSha256("sing-box-1.13.13-windows-amd64.zip", "sha256:" + Hex));
    }

    [Theory]
    [InlineData("sing-box-1.13.13-windows-amd64.zip")]
    [InlineData("sing-box-1.13.13-darwin-amd64.tar.gz")]
    [InlineData("sing-box-1.13.13-darwin-arm64.tar.gz")]
    [InlineData("sing-box-1.13.13-linux-amd64.tar.gz")]
    [InlineData("sing-box-1.13.13-linux-arm64.tar.gz")]
    [InlineData("Xray-windows-64.zip")]
    [InlineData("Xray-macos-64.zip")]
    [InlineData("Xray-macos-arm64-v8a.zip")]
    [InlineData("Xray-linux-64.zip")]
    [InlineData("Xray-linux-arm64-v8a.zip")]
    public void Every_platform_asset_has_a_pinned_hash(string assetName)
    {
        // With no API digest at all, each asset the app can pick still has a hash to check against.
        var hash = DependencyManager.ExpectedCoreSha256(assetName, null);
        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
    }

    [Fact]
    public void An_unpinned_asset_falls_back_to_the_api_digest_or_nothing()
    {
        Assert.Equal(Hex, DependencyManager.ExpectedCoreSha256("sing-box-9.9.9-windows-amd64.zip", "sha256:" + Hex));
        Assert.Null(DependencyManager.ExpectedCoreSha256("sing-box-9.9.9-windows-amd64.zip", null));
    }

    private const string TorSums =
        "ed4bc23065ee10f68efcdae63ea318ffa4b02b04ba00f13a3f59f8e3832fdfad  tor-expert-bundle-android-aarch64-15.0.23.tar.gz\n" +
        "08d49de27f542b8f73e2014e064d8320562b5d20019c03d4725c5a5249d97985  tor-expert-bundle-linux-x86_64-15.0.23.tar.gz\n" +
        "1e4de9a4f1d99b8f40b5e0c75f3dcc3ea51b0aeab040d48fd23881e9fa94979a  tor-expert-bundle-windows-i686-15.0.23.tar.gz\r\n" +
        "231DAD6B9CB401A54C260DB7046965EF04E4F72FF071B140D423FB5DA281AB1E *tor-expert-bundle-windows-x86_64-15.0.23.tar.gz\n";

    [Fact]
    public void Finds_the_tor_bundle_hash_by_exact_file_name()
    {
        Assert.Equal(
            "08d49de27f542b8f73e2014e064d8320562b5d20019c03d4725c5a5249d97985",
            DependencyManager.FindSha256InSumsFile(TorSums, "tor-expert-bundle-linux-x86_64-15.0.23.tar.gz"));
    }

    [Fact]
    public void Reads_binary_mode_entries_and_normalises_case()
    {
        Assert.Equal(
            "231dad6b9cb401a54c260db7046965ef04e4f72ff071b140d423fb5da281ab1e",
            DependencyManager.FindSha256InSumsFile(TorSums, "tor-expert-bundle-windows-x86_64-15.0.23.tar.gz"));
    }

    [Theory]
    [InlineData("tor-expert-bundle-windows-x86_64-15.0.22.tar.gz")]
    [InlineData("tor-expert-bundle-windows-x86_64")]
    [InlineData("")]
    public void A_file_the_sums_do_not_list_gets_no_hash(string fileName)
    {
        // No entry means nothing vouches for the download, so the caller refuses it.
        Assert.Null(DependencyManager.FindSha256InSumsFile(TorSums, fileName));
    }

    [Fact]
    public void A_missing_sums_file_gives_no_hash()
    {
        Assert.Null(DependencyManager.FindSha256InSumsFile(null, "tor-expert-bundle-linux-x86_64-15.0.23.tar.gz"));
        Assert.Null(DependencyManager.FindSha256InSumsFile("<html>404</html>", "tor-expert-bundle-linux-x86_64-15.0.23.tar.gz"));
    }
}
