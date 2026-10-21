using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// Sharing Tor on one interface (#89) gives Tor a second SOCKS listener there, next to the loopback
/// one OnionHop itself uses, instead of one listener on every interface.
/// </summary>
public sealed class TorLanSocksPortTests
{
    private static List<string> SocksPorts(TorLaunchConfig config)
    {
        var arguments = TorService.BuildArguments(config, "data");
        return arguments
            .Select((value, index) => (value, index))
            .Where(entry => entry.value == "--SocksPort")
            .Select(entry => arguments[entry.index + 1])
            .ToList();
    }

    [Fact]
    public void Sharing_on_an_interface_adds_a_listener_there()
    {
        var ports = SocksPorts(new TorLaunchConfig
        {
            SocksPort = 9050,
            SocksListenAddress = "127.0.0.1",
            AdditionalSocksListenAddresses = ["192.168.4.1"]
        });

        Assert.Equal(["127.0.0.1:9050", "192.168.4.1:9050"], ports);
    }

    [Fact]
    public void Without_it_there_is_one_listener()
    {
        Assert.Equal(["0.0.0.0:9050"], SocksPorts(new TorLaunchConfig { SocksPort = 9050, SocksListenAddress = "0.0.0.0" }));
    }
}
