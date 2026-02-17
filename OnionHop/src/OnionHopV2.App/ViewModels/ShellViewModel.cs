using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using OnionHopV2.App.Services;

namespace OnionHopV2.App.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    public ShellViewModel()
    {
        State = new AppStateViewModel();

        Pages =
        [
            new HomePageViewModel(State),
            new SettingsPageViewModel(State),
            new LogsPageViewModel(State),
            new AboutPageViewModel(State)
        ];

        ActivePage = Pages[0];
    }

    public AppStateViewModel State { get; }

    public ObservableCollection<PageViewModelBase> Pages { get; }

    [ObservableProperty] private PageViewModelBase _activePage = null!;

    public void Dispose()
    {
        State.Dispose();
    }
}

public sealed class HomePageViewModel(AppStateViewModel state)
    : PageViewModelBase("Nav.Home", MaterialIconKind.HomeOutline, state);

public sealed class SettingsPageViewModel(AppStateViewModel state)
    : PageViewModelBase("Nav.Settings", MaterialIconKind.CogOutline, state);

public sealed class LogsPageViewModel(AppStateViewModel state)
    : PageViewModelBase("Nav.Logs", MaterialIconKind.TextBoxOutline, state);

public sealed class AboutPageViewModel : PageViewModelBase
{
    private const string ReleasesApiBase = "https://api.github.com/repos/center2055/OnionHop/releases";
    private const int ReleasesPerPage = 100;
    private const int MaxReleasePages = 10;
    private static readonly HttpClient ReleasesHttpClient = CreateReleasesHttpClient();

    private bool _isLoadingChangelogs;
    private string _changelogStatusText = string.Empty;

