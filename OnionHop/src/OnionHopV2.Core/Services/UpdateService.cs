using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OnionHopV2.Core.Models;

namespace OnionHopV2.Core.Services;

public sealed class UpdateService
{
    public async Task<UpdateInfo?> GetLatestReleaseAsync(string apiUrl)
    {
        using var response = await HttpClientFactory.Default.GetAsync(apiUrl);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        if (release == null)
        {
            return null;
        }

        var latestVersion = ParseVersionFromTag(release.TagName);
        if (latestVersion.Major == 0 && latestVersion.Minor == 0 && latestVersion.Build == 0)
        {
            return null;
        }

        var asset = release.Assets?
            .FirstOrDefault(a => a.Name != null
                                 && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                 && a.Name.Contains("OnionHop", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(a => a.Name != null
                                                  && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo
        {
            Version = latestVersion,
            DownloadUrl = asset?.BrowserDownloadUrl,
            HtmlUrl = release.HtmlUrl,
            FileName = asset?.Name
        };
    }

    public async Task<string?> DownloadUpdateAsync(UpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            return null;
        }

        var updatesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnionHop", "updates");
        Directory.CreateDirectory(updatesDir);
        var fileName = string.IsNullOrWhiteSpace(info.FileName)
            ? $"OnionHop-Setup-{info.Version}.exe"
            : info.FileName;
        var targetPath = Path.Combine(updatesDir, fileName);

        using var response = await HttpClientFactory.LongTimeout.GetAsync(info.DownloadUrl);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file);
        return targetPath;
    }

    /// <summary>Parses a version from a tag string (e.g. "v1.2.3"). Used by update logic and tests.</summary>
    public static Version ParseVersionFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return new Version(0, 0, 0);
        }

        var clean = tag.Trim().TrimStart('v', 'V');
        var match = Regex.Match(clean, @"\d+(\.\d+){0,3}");
        if (match.Success && Version.TryParse(match.Value, out var version))
        {
            return version;
        }

        return new Version(0, 0, 0);
    }
}

