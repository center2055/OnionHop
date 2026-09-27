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
}