    public AboutPageViewModel(AppStateViewModel state)
        : base("Nav.About", MaterialIconKind.InformationOutline, state)
    {
        VersionText = BuildVersionText();
        RefreshChangelogsCommand = new AsyncRelayCommand(LoadChangelogsAsync);
        ChangelogEntries.CollectionChanged += OnChangelogEntriesCollectionChanged;

        LocalizationService.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ChangelogSummaryText));
            _ = RefreshChangelogsCommand.ExecuteAsync(null);
        };

        _ = RefreshChangelogsCommand.ExecuteAsync(null);
    }

    public string VersionText { get; }

    public ObservableCollection<AboutChangelogEntry> ChangelogEntries { get; } = [];

    public IAsyncRelayCommand RefreshChangelogsCommand { get; }

    public bool IsLoadingChangelogs
    {
        get => _isLoadingChangelogs;
        private set
        {
            if (SetProperty(ref _isLoadingChangelogs, value))
            {
                OnPropertyChanged(nameof(ChangelogSummaryText));
            }
        }
    }

    public string ChangelogStatusText
    {
        get => _changelogStatusText;
        private set
        {
            if (SetProperty(ref _changelogStatusText, value))
            {
                OnPropertyChanged(nameof(HasChangelogStatus));
            }
        }
    }

    public bool HasChangelogEntries => ChangelogEntries.Count > 0;
    public bool HasChangelogStatus => !string.IsNullOrWhiteSpace(ChangelogStatusText);

    public string ChangelogSummaryText
    {
        get
        {
            if (IsLoadingChangelogs && ChangelogEntries.Count == 0)
            {
                return LocalizationService.Get("About.ChangelogSummaryLoading");
            }

            if (ChangelogEntries.Count == 0)
            {
                return LocalizationService.Get("About.ChangelogSummaryEmpty");
            }

            var oldest = ChangelogEntries[^1].Tag;
            var newest = ChangelogEntries[0].Tag;
            return string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Get("About.ChangelogSummaryRange"),
                ChangelogEntries.Count,
                oldest,
                newest);
        }
    }

    private async Task LoadChangelogsAsync()
    {
        if (IsLoadingChangelogs)
        {
            return;
        }

        IsLoadingChangelogs = true;
        ChangelogStatusText = LocalizationService.Get("About.ChangelogLoading");

        try
        {
            var releases = await FetchAllReleasesAsync();
            var ordered = releases
                .Where(static release => !release.Draft)
                .OrderBy(static release => release.PublishedAt ?? release.CreatedAt ?? DateTimeOffset.MinValue)
                .ToList();

            var firstBetaIndex = ordered.FindIndex(static release => IsBetaLike(release.TagName) || IsBetaLike(release.Name));
            if (firstBetaIndex > 0)
            {
                ordered = ordered.Skip(firstBetaIndex).ToList();
            }

            if (ordered.Count == 0)
            {
                ChangelogEntries.Clear();
                ChangelogStatusText = LocalizationService.Get("About.ChangelogEmpty");
                return;
            }

            var mapped = ordered
                .Select((release, index) => MapRelease(release, index == ordered.Count - 1))
                .ToList();
            mapped.Reverse();

            ChangelogEntries.Clear();
            foreach (var entry in mapped)
            {
                ChangelogEntries.Add(entry);
            }

            ChangelogStatusText = LocalizationService.Get("About.ChangelogLoaded");
        }
        catch
        {
            ChangelogEntries.Clear();
            ChangelogStatusText = LocalizationService.Get("About.ChangelogFailed");
        }
        finally
        {
            IsLoadingChangelogs = false;
        }
    }

    private void OnChangelogEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasChangelogEntries));
        OnPropertyChanged(nameof(ChangelogSummaryText));
    }

    private static async Task<List<GitHubReleaseHistoryItem>> FetchAllReleasesAsync()
    {
        var releases = new List<GitHubReleaseHistoryItem>();

        for (var page = 1; page <= MaxReleasePages; page++)
        {
            var requestUri = $"{ReleasesApiBase}?per_page={ReleasesPerPage}&page={page}";
            using var response = await ReleasesHttpClient.GetAsync(requestUri);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var batch = await JsonSerializer.DeserializeAsync<List<GitHubReleaseHistoryItem>>(stream)
                        ?? [];

            if (batch.Count == 0)
            {
                break;
            }

            releases.AddRange(batch);

            if (batch.Count < ReleasesPerPage)
            {
                break;
            }
        }

        return releases;
    }

    private static AboutChangelogEntry MapRelease(GitHubReleaseHistoryItem release, bool expandedByDefault)
    {
        var tag = string.IsNullOrWhiteSpace(release.TagName) ? "untagged" : release.TagName.Trim();
        var title = string.IsNullOrWhiteSpace(release.Name) ? tag : release.Name.Trim();
        var published = release.PublishedAt ?? release.CreatedAt;
        var publishedText = published.HasValue
            ? published.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : LocalizationService.Get("About.ChangelogDateUnknown");

        var releaseType = (release.Prerelease || IsBetaLike(release.TagName) || IsBetaLike(release.Name))
            ? LocalizationService.Get("About.ChangelogTypeBeta")
            : LocalizationService.Get("About.ChangelogTypeRelease");

        var notes = NormalizeNotes(release.Body);

        return new AboutChangelogEntry
        {
            Title = title,
            Tag = tag,
            PublishedText = publishedText,
            ReleaseType = releaseType,
            Notes = notes,
            IsExpanded = expandedByDefault
        };
    }

    private static string NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return LocalizationService.Get("About.ChangelogNotesMissing");
        }

        return notes.Replace("\r\n", "\n").Trim();
    }

    private static bool IsBetaLike(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("beta", StringComparison.OrdinalIgnoreCase)
               || value.Contains("alpha", StringComparison.OrdinalIgnoreCase)
               || value.Contains("rc", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildVersionText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var semanticVersion = informationalVersion?
            .Split('+', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(semanticVersion))
        {
            semanticVersion = assembly.GetName().Version?.ToString(3) ?? "unknown";
        }

        return $"OnionHop V{semanticVersion}";
    }

    private static HttpClient CreateReleasesHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHopV2-Changelog/1.0");
        return client;
    }

    private sealed class GitHubReleaseHistoryItem
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }
}

public sealed partial class AboutChangelogEntry : ObservableObject
{
    public string Title { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string PublishedText { get; init; } = string.Empty;
    public string ReleaseType { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    [ObservableProperty] private bool _isExpanded;
}
