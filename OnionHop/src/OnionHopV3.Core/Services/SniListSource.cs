using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

/// <summary>One country available in the SNI-lists source.</summary>
public sealed record SniCountry(string Code, string Name, int Count)
{
    public string Display => Count > 0 ? $"{Name} ({Code.ToUpperInvariant()})" : $"{Name} ({Code.ToUpperInvariant()})";
}

/// <summary>
/// Client for the per-country SNI/front-domain candidate lists published at
/// github.com/center2055/OnionHop-SNI-Lists (the "Request SNI" source, mirroring the bridges
/// collector). Working SNIs are country-specific, so the scanner fetches the list for the chosen
/// country and probes those. All fetches are direct (the SNI scan is meant to run with the VPN off)
/// and never throw - a missing source just yields an empty result.
/// </summary>
public static class SniListSource
{
    private const string BaseUrl = "https://raw.githubusercontent.com/center2055/OnionHop-SNI-Lists/main/";

    /// <summary>Fetch the list of available countries from index.json. Returns empty on any failure.</summary>
    public static async Task<IReadOnlyList<SniCountry>> FetchCountriesAsync(CancellationToken token)
    {
        try
        {
            using var response = await HttpClientFactory.Default.GetAsync(BaseUrl + "index.json", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<SniCountry>();
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var index = JsonSerializer.Deserialize<IndexModel>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var countries = index?.Countries;
            if (countries == null || countries.Count == 0)
            {
                return Array.Empty<SniCountry>();
            }

            return countries
                .Where(c => !string.IsNullOrWhiteSpace(c.Code))
                .Select(c => new SniCountry(
                    c.Code!.Trim().ToLowerInvariant(),
                    string.IsNullOrWhiteSpace(c.Name) ? c.Code!.Trim().ToUpperInvariant() : c.Name!.Trim(),
                    c.Count))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<SniCountry>();
        }
    }

    /// <summary>Fetch the candidate domains for a country code (sni/&lt;code&gt;.txt). Empty on failure.</summary>
    public static async Task<IReadOnlyList<string>> FetchListAsync(string countryCode, CancellationToken token)
    {
        var code = (countryCode ?? string.Empty).Trim().ToLowerInvariant();
        if (code.Length == 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            using var response = await HttpClientFactory.Default.GetAsync($"{BaseUrl}sni/{code}.txt", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private sealed class IndexModel
    {
        [JsonPropertyName("countries")]
        public List<CountryModel>? Countries { get; set; }
    }

    private sealed class CountryModel
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }
    }
}
