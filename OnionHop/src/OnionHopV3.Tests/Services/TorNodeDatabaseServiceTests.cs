using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class TorNodeDatabaseServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onionhop-nodedb-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private TorNodeDatabaseService NewService() => new(Path.Combine(_dir, "tor-node-db.json"));

    [Fact]
    public void The_bundled_snapshot_lists_the_exit_countries()
    {
        var snapshot = TorNodeDatabaseService.LoadBundledSnapshot();

        Assert.True(snapshot.Countries.Count > 50);
        Assert.True(snapshot.Countries.Count(c => c.ExitNodes > 0) > 30);
        Assert.Contains(snapshot.Countries, c => c.CountryCode == "de" && c.ExitNodes > 0 && c.EntryNodes > 0);
        Assert.All(snapshot.Countries, c => Assert.Equal(c.CountryCode.ToLowerInvariant(), c.CountryCode));
        Assert.True(snapshot.UpdatedUtc > new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Without_a_saved_list_the_offline_list_is_the_snapshot()
    {
        var offline = NewService().GetOfflineCountryStats();

        Assert.Equal(TorNodeDatabaseService.LoadBundledSnapshot().Countries.Count, offline.Count);
    }

    // Where Onionoo is blocked the list used to fall back to 8 countries with no relay counts, held as
    // current for 12 hours, so nothing tried again and every exit country looked like it had no exits.
    [Fact]
    public async Task A_failed_lookup_returns_the_snapshot_and_does_not_count_as_current()
    {
        var service = NewService();
        using var blocked = new HttpClient(new StubHandler(_ => throw new HttpRequestException("Connection reset")));

        var countries = await service.GetCountryStatsAsync(_log.Add, CancellationToken.None, blocked);

        Assert.True(countries.Count > 50);
        Assert.Contains(countries, c => c.CountryCode == "us" && c.ExitNodes > 0);
        Assert.False(service.HasCurrentData);
        Assert.Contains(_log, line => line.Contains("through Tor failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connecting_loads_the_list_through_Tor_and_saves_it()
    {
        var service = NewService();
        using var blocked = new HttpClient(new StubHandler(_ => throw new HttpRequestException("Connection reset")));
        await service.GetCountryStatsAsync(_log.Add, CancellationToken.None, blocked);

        var requested = new List<Uri>();
        using var throughTor = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OnionooPayload, Encoding.UTF8, "application/json")
            };
        }));

        var refreshed = await service.RefreshThroughTorIfOutdatedAsync(throughTor, _log.Add, CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.Equal(["de", "is"], refreshed!.Select(c => c.CountryCode).OrderBy(c => c));
        var germany = refreshed.Single(c => c.CountryCode == "de");
        Assert.Equal((2, 1, 1), (germany.TotalNodes, germany.ExitNodes, germany.EntryNodes));
        Assert.True(service.HasCurrentData);
        Assert.Single(requested);
        Assert.Equal("onionoo.torproject.org", requested[0].Host);
        Assert.Contains(_log, line => line.Contains("loaded from Onionoo through Tor", StringComparison.Ordinal));

        // Saved for the next start, and nothing is fetched again while it is current.
        Assert.Equal(2, NewService().GetOfflineCountryStats().Count);
        Assert.Null(await service.RefreshThroughTorIfOutdatedAsync(throughTor, _log.Add, CancellationToken.None));
        Assert.Single(requested);
    }

    private const string OnionooPayload = """
    {
      "relays_published": "2026-11-09 12:00:00",
      "relays": [
        { "country": "de", "country_name": "Germany", "flags": ["Exit", "Fast", "Running", "Valid"] },
        { "country": "de", "country_name": "Germany", "flags": ["Guard", "Running", "Stable"] },
        { "country": "is", "country_name": "Iceland", "flags": ["Running"] },
        { "flags": ["Running"] }
      ]
    }
    """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
