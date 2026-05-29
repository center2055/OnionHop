using System;
using OnionHopV3.Core.Models;
using Xunit;

namespace OnionHopV3.Tests.Models;

public sealed class UpdateInfoTests
{
    [Fact]
    public void Default_has_zero_version()
    {
        var info = new UpdateInfo();
        Assert.Equal(0, info.Version.Major);
        Assert.Equal(0, info.Version.Minor);
        Assert.Equal(0, info.Version.Build);
    }

    [Fact]
    public void Stores_version_and_download_url()
    {
        var info = new UpdateInfo
        {
            Version = new Version(1, 2, 3),
            DownloadUrl = "https://example.com/setup.exe",
            FileName = "OnionHop-Setup-1.2.3.exe"
        };
        Assert.Equal(1, info.Version.Major);
        Assert.Equal(2, info.Version.Minor);
        Assert.Equal(3, info.Version.Build);
        Assert.Equal("https://example.com/setup.exe", info.DownloadUrl);
        Assert.Equal("OnionHop-Setup-1.2.3.exe", info.FileName);
    }
}
