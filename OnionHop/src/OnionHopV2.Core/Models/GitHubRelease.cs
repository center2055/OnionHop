using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OnionHopV2.Core.Models;

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}

