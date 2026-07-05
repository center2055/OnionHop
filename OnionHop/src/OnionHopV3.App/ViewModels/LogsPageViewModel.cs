using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
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

        // Polls tor's control port for the relays it is connected to, so the bridges tab can mark
        // the bridge actually in use (#69). Only runs while that tab is visible and bridges exist.
        _bridgeStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _bridgeStatusTimer.Tick += async (_, _) => await RefreshBridgeStatusAsync();

        RebuildVisibleEntries();
        RebuildBridgeRows();
    }

    private readonly DispatcherTimer _bridgeStatusTimer;
    private readonly Dictionary<string, DateTime> _bridgeLastSeenUtc = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _connectedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private bool _bridgeStatusRefreshInProgress;

    // Bridges tab sort state (#69): which column and direction the rows are ordered by.
    [ObservableProperty] private string _bridgeSortColumn = "Type";
    [ObservableProperty] private bool _bridgeSortDescending;

    public ObservableCollection<StructuredLogEntry> VisibleEntries { get; } = [];

    // The "Current Bridge" tab shows connection state, not a timestamped log stream, so it gets its
    // own Type | Address | Details rows instead of being squeezed through the log-line parser (which
    // sliced the first 8 characters of the bridge line into the Time column - issue #69).
    public ObservableCollection<BridgeRowEntry> BridgeRows { get; } = [];

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
    public bool ShowEmptyState => IsBridgesSelected ? BridgeRows.Count == 0 : VisibleEntries.Count == 0;
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
        // The bridges tab renders BridgeRows, so Copy/Export must follow what is actually visible
        // there (a lingering level filter from another tab does not apply to bridges).
        return SelectedSource == SourceBridges
            ? string.Join(Environment.NewLine, BridgeRows.Select(row => row.RawLine))
            : string.Join(Environment.NewLine, VisibleEntries.Select(entry => entry.RawLine));
    }

    /// <summary>True while the bridges tab is selected, so the view can switch Copy/Export to the
    /// bridge-specific formats (issue #56).</summary>
    public bool IsBridgesExport => IsBridgesSelected;

    /// <summary>
    /// Copy text for the bridges tab (issue #56): only the bridge(s) Tor is actually using, so the
    /// user gets the working bridge to carry to another device rather than the whole supplemented
    /// list. Falls back to bridges seen in use this session, then to all rows when no live status is
    /// available (e.g. the Arti engines expose none).
    /// </summary>
    public string GetBridgeCopyText()
    {
        var inUse = BridgeRows.Where(row => row.StatusTone == "success").ToList();
        var seen = BridgeRows.Where(row => row.HasStatus).ToList();
        var chosen = inUse.Count > 0 ? inUse : seen.Count > 0 ? seen : BridgeRows.ToList();
        return string.Join(Environment.NewLine, chosen.Select(row => row.RawLine));
    }

    /// <summary>
    /// CSV of the current bridges with status columns (issue #56): Type, Address, Status, Fingerprint
    /// and the raw bridge line. Status is "In use", a last-seen time, or empty.
    /// </summary>
    public string GetBridgeCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Type,Address,Status,Fingerprint,Bridge line\r\n");
        foreach (var row in BridgeRows)
        {
            sb.Append(CsvField(row.Type)).Append(',')
              .Append(CsvField(row.Address)).Append(',')
              .Append(CsvField(row.StatusLabel)).Append(',')
              .Append(CsvField(row.Fingerprint ?? string.Empty)).Append(',')
              .Append(CsvField(row.RawLine)).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
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

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisibleEntries();
        RebuildBridgeRows();
    }

    partial void OnSelectedSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsAppSelected));
        OnPropertyChanged(nameof(IsDnsSelected));
        OnPropertyChanged(nameof(IsEngineSelected));
        OnPropertyChanged(nameof(IsBridgesSelected));
        OnPropertyChanged(nameof(ActiveSourcesSummary));
        RebuildVisibleEntries();
        OnPropertyChanged(nameof(ShowEmptyState));
        UpdateBridgeStatusTimer();
    }

    private void UpdateBridgeStatusTimer()
    {
        var shouldRun = IsBridgesSelected && HasActiveBridges;
        if (shouldRun && !_bridgeStatusTimer.IsEnabled)
        {
            _bridgeStatusTimer.Start();
            _ = RefreshBridgeStatusAsync();
        }
        else if (!shouldRun && _bridgeStatusTimer.IsEnabled)
        {
            _bridgeStatusTimer.Stop();
        }
    }

    private async Task RefreshBridgeStatusAsync()
    {
        if (_bridgeStatusRefreshInProgress || !IsBridgesSelected || !HasActiveBridges)
        {
            return;
        }

        _bridgeStatusRefreshInProgress = true;
        try
        {
            var fingerprints = await State.GetConnectedRelayFingerprintsAsync();
            var connected = new HashSet<string>(fingerprints, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            foreach (var fp in connected)
            {
                _bridgeLastSeenUtc[fp] = now;
            }

            // Only rebuild the rows when the in-use set actually changed; the timer fires every
            // few seconds and pointless rebuilds would fight row selection.
            if (!connected.SetEquals(_connectedFingerprints))
            {
                _connectedFingerprints = connected;
                RebuildBridgeRows();
            }
        }
        catch
        {
            // Status polling is best-effort; the rows just keep their last known state.
        }
        finally
        {
            _bridgeStatusRefreshInProgress = false;
        }
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
            // Bridges set/cleared: refresh the "Current Bridge" tab's visibility and rows regardless
            // of pause - the list reflects live connection state, not a streaming log.
            OnPropertyChanged(nameof(HasActiveBridges));
            RebuildBridgeRows();
            UpdateBridgeStatusTimer();
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

    private void RebuildBridgeRows()
    {
        var rows = new List<BridgeRowEntry>();
        foreach (var line in State.ActiveBridgeLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var row = ParseBridgeLine(line);
            if (row.Fingerprint is { } fp)
            {
                // Live status from the control-port poll (#69): a badge while tor holds a connection
                // to this bridge, the last-seen time once it does not anymore.
                if (_connectedFingerprints.Contains(fp))
                {
                    row = row with
                    {
                        StatusLabel = LocalizationService.Get("Logs.BridgeInUse"),
                        StatusTone = "success"
                    };
                }
                else if (_bridgeLastSeenUtc.TryGetValue(fp, out var lastSeen))
                {
                    row = row with
                    {
                        StatusLabel = lastSeen.ToLocalTime().ToString("HH:mm:ss"),
                        StatusTone = "neutral"
                    };
                }
            }

            if (string.IsNullOrWhiteSpace(SearchText) ||
                row.RawLine.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(row);
            }
        }

        // In-use bridges always float to the top; the chosen column then orders the rest (#69).
        var sorted = SortBridgeRows(rows);
        BridgeRows.Clear();
        foreach (var row in sorted)
        {
            BridgeRows.Add(row);
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private IEnumerable<BridgeRowEntry> SortBridgeRows(List<BridgeRowEntry> rows)
    {
        Func<BridgeRowEntry, string> key = BridgeSortColumn switch
        {
            "Address" => r => r.Address,
            "Status" => r => r.StatusLabel,
            _ => r => r.Type
        };

        var ordered = BridgeSortDescending
            ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase);

        // Keep the bridge actually in use pinned to the top regardless of sort, so it stays obvious.
        return ordered.OrderByDescending(r => r.StatusTone == "success");
    }

    /// <summary>Sort the bridges tab by a column, toggling ascending/descending on repeat clicks (#69).</summary>
    [RelayCommand]
    private void SortBridges(string? column)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return;
        }

        if (string.Equals(BridgeSortColumn, column, StringComparison.Ordinal))
        {
            BridgeSortDescending = !BridgeSortDescending;
        }
        else
        {
            BridgeSortColumn = column;
            BridgeSortDescending = false;
        }

        RebuildBridgeRows();
    }

    /// <summary>Copy a single bridge row's raw line (#69: one-tap copy per row).</summary>
    public string GetBridgeRowLine(BridgeRowEntry? row) => row?.RawLine ?? string.Empty;

    // A bridge line is "<transport> host:port fingerprint key=value ..." for pluggable transports,
    // or just "host:port fingerprint" for vanilla bridges (no transport token). Transport names never
    // contain ':', so a colon in the first token means the line starts with the address.
    internal static BridgeRowEntry ParseBridgeLine(string line)
    {
        var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string type;
        string address;
        string details;
        if (tokens.Length == 0)
        {
            type = "?";
            address = line.Trim();
            details = string.Empty;
        }
        else if (tokens[0].Contains(':'))
        {
            type = "vanilla";
            address = tokens[0];
            details = string.Join(' ', tokens.Skip(1));
        }
        else
        {
            type = tokens[0];
            address = tokens.Length > 1 ? tokens[1] : string.Empty;
            details = string.Join(' ', tokens.Skip(2));
        }

        return new BridgeRowEntry
        {
            Type = type,
            Address = address,
            Details = details,
            RawLine = line,
            Fingerprint = TryExtractBridgeFingerprint(tokens)
        };
    }

    // The bridge's identity fingerprint is the first standalone 40-hex token in the line; it is what
    // tor reports on the control port for the connection, so it links rows to live status (#69).
    private static string? TryExtractBridgeFingerprint(IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.Length != 40 || token.Contains('='))
            {
                continue;
            }

            var isHex = true;
            foreach (var ch in token)
            {
                if (!Uri.IsHexDigit(ch))
                {
                    isHex = false;
                    break;
                }
            }

            if (isHex)
            {
                return token.ToUpperInvariant();
            }
        }

        return null;
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

/// <summary>One row of the "Current Bridge" tab: a parsed bridge line, not a log entry (issue #69).</summary>
public sealed record BridgeRowEntry
{
    public string Type { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string RawLine { get; init; } = string.Empty;
    /// <summary>The bridge's identity fingerprint (40 hex), when the line carries one.</summary>
    public string? Fingerprint { get; init; }
    /// <summary>"In use" while tor is connected to this bridge; the last-seen time afterwards.</summary>
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "neutral";
    public bool HasStatus => StatusLabel.Length > 0;
}
