using System.Runtime.InteropServices;
using OnionHopV3.Core.Platform;
using Xunit;

namespace OnionHopV3.Tests.Platform;

/// <summary>
/// The helpers OnionHop downloads must match the CPU they run on. 32-bit ARM (a Raspberry Pi on
/// 32-bit Raspberry Pi OS) used to fall through to the x86_64 builds, which cannot run there (#89).
/// </summary>
public sealed class HelperAssetNameTests
{
    [Theory]
    [InlineData(Architecture.X64, "linux-amd64.tar.gz")]
    [InlineData(Architecture.Arm64, "linux-arm64.tar.gz")]
    [InlineData(Architecture.Arm, "linux-armv7.tar.gz")]
    public void Linux_sing_box_matches_the_architecture(Architecture architecture, string expected)
    {
        Assert.Equal(expected, PlatformHelper.SingBoxAssetFilterFor(isWindows: false, isMac: false, architecture));
    }

    [Theory]
    [InlineData(Architecture.X64, "Xray-linux-64.zip")]
    [InlineData(Architecture.Arm64, "Xray-linux-arm64-v8a.zip")]
    [InlineData(Architecture.Arm, "Xray-linux-arm32-v7a.zip")]
    public void Linux_xray_matches_the_architecture(Architecture architecture, string expected)
    {
        Assert.Equal(expected, PlatformHelper.XrayAssetNameFor(isWindows: false, isMac: false, architecture));
    }

    [Theory]
    [InlineData(Architecture.X64, "linux-x86_64")]
    [InlineData(Architecture.Arm64, "linux-aarch64")]
    [InlineData(Architecture.Arm, "linux-armv7")]
    public void Linux_tor_bundle_suffix_matches_the_architecture(Architecture architecture, string expected)
    {
        Assert.Equal(expected, PlatformHelper.TorExpertBundleSuffixFor(isWindows: false, isMac: false, architecture));
    }

    [Fact]
    public void Windows_and_mac_names_are_unchanged()
    {
        Assert.Equal("windows-amd64.zip", PlatformHelper.SingBoxAssetFilterFor(isWindows: true, isMac: false, Architecture.X64));
        Assert.Equal("Xray-windows-64.zip", PlatformHelper.XrayAssetNameFor(isWindows: true, isMac: false, Architecture.Arm64));
        Assert.Equal("windows-x86_64", PlatformHelper.TorExpertBundleSuffixFor(isWindows: true, isMac: false, Architecture.Arm64));
        Assert.Equal("darwin-arm64.tar.gz", PlatformHelper.SingBoxAssetFilterFor(isWindows: false, isMac: true, Architecture.Arm64));
        Assert.Equal("Xray-macos-64.zip", PlatformHelper.XrayAssetNameFor(isWindows: false, isMac: true, Architecture.X64));
        Assert.Equal("macos-aarch64", PlatformHelper.TorExpertBundleSuffixFor(isWindows: false, isMac: true, Architecture.Arm64));
    }
}
