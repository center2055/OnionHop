using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using OnionHopV3.App.Services;

namespace OnionHopV3.App.ViewModels;

public sealed partial class LogsPageViewModel : PageViewModelBase
{
    private const string SourceApp = "app";
    private const string SourceDns = "dns";
    private const string SourceEngine = "engine";
    private const string SourceBridges = "bridges";
    private const string LevelAll = "all";
    private const string LevelInfo = "info";
    private const string LevelWarning = "warning";
    private const string LevelError = "error";

    public LogsPageViewModel(AppStateViewModel state)
        : base("Nav.Logs", MaterialIconKind.TextBoxOutline, state, 0xE7C3)
    {
        State.LogLines.CollectionChanged += OnSourceLogsChanged;
        State.DnsLogLines.CollectionChanged += OnSourceLogsChanged;
        State.VpnLogLines.CollectionChanged += OnSourceLogsChanged;
        State.ActiveBridgeLines.CollectionChanged += OnSourceLogsChanged;
        State.PropertyChanged += OnStatePropertyChanged;

        RebuildVisibleEntries();
    }

    public ObservableCollection<StructuredLogEntry> VisibleEntries { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedSource = SourceApp;
    [ObservableProperty] private string _selectedLevel = LevelAll;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private int _totalEntries;
    [ObservableProperty] private int _errorEntries;
    [ObservableProperty] private int _warningEntries;
    [ObservableProperty] private int _infoEntries;

    public string EngineTabLabel => State.VpnLogTabHeader;
    // The Summary panel describes the currently-selected source, so it shows that source's label
    // rather than a global list (which read confusingly as "App" while sitting on an empty DNS tab).
    public string ActiveSourcesSummary => SelectedSource switch
    {
        SourceDns => LocalizationService.Get("Logs.TabDns"),
        SourceEngine => EngineTabLabel,
        SourceBridges => LocalizationService.Get("Logs.TabBridges"),
        _ => LocalizationService.Get("Logs.TabApp")
    };
    public bool ShowEmptyState => VisibleEntries.Count == 0;
    public bool HasActiveBridges => State.ActiveBridgeLines.Count > 0;
    public bool IsAppSelected { get => SelectedSource == SourceApp; set { if (value) SelectedSource = SourceApp; } }
    public bool IsDnsSelected { get => SelectedSource == SourceDns; set { if (value) SelectedSource = SourceDns; } }
    public bool IsEngineSelected { get => SelectedSource == SourceEngine; set { if (value) SelectedSource = SourceEngine; } }
    public bool IsBridgesSelected { get => SelectedSource == SourceBridges; set { if (value) SelectedSource = SourceBridges; } }
    public bool IsAllSelected { get => SelectedLevel == LevelAll; set { if (value) SelectedLevel = LevelAll; } }
    public bool IsInfoSelected { get => SelectedLevel == LevelInfo; set { if (value) SelectedLevel = LevelInfo; } }
    public bool IsWarningSelected { get => SelectedLevel == LevelWarning; set { if (value) SelectedLevel = LevelWarning; } }
    public bool IsErrorSelected { get => SelectedLevel == LevelError; set { if (value) SelectedLevel = LevelError; } }

    [RelayCommand]
    private void ClearSelectedLogs()
    {
        switch (SelectedSource)
        {
            case SourceDns:
                State.ClearDnsLogs();
                break;
            case SourceEngine:
                State.ClearVpnLogs();
                break;
            case SourceBridges:
                // The active bridge list reflects the live connection; not a user-clearable log.
                break;
            default:
                State.ClearAppLogs();
                break;
        }
    }

    public string GetVisibleLogText()
    {
        return string.Join(Environment.NewLine, VisibleEntries.Select(entry => entry.RawLine));
    }

    public string GetSelectedFileNameStem()
    {
        return SelectedSource switch
        {
            SourceDns => "dns",
            SourceEngine => State.TunCoreMode == AppStateViewModel.TunCoreXray ? "xray" : "sing-box",
            SourceBridges => "bridges",
            _ => "app"
        };
    }

    partial void OnSearchTextChanged(string value) => RebuildVisibleEntries();
    partial void OnSelectedSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsAppSelected));
        OnPropertyChanged(nameof(IsDnsSelected));
        OnPropertyChanged(nameof(IsEngineSelected));
        OnPropertyChanged(nameof(IsBridgesSelected));
        OnPropertyChanged(nameof(ActiveSourcesSummary));
        RebuildVisibleEntries();
    }

    partial void OnSelectedLevelChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(IsInfoSelected));
        OnPropertyChanged(nameof(IsWarningSelected));
        OnPropertyChanged(nameof(IsErrorSelected));
        RebuildVisibleEntries();
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            RebuildVisibleEntries();
        }
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppStateViewModel.VpnLogTabHeader))
        {
            OnPropertyChanged(nameof(EngineTabLabel));
        }
    }

    private void OnSourceLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, State.ActiveBridgeLines))
        {
            // Bridges set/cleared: refresh the "Current Bridge" tab's visibility regardless of pause.
            OnPropertyChanged(nameof(HasActiveBridges));
        }

        if (IsPaused)
        {
            return;
        }

        // Only changes to the currently-selected source affect the visible list. Skipping the other
        // two collections avoids a full rebuild every time another source streams - e.g. the engine
        // /Tor log firehose while the user is on the App tab. That O(N) rebuild-per-line (Clear +
        // re-add of up to 2000 entries on a non-virtualized list) was pegging CPU and ballooning
        // memory during long or failing connects.
        if (!ReferenceEquals(sender, GetSourceLines()))
        {
            OnPropertyChanged(nameof(ActiveSourcesSummary));
            return;
        }

        // Fast path: newly appended lines are parsed and appended in place, instead of tearing down
        // and rebuilding the whole collection on every single incoming line.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 })
        {
            AppendNewEntries(e.NewItems);
            return;
        }

        // Trim / clear / replace are infrequent (a trim happens once per ~200 lines past the cap),
        // so a full rebuild there is fine.
        RebuildVisibleEntries();
    }

    private void AppendNewEntries(System.Collections.IList newItems)
    {
        var appended = 0;
        foreach (var item in newItems)
        {
            if (item is not string line)
            {
                continue;
            }

            var entry = ParseLine(line, SelectedSource);

            // The Summary counts describe the whole selected source, so every new entry counts -
            // independent of the level/search filter applied to the visible list.
            TotalEntries++;
            switch (entry.Level)
            {
                case LevelError: ErrorEntries++; break;
                case LevelWarning: WarningEntries++; break;
                case LevelInfo: InfoEntries++; break;
            }

            if (FilterEntry(entry))
            {
                VisibleEntries.Add(entry);
                appended++;
            }
        }

        if (appended > 0)
        {
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private void RebuildVisibleEntries()
    {
        // The visible list honors the level + search filter...
        var sourceEntries = GetSourceLines()
            .Select(line => ParseLine(line, SelectedSource))
            .ToList();

        VisibleEntries.Clear();
        foreach (var entry in sourceEntries.Where(FilterEntry))
        {
            VisibleEntries.Add(entry);
        }

        // ...but the Summary counts describe the entire selected source (all levels), so switching
        // the level filter or typing a search never changes the stats - only switching the source tab
        // does. They're computed from the unfiltered source, not from VisibleEntries.
        TotalEntries = sourceEntries.Count;
        ErrorEntries = sourceEntries.Count(entry => entry.Level == LevelError);
        WarningEntries = sourceEntries.Count(entry => entry.Level == LevelWarning);
        InfoEntries = sourceEntries.Count(entry => entry.Level == LevelInfo);
        OnPropertyChanged(nameof(ActiveSourcesSummary));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private IEnumerable<string> GetSourceLines()
    {
        return SelectedSource switch
        {
            SourceDns => State.DnsLogLines,
            SourceEngine => State.VpnLogLines,
            SourceBridges => State.ActiveBridgeLines,
            _ => State.LogLines
        };
    }

    private IEnumerable<string> GetActiveSources()
    {
        if (State.LogLines.Count > 0)
        {
            yield return LocalizationService.Get("Logs.TabApp");
        }

        if (State.DnsLogLines.Count > 0)
        {
            yield return LocalizationService.Get("Logs.TabDns");
        }

        if (State.VpnLogLines.Count > 0)
        {
            yield return EngineTabLabel;
        }

        if (State.ActiveBridgeLines.Count > 0)
        {
            yield return LocalizationService.Get("Logs.TabBridges");
        }
    }

    private bool FilterEntry(StructuredLogEntry entry)
    {
        if (!string.Equals(SelectedLevel, LevelAll, StringComparison.Ordinal) &&
            !string.Equals(entry.Level, SelectedLevel, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
               || entry.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
               || entry.RawLine.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    // Classify a log line's severity. Explicit level tokens emitted by tor ([notice]/[warn]/[err]),
    // sing-box, arti and ArtiHop (uppercase WARN/INFO/ERROR from Rust tracing) are authoritative and
    // win over keyword heuristics — otherwise descriptive words like "error=none" or sing-box's
    // benign "INFO ... failed to search process" get wrongly flagged red.
    private static string ClassifyLevel(string message)
    {
        // tor-style bracketed levels.
        if (message.Contains("[err", StringComparison.OrdinalIgnoreCase)) return LevelError;
        if (message.Contains("[warn", StringComparison.OrdinalIgnoreCase)) return LevelWarning;
        if (message.Contains("[notice", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("[info", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("[debug", StringComparison.OrdinalIgnoreCase))
        {
            return LevelInfo;
        }

        // Uppercase tracing/sing-box level tokens (case-sensitive, so prose "error"/"warn" is ignored).
        if (HasUpperLevelToken(message, "ERROR") || HasUpperLevelToken(message, "FATAL") || HasUpperLevelToken(message, "CRITICAL")) return LevelError;
        if (HasUpperLevelToken(message, "WARN")) return LevelWarning;
        if (HasUpperLevelToken(message, "INFO") || HasUpperLevelToken(message, "NOTICE") ||
            HasUpperLevelToken(message, "DEBUG") || HasUpperLevelToken(message, "TRACE"))
        {
            return LevelInfo;
        }

        // Heuristic fallback for OnionHop's own (level-less) messages.
        var lower = message.ToLowerInvariant();
        if (lower.Contains("failed to search process") || lower.Contains("success=true"))
        {
            return LevelInfo;
        }

        // Advisory notices are informational cautions, not errors (e.g. the proxy-mode privacy
        // notice mentions "sites fail to load").
        if (lower.Contains("privacy notice") || lower.Contains("notice:") || lower.Contains("warning") || lower.Contains("caution"))
        {
            return LevelWarning;
        }

        // Require strong failure signals — "failed"/"cannot"/etc. — so prose like "fail to load"
        // inside an advisory doesn't turn the whole line red.
        if (lower.Contains("failed") ||
            lower.Contains("exception") ||
            lower.Contains("unable to") ||
            lower.Contains("could not") ||
            lower.Contains("cannot ") ||
            lower.Contains("fatal"))
        {
            return LevelError;
        }

        return LevelInfo;
    }

    private static bool HasUpperLevelToken(string message, string token)
    {
        var idx = message.IndexOf(token, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var before = idx == 0 ? ' ' : message[idx - 1];
            var afterIdx = idx + token.Length;
            var after = afterIdx >= message.Length ? ' ' : message[afterIdx];
            // Standalone uppercase word, not a "TOKEN=value" field.
            if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after) && after != '=')
            {
                return true;
            }

            idx = message.IndexOf(token, idx + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private StructuredLogEntry ParseLine(string line, string source)
    {
        var time = line.Length >= 8 ? line[..8] : "--:--:--";
        var message = line.Length > 9 ? line[9..] : line;
        var level = ClassifyLevel(message);

        return new StructuredLogEntry
        {
            Time = time,
            Source = source switch
            {
                SourceDns => LocalizationService.Get("Logs.TabDns"),
                SourceEngine => EngineTabLabel,
                SourceBridges => LocalizationService.Get("Logs.TabBridges"),
                _ => LocalizationService.Get("Logs.TabApp")
            },
            Message = message,
            RawLine = line,
            Level = level,
            LevelLabel = level switch
            {
                LevelError => LocalizationService.Get("Logs.LevelError"),
                LevelWarning => LocalizationService.Get("Logs.LevelWarning"),
                _ => LocalizationService.Get("Logs.LevelInfo")
            },
            LevelTone = level switch
            {
                LevelError => "danger",
                LevelWarning => "warning",
                _ => "info"
            }
        };
    }
}

public sealed class StructuredLogEntry
{
    public string Time { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string LevelLabel { get; init; } = string.Empty;
    public string LevelTone { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string RawLine { get; init; } = string.Empty;
}
