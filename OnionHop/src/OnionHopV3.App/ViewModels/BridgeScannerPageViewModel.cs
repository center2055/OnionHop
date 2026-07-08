using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using OnionHopV3.App.Services;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.ViewModels;

public sealed partial class BridgeScannerPageViewModel : PageViewModelBase
{
    private const string SubTabBridge = "bridge";
    private const string SubTabSni = "sni";
    private const string SubTabSaved = "saved";

    private CancellationTokenSource? _scanCts;
    private readonly List<string> _workingLines = new();
    // Ping + reachability per working line, so a saved bridge carries its latency into the library.
    private readonly Dictionary<string, (int? Ping, string Status)> _workingMeta = new(StringComparer.Ordinal);
    private readonly SavedBridgeStore _savedStore = new();

    public BridgeScannerPageViewModel(AppStateViewModel state)
        : base("Nav.Scanner", MaterialIconKind.Radar, state, 0xE721)
    {
        foreach (var category in BridgeSourceService.Categories)
        {
            Categories.Add(category);
        }

        foreach (var transport in BridgeSourceService.Transports)
        {
            Transports.Add(transport);
        }

        foreach (var ipVersion in BridgeSourceService.IpVersions)
        {
            IpVersions.Add(ipVersion);
        }

        // The Scanner page hosts three subtabs (v3.6): the bridge scanner (this VM), the SNI scanner,
        // and the saved-bridges library. The child VMs share one library store.
        Sni = new SniScannerViewModel(state, _savedStore);
        Saved = new SavedBridgesViewModel(state, _savedStore);
    }

    public SniScannerViewModel Sni { get; }
    public SavedBridgesViewModel Saved { get; }

    // Which subtab is showing. Setting it refreshes the library when that tab opens so freshly-saved
    // items appear without a manual reload.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBridgeTab))]
    [NotifyPropertyChangedFor(nameof(IsSniTab))]
    [NotifyPropertyChangedFor(nameof(IsSavedTab))]
    private string _subTab = SubTabBridge;

    public bool IsBridgeTab => SubTab == SubTabBridge;
    public bool IsSniTab => SubTab == SubTabSni;
    public bool IsSavedTab => SubTab == SubTabSaved;

