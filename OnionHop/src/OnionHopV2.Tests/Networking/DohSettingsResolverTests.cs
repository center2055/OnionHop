using OnionHopV2.Core;
using OnionHopV2.Core.Networking;
using Xunit;

namespace OnionHopV2.Tests.Networking;

public sealed class DohSettingsResolverTests
{
    [Theory]
    [InlineData(OnionHopConnectOptions.DnsProviderCloudflare, "cloudflare-dns.com")]
    [InlineData(OnionHopConnectOptions.DnsProviderGoogle, "dns.google")]
    [InlineData(OnionHopConnectOptions.DnsProviderQuad9, "dns.quad9.net")]
    [InlineData(OnionHopConnectOptions.DnsProviderAdGuard, "dns.adguard.com")]
    [InlineData(OnionHopConnectOptions.DnsProviderMullvad, "dns.mullvad.net")]
    [InlineData(OnionHopConnectOptions.DnsProviderOpenDns, "doh.opendns.com")]
    [InlineData(OnionHopConnectOptions.DnsProviderAuto, "cloudflare-dns.com")]
    [InlineData("unknown-provider", "cloudflare-dns.com")]
    public void Resolve_maps_known_provider_to_expected_host(string provider, string expectedHost)
    {
        var options = new OnionHopConnectOptions
        {
            SelectedDnsProvider = provider
        };

        var result = DohSettingsResolver.Resolve(options);

        Assert.Equal(expectedHost, result.Server);
        Assert.Equal(443, result.Port);
        Assert.Equal("/dns-query", result.Path);
    }

    [Fact]
    public void Resolve_custom_provider_normalizes_host_port_and_path()
    {
        var options = new OnionHopConnectOptions
        {
            SelectedDnsProvider = OnionHopConnectOptions.DnsProviderCustom,
            CustomDohHost = "https://dns.example.test:8443",
            CustomDohPath = "custom-path"
        };

        var result = DohSettingsResolver.Resolve(options);

        Assert.Equal("dns.example.test", result.Server);
        Assert.Equal(8443, result.Port);
        Assert.Equal("/custom-path", result.Path);
    }
}
