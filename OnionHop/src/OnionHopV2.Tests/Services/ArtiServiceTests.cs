using System;
using System.IO;
using OnionHopV2.Core.Services;
using Xunit;

namespace OnionHopV2.Tests.Services;

public sealed class ArtiServiceTests
{
    [Fact]
    public void BuildConfigText_UsesConfiguredSocksEndpointAndStorage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OnionHopV2.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            var config = new ArtiLaunchConfig
            {
                SocksPort = 9250,
                SocksListenAddress = "::1"
            };

            var text = ArtiService.BuildConfigText(config, dir);

            Assert.Contains("socks_listen = \"[::1]:9250\"", text, StringComparison.Ordinal);
            Assert.Contains("[storage]", text, StringComparison.Ordinal);
            Assert.Contains("state_dir", text, StringComparison.Ordinal);
            Assert.Contains("cache_dir", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