    partial void OnSubTabChanged(string value)
    {
        if (value == SubTabSaved)
        {
            Saved.Refresh();
        }
        else if (value == SubTabSni)
        {
            // Populate the Request SNI country picker the first time the tab is opened (best-effort;
            // stays empty and the picker hides if the source is unreachable).
            _ = Sni.LoadCountriesCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void ShowBridgeTab() => SubTab = SubTabBridge;

    [RelayCommand]
    private void ShowSniTab() => SubTab = SubTabSni;

    [RelayCommand]
    private void ShowSavedTab() => SubTab = SubTabSaved;

    public ObservableCollection<string> Categories { get; } = [];
    public ObservableCollection<string> Transports { get; } = [];
    public ObservableCollection<string> IpVersions { get; } = [];
    public ObservableCollection<BridgeScanRow> Results { get; } = [];

    [ObservableProperty] private string _selectedCategory = "Tested & Active";
    [ObservableProperty] private string _selectedTransport = "obfs4";
    [ObservableProperty] private string _selectedIpVersion = "IPv4";
    [ObservableProperty] private int _workers = 20;
    [ObservableProperty] private int _timeoutSeconds = 5;
    [ObservableProperty] private bool _useCustomBridges;
    [ObservableProperty] private string _customBridges = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan))]
    private bool _isScanning;

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "Ready.";
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _hasResults;

    private int _total;
    private int _completed;
    private int _reachable;
    private int _slow;

    public bool CanStartScan => !IsScanning;

    /// <summary>Newline-joined working bridge lines, consumed by the view's "Export Working" save dialog.</summary>
    public string WorkingBridgeText => string.Join(Environment.NewLine, _workingLines);

    public bool HasWorkingBridges => _workingLines.Count > 0;

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var scanStarted = false;

        try
        {
            IReadOnlyList<string> candidates;
            if (UseCustomBridges)
            {
                candidates = SplitLines(CustomBridges);
                if (candidates.Count == 0)
                {
                    ProgressText = "Paste at least one bridge line first.";
                    return;
                }
            }
            else
            {
                ProgressText = "Fetching bridge list...";
                var fetch = await BridgeSourceService
                    .FetchAsync(SelectedCategory, SelectedTransport, SelectedIpVersion, null, State.AppendLog, _scanCts.Token)
                    .ConfigureAwait(true);
                if (fetch == null || fetch.Lines.Count == 0)
                {
                    ProgressText = "Could not fetch bridges from any mirror. Check your connection or paste custom bridges.";
                    State.AppendLog("Bridge scanner: all bridge-source mirrors failed.");
                    return;
                }

                candidates = fetch.Lines;
            }

            ResetState(candidates.Count);
            scanStarted = true;
            State.AppendLog($"Bridge scanner: probing {candidates.Count} {SelectedTransport} bridge(s) " +
                            $"({SelectedCategory}, {SelectedIpVersion}) with {Workers} worker(s), {TimeoutSeconds}s timeout.");

            // Progress<T> captures the UI SynchronizationContext here, so OnResult runs on the UI thread.
            var progress = new Progress<BridgeScanResult>(OnResult);

            await BridgeScanService
                .ScanAsync(candidates, Workers, TimeSpan.FromSeconds(TimeoutSeconds), progress, _scanCts.Token)
                .ConfigureAwait(true);
            ProgressText = "Done.";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Scan stopped.";
        }
        catch (Exception ex)
        {
            ProgressText = $"Scan failed: {ex.Message}";
            State.AppendLog($"Bridge scanner error: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
            if (scanStarted)
            {
                UpdateSummary(final: true);
            }
        }
    }

    [RelayCommand]
    private void StopScan()
    {
        _scanCts?.Cancel();
        ProgressText = "Stopping…";
    }

    /// <summary>
    /// Fetch the selected category/transport bridge list into the input box WITHOUT scanning
    /// (BridgeHop-style two-step: load, then scan), so the user can review or edit the list first.
    /// </summary>
    [RelayCommand]
    private async Task LoadBridgesAsync()
    {
        if (IsScanning)
        {
            return;
        }

        try
        {
            ProgressText = "Fetching bridge list…";
            var fetch = await BridgeSourceService
                .FetchAsync(SelectedCategory, SelectedTransport, SelectedIpVersion, null, State.AppendLog, CancellationToken.None)
                .ConfigureAwait(true);
            if (fetch == null || fetch.Lines.Count == 0)
            {
                ProgressText = "Could not fetch bridges from any mirror. Check your connection or paste a custom list.";
                return;
            }

            CustomBridges = string.Join(Environment.NewLine, fetch.Lines);
            UseCustomBridges = true;
            ProgressText = $"Loaded {fetch.Lines.Count} bridge(s). Press Start Scan to test them.";
        }
        catch (Exception ex)
        {
            ProgressText = $"Load failed: {ex.Message}";
        }
    }

    /// <summary>Load bridge lines from imported file text into the input box (used by Import file).</summary>
    public void LoadBridgesFromText(string text)
    {
        CustomBridges = text;
        UseCustomBridges = true;
        ProgressText = "Imported bridge list. Press Start Scan to test them.";
    }

    [RelayCommand]
    private void ApplyWorkingAsCustomBridges()
    {
        if (_workingLines.Count == 0)
        {
            return;
        }

        // Push the reachable bridges into the app's custom-bridge setting so the user can connect
        // with exactly the bridges that passed the scan. Selecting the Custom source makes them
        // actually take effect (issue #70).
        State.CustomBridges = string.Join(Environment.NewLine, _workingLines);
        State.SelectedBridgeType = "custom";
        State.BridgeSourceMode = AppStateViewModel.BridgeSourceCustom;
        State.UseTorBridges = true;
        State.AppendLog($"Bridge scanner: applied {_workingLines.Count} working bridge(s) as custom bridges.");
        ProgressText = $"Applied {_workingLines.Count} working bridge(s) as custom bridges.";
    }

    /// <summary>Save the working bridges from the last scan to the library (v3.6) for reuse later.</summary>
    [RelayCommand]
    private void SaveWorkingToLibrary()
    {
        if (_workingLines.Count == 0)
        {
            return;
        }

        var entries = _workingLines.Select(line =>
        {
            _workingMeta.TryGetValue(line, out var meta);
            return new SavedBridge
            {
                Line = line,
                Kind = SavedBridgeKind.Bridge,
                Transport = SelectedTransport,
                Source = "bridge-scan",
                AddedUtc = DateTime.UtcNow.ToString("o"),
                LastStatus = string.IsNullOrEmpty(meta.Status) ? "reachable" : meta.Status,
                LastPingMs = meta.Ping
            };
        });

        var added = _savedStore.AddRange(entries);
        ProgressText = added > 0
            ? $"Saved {added} bridge(s) to your library."
            : "Those bridges are already in your library.";
        Saved.Refresh();
    }

    private void OnResult(BridgeScanResult result)
    {
        var (tone, label) = result.Reachability switch
        {
            BridgeReachability.Reachable => ("success", $"✔ {result.Detail}"),
            BridgeReachability.Slow => ("warning", $"✔ {result.Detail}"),
            BridgeReachability.Fronted => ("accent", $"↯ {result.Detail}"),
            BridgeReachability.Unreachable => ("danger", $"✘ {result.Detail}"),
            BridgeReachability.Unparsed => ("neutral", result.Detail),
            _ => ("neutral", "—")
        };

        Results.Add(new BridgeScanRow
        {
            Transport = result.Transport,
            Host = result.Host,
            Port = result.Port > 0 ? result.Port.ToString(CultureInfo.InvariantCulture) : "—",
            Ping = result.PingMs.HasValue ? result.PingMs.Value.ToString(CultureInfo.InvariantCulture) : "—",
            StatusText = label,
            Tone = tone,
            RawLine = result.RawLine,
            IsWorking = result.IsWorking
        });

        HasResults = true;
        if (result.IsWorking)
        {
            _workingLines.Add(result.RawLine);
            _workingMeta[result.RawLine] = (result.PingMs, result.Reachability == BridgeReachability.Slow ? "slow" : "reachable");
            _reachable += result.Reachability == BridgeReachability.Reachable ? 1 : 0;
            _slow += result.Reachability == BridgeReachability.Slow ? 1 : 0;
        }

        _completed++;
        ProgressValue = _total > 0 ? _completed * 100.0 / _total : 0;
        ProgressText = $"Scanning… {_completed}/{_total}";
        OnPropertyChanged(nameof(HasWorkingBridges));
        OnPropertyChanged(nameof(WorkingBridgeText));
        UpdateSummary(final: false);
    }

    private void ResetState(int total)
    {
        Results.Clear();
        _workingLines.Clear();
        _workingMeta.Clear();
        _total = total;
        _completed = 0;
        _reachable = 0;
        _slow = 0;
        ProgressValue = 0;
        SummaryText = string.Empty;
        HasResults = false;
        OnPropertyChanged(nameof(HasWorkingBridges));
        OnPropertyChanged(nameof(WorkingBridgeText));
    }

    private void UpdateSummary(bool final)
    {
        var working = _workingLines.Count;
        var unreachable = _completed - working;
        SummaryText = final
            ? $"✔ {working} reachable  /  {unreachable} unreachable  /  {_total} total"
            : $"{working} reachable so far  /  {_completed}/{_total} checked";
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .ToList();
    }
}

public sealed class BridgeScanRow
{
    public string Transport { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string Port { get; init; } = string.Empty;
    public string Ping { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string Tone { get; init; } = "neutral";
    public string RawLine { get; init; } = string.Empty;
    public bool IsWorking { get; init; }
}
