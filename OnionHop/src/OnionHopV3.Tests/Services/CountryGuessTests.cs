using System;
using System.IO;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class CountryGuessTests
{
    [Theory]
    [InlineData("Iran Standard Time", "US", "IR")]          // one country per zone: region irrelevant
    [InlineData("Asia/Tehran", null, "IR")]
    [InlineData("Europe/Moscow", "DE", "RU")]
    [InlineData("China Standard Time", "HK", "HK")]         // the region picks among the zone's countries
    [InlineData("China Standard Time", "US", "CN")]         // otherwise the most censored one
    [InlineData("West Asia Standard Time", "UZ", "UZ")]
    [InlineData("West Asia Standard Time", "US", "TM")]
    public void Time_zone_and_region_give_the_country(string zone, string? region, string expected)
    {
        Assert.Equal(expected, CountryGuess.FromTimeZone(zone, region)?.Country);
    }

    [Theory]
    [InlineData("W. Europe Standard Time")]
    [InlineData("Europe/Berlin")]
    [InlineData("")]
    public void Zones_outside_the_profiled_countries_give_no_guess(string zone)
    {
        Assert.Null(CountryGuess.FromTimeZone(zone, "DE"));
    }

    [Fact]
    public void A_country_detected_online_is_remembered_first()
    {
        var path = Path.Combine(Path.GetTempPath(), "OnionHopV3.Tests", Path.GetRandomFileName(), "country.json");
        var previous = CountryGuess.MemoryPath;
        CountryGuess.MemoryPath = path;
        try
        {
            CountryGuess.Remember("ir");

            var guess = CountryGuess.TryGuess();

            Assert.Equal("IR", guess?.Country);
            Assert.Contains("earlier connect", guess?.Basis);
        }
        finally
        {
            CountryGuess.MemoryPath = previous;
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }
}
