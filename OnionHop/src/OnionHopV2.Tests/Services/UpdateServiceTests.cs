using OnionHopV2.Core.Services;
using Xunit;

namespace OnionHopV2.Tests.Services;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.0", 1, 0, 0)]
    [InlineData("V2.3.4", 2, 3, 4)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData("10.20.30", 10, 20, 30)]
    [InlineData("v0.1.0-beta", 0, 1, 0)]
    [InlineData("release-1.2.3", 1, 2, 3)]
    public void ParseVersionFromTag_parses_valid_tags(string tag, int major, int minor, int build, int revision = -1)
    {
        var v = UpdateService.ParseVersionFromTag(tag);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(build, v.Build);
        if (revision >= 0)
            Assert.Equal(revision, v.Revision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-numbers")]
    [InlineData("beta")]
    public void ParseVersionFromTag_returns_zero_version_for_invalid_or_empty(string? tag)
    {
        var v = UpdateService.ParseVersionFromTag(tag);
        Assert.Equal(0, v.Major);
        Assert.Equal(0, v.Minor);
        Assert.Equal(0, v.Build);
    }
}
