using System;

namespace OnionHopV2.Core.Models;

public sealed class UpdateInfo
{
    public Version Version { get; set; } = new(0, 0, 0);
    public string? DownloadUrl { get; set; }
    public string? HtmlUrl { get; set; }
    public string? FileName { get; set; }
}

