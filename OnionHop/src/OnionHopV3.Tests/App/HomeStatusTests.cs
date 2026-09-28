using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OnionHopV3.App.ViewModels;
using Xunit;
using static OnionHopV3.App.ViewModels.HomePageViewModel;

namespace OnionHopV3.Tests.App;

/// <summary>
/// The Home status card (#73) derives its title, detail, tone and icon from one state, so it cannot
/// say "Connected" beside a leak warning or keep showing whatever message was set last. These pin that
/// derivation down, including the kill switch state that appeared once the block stopped lifting itself.
/// </summary>
public sealed class HomeStatusTests
{
    private static HeroState Derive(bool connected = false, bool starting = false, bool disconnecting = false,
        bool leaking = false, bool killSwitch = false) =>
        DeriveHeroState(connected, starting, disconnecting, leaking, killSwitch);

    [Fact]
    public void Disconnecting_wins_over_everything()
    {
        Assert.Equal(HeroState.Disconnecting,
            Derive(connected: true, starting: true, disconnecting: true, leaking: true, killSwitch: true));
    }

    [Fact]
    public void Connecting_wins_over_a_held_kill_switch()
    {
        // Connecting is what lifts the block, so while it runs the card has to say "connecting".
        Assert.Equal(HeroState.Connecting, Derive(starting: true, killSwitch: true));
    }

    [Fact]
    public void A_leak_while_connected_is_not_protected()
    {
        Assert.Equal(HeroState.NotProtected, Derive(connected: true, leaking: true));
    }

    [Fact]
    public void A_leak_verdict_means_nothing_once_disconnected()
    {
        Assert.Equal(HeroState.Disconnected, Derive(leaking: true));
    }

    [Fact]
    public void A_held_kill_switch_is_not_shown_as_plain_disconnected()
    {
        // "Disconnected" reads as "back to normal", which is exactly what is not true while it holds.
        Assert.Equal(HeroState.KillSwitch, Derive(killSwitch: true));
    }

    [Fact]
    public void Connected_without_a_leak_is_connected()
    {
        Assert.Equal(HeroState.Connected, Derive(connected: true));
    }

    [Theory]
    [InlineData(true, false, true, true, "Home.DetailTunFull")]
    [InlineData(true, true, true, true, "Home.DetailTunHybrid")]
    [InlineData(false, false, true, true, "Home.DetailProxyOn")]
    [InlineData(false, false, true, false, "Home.DetailProxyOff")]
    [InlineData(false, false, false, true, "Home.DetailLocalOnly")]
    public void Connected_detail_describes_the_live_mode(bool tun, bool hybrid, bool systemScope, bool proxyOn, string expected)
    {
        Assert.Equal(expected, HeroDetailKey(HeroState.Connected, tun, hybrid, systemScope, proxyOn));
    }

    [Theory]
    [InlineData("NotProtected", "danger")]
    [InlineData("KillSwitch", "warning")]
    [InlineData("Disconnecting", "warning")]
    [InlineData("Connected", "success")]
    [InlineData("Connecting", "info")]
    [InlineData("Disconnected", "neutral")]
    public void Tone_matches_the_state(string state, string tone)
    {
        Assert.Equal(tone, HeroToneFor(Enum.Parse<HeroState>(state)));
    }

    [Fact]
    public void Every_state_has_its_text_in_english()
    {
        // A missing key shows the raw key ("Home.StatusKillSwitch") on the most visible line of the app.
        var keys = EnglishKeys();
        foreach (var state in Enum.GetValues<HeroState>())
        {
            Assert.Contains(HeroTitleKey(state), keys);
            foreach (var tun in new[] { false, true })
            {
                foreach (var flag in new[] { false, true })
                {
                    Assert.Contains(HeroDetailKey(state, tun, flag, flag, !flag), keys);
                }
            }
        }
    }

    [Theory]
    [InlineData("12:00:01 Disconnected.", "neutral")]
    [InlineData("12:00:01 Tor stopped. Traffic is back to normal.", "neutral")]
    [InlineData("12:00:01 Connected to Tor.", "success")]
    [InlineData("12:00:01 Tunnel check passed: a new connection left through Tor.", "success")]
    [InlineData("12:00:01 WARNING: tunnel check FAILED.", "danger")]
    [InlineData("12:00:01 Connect failed: timeout", "danger")]
    [InlineData("12:00:01 Kill switch engaged because the tunnel stopped unexpectedly.", "warning")]
    public void Latest_activity_gets_the_right_tone(string line, string tone)
    {
        // "Disconnected" contains "connected" and used to get the green dot.
        Assert.Equal(tone, ParseEvent(line).Tone);
    }

    [Theory]
    [InlineData("12:00:01 IP check: torFirst=True", true)]
    [InlineData("12:00:01 Auto IP refresh started", true)]
    [InlineData("12:00:01 Tor arguments: -f torrc", true)]
    [InlineData("12:00:01 Paths: baseDir=%USERPROFILE%", true)]
    [InlineData("12:00:01 Kill switch lifted.", false)]
    public void Routine_polling_stays_off_the_home_page(string line, bool routine)
    {
        Assert.Equal(routine, IsRoutineLine(line));
    }

    [Fact]
    public void Long_messages_are_cut_to_one_line()
    {
        var item = ParseEvent("12:00:01 " + new string('x', 400));
        Assert.Equal("12:00:01", item.Time);
        Assert.Equal(140, item.Message.Length);
        Assert.EndsWith("...", item.Message);
    }

    [Theory]
    [InlineData("80", true)]
    [InlineData(" 443 ", true)]
    [InlineData("65535", true)]
    [InlineData("0", false)]
    [InlineData("65536", false)]
    [InlineData("8o", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Onion_service_ports_must_be_1_to_65535(string? raw, bool valid)
    {
        Assert.Equal(valid, OnionServicesViewModel.TryParsePort(raw, out _));
    }

    private static HashSet<string> EnglishKeys()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var path = Path.Combine(Resources.LocalizationIntegrityTests.ResourcesDirectory(), "Strings.en.axaml");
        return XDocument.Load(path).Root!
            .Elements()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(key => key != null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
