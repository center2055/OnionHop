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
using OnionHopV2.App.Services;
using OnionHopV2.Core.Services;

namespace OnionHopV2.App.ViewModels;

public sealed partial class BridgeScannerPageViewModel : PageViewModelBase
{
    private CancellationTokenSource? _scanCts;
    private readonly List<string> _workingLines = new();

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
    }

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
            ProgressText = "Fetching bridge list…";
            var fetch = await BridgeSourceService
                .FetchAsync(SelectedCategory, SelectedTransport, SelectedIpVersion, null, State.AppendLog, CancellationToken.None)
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
        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        State.AppendLog($"Bridge scanner: probing {candidates.Count} {SelectedTransport} bridge(s) " +
                        $"({SelectedCategory}, {SelectedIpVersion}) with {Workers} worker(s), {TimeoutSeconds}s timeout.");

        // Progress<T> captures the UI SynchronizationContext here, so OnResult runs on the UI thread.
        var progress = new Progress<BridgeScanResult>(OnResult);

        try
        {
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
            UpdateSummary(final: true);
        }
    }

    [RelayCommand]
    private void StopScan()
    {
        _scanCts?.Cancel();
        ProgressText = "Stopping…";
    }

    [RelayCommand]
    private void ApplyWorkingAsCustomBridges()
    {
        if (_workingLines.Count == 0)
        {
            return;
        }

        // Push the reachable bridges into the app's custom-bridge setting so the user can connect
        // with exactly the bridges that passed the scan.
        State.CustomBridges = string.Join(Environment.NewLine, _workingLines);
        State.SelectedBridgeType = "custom";
        State.UseTorBridges = true;
        State.AppendLog($"Bridge scanner: applied {_workingLines.Count} working bridge(s) as custom bridges.");
        ProgressText = $"Applied {_workingLines.Count} working bridge(s) as custom bridges.";
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
