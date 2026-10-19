using System.Collections.Generic;
using OnionHopV3.Core;
using OnionHopV3.Core.Tor;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// The reachability race probes a fronted transport at the hosts its attempt really connects to
/// first: the domain fronts (never the url host hidden inside the fronted request), an SQS queue or
/// a DoH resolver, taken from the lines that attempt will use.
/// </summary>
public sealed class FrontedProbeHostsTests
{
    private static PluggableTransportConfig Bundled(string transport, params string[] lines) =>
        new() { Bridges = new Dictionary<string, List<string>> { [transport] = [.. lines] } };

    [Fact]
    public void Tors_lines_for_the_country_come_first()
    {
        var options = new OnionHopConnectOptions
        {
            CountryBridgeLines = string.Join("\n",
                "snowflake 192.0.2.3:80 A url=https://1098762253.rsc.cdn77.org fronts=www.phpmyadmin.net,cdn.zk.mk",
                "snowflake 192.0.2.5:80 A sqsqueue=https://sqs.us-east-2.amazonaws.com/1/snowflake-broker sqscreds=x",
                "meek_lite 192.0.2.20:80 url=https://other.test front=elsewhere.test")
        };
        var bundled = Bundled("snowflake", "snowflake 192.0.2.3:80 A url=https://x.test fronts=app.datapacket.com");

        var hosts = OnionHopClient.FrontedProbeHosts("snowflake", options, bundled);

        Assert.Equal(["www.phpmyadmin.net", "cdn.zk.mk", "sqs.us-east-2.amazonaws.com"], hosts);
    }

    [Fact]
    public void Without_country_lines_the_bundled_fronts_are_probed()
    {
        var bundled = Bundled("meek", "meek_lite 192.0.2.20:80 url=https://1603026938.rsc.cdn77.org front=www.phpmyadmin.net utls=HelloRandomizedALPN");

        Assert.Equal(["www.phpmyadmin.net"], OnionHopClient.FrontedProbeHosts("meek", new OnionHopConnectOptions(), bundled));
    }

    [Fact]
    public void With_amp_on_snowflake_is_probed_at_its_google_front()
    {
        var bundled = Bundled("snowflake", "snowflake 192.0.2.3:80 A url=https://1098762253.rsc.cdn77.org/ fronts=app.datapacket.com,www.datapacket.com");

        var hosts = OnionHopClient.FrontedProbeHosts("snowflake", new OnionHopConnectOptions { UseSnowflakeAmp = true }, bundled);

        Assert.Equal(["www.google.com"], hosts);
    }

    [Fact]
    public void Dnstt_falls_back_to_the_built_in_lines_and_probes_the_resolver()
    {
        Assert.Equal(["dns.google"], OnionHopClient.FrontedProbeHosts("dnstt", new OnionHopConnectOptions(), config: null));
    }
}
