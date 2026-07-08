using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnionHopV3.App.Services;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.ViewModels;

/// <summary>
/// SNI scanner subtab (v3.6). Two modes: probe a list of candidate domains as SNI, or probe one SNI
/// across an IPv4 CIDR range. Working SNIs can be applied as the app's custom SNI/front hosts or saved
/// to the library. See <see cref="SniScanService"/>.
/// </summary>
public sealed partial class SniScannerViewModel : ObservableObject
{
    private readonly AppStateViewModel _state;
    private readonly SavedBridgeStore _store;
    private CancellationTokenSource? _scanCts;
    private readonly List<string> _workingSnis = new();
    private readonly List<SavedBridge> _workingEntries = new();

    public SniScannerViewModel(AppStateViewModel state, SavedBridgeStore store)
    {
        _state = state;
        _store = store;
        _progressText = L("Sni.Ready", "Ready.");
    }

    // Localized status text with an English fallback, so messages match the app language instead of
    // showing English on a translated UI.
    private static string L(string key, string fallback)
    {
        var v = LocalizationService.Get(key);
        return string.IsNullOrEmpty(v) || v == key ? fallback : v;
    }

    public ObservableCollection<SniScanRow> Results { get; } = [];

    // false = domain mode (test many domains as SNI); true = range mode (one SNI across a CIDR).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDomainMode))]
    private bool _isRangeMode;

    public bool IsDomainMode => !IsRangeMode;

    [ObservableProperty] private string _domainList = string.Empty;
    [ObservableProperty] private string _rangeSni = string.Empty;
    [ObservableProperty] private string _rangeCidr = string.Empty;
    [ObservableProperty] private int _workers = 20;
    [ObservableProperty] private int _timeoutSeconds = 5;
    [ObservableProperty] private int _port = 443;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan))]
    private bool _isScanning;

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _hasResults;

    private int _total;
    private int _completed;

    public bool CanStartScan => !IsScanning;
    public bool HasWorking => _workingSnis.Count > 0;

    /// <summary>Newline-joined working SNI hosts, consumed by the view's "Export" save dialog.</summary>
    public string WorkingSniText => string.Join(Environment.NewLine, _workingSnis);

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var started = false;
        try
        {
            IReadOnlyList<SniScanResult> _;
            var progress = new Progress<SniScanResult>(OnResult);

            if (IsRangeMode)
            {
                var sni = SniScanService.NormalizeSniHost(RangeSni);
                if (sni.Length == 0)
                {
                    ProgressText = L("Sni.NeedSni", "Enter an SNI host to test across the range.");
                    return;
                }

                if (!SniScanService.TryEnumerateCidr(RangeCidr?.Trim() ?? string.Empty, SniScanService.MaxRangeHosts, out var addresses, out var truncated))
                {
                    ProgressText = L("Sni.NeedCidr", "Enter a valid IPv4 CIDR range, e.g. 104.16.0.0/24.");
                    return;
                }

                ResetState(addresses.Count);
                started = true;
                _state.AppendLog($"SNI scanner: probing SNI '{sni}' across {addresses.Count} IP(s) in {RangeCidr}" +
                                 (truncated ? $" (capped at {SniScanService.MaxRangeHosts})." : "."));
                await SniScanService
                    .ScanCidrAsync(sni, RangeCidr!.Trim(), Workers, TimeSpan.FromSeconds(TimeoutSeconds), Port, progress, _scanCts.Token)
                    .ConfigureAwait(true);
            }
            else
            {
                var domains = SplitLines(DomainList);
                if (domains.Count == 0)
                {
                    ProgressText = L("Sni.NeedDomains", "Enter at least one domain to test.");
                    return;
                }

                ResetState(domains.Count);
                started = true;
                _state.AppendLog($"SNI scanner: probing {domains.Count} domain(s) as SNI on :{Port}.");
                await SniScanService
                    .ScanDomainsAsync(domains, Workers, TimeSpan.FromSeconds(TimeoutSeconds), Port, progress, _scanCts.Token)
                    .ConfigureAwait(true);
            }

            ProgressText = L("Sni.Done", "Done.");
        }
        catch (OperationCanceledException)
        {
            ProgressText = L("Sni.Stopped", "Scan stopped.");
        }
        catch (Exception ex)
        {
            ProgressText = $"{L("Sni.Failed", "Scan failed")}: {ex.Message}";
            _state.AppendLog($"SNI scanner error: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
            if (started)
            {
                UpdateSummary(final: true);
            }
        }
    }

    [RelayCommand]
    private void StopScan()
    {
        _scanCts?.Cancel();
        ProgressText = L("Sni.Stopping", "Stopping…");
    }

    [RelayCommand]
    private void SetDomainMode() => IsRangeMode = false;

    [RelayCommand]
    private void SetRangeMode() => IsRangeMode = true;

    /// <summary>
    /// Fill the domain box with the built-in starter list of common SNI/front candidates, so the user
    /// has something to scan without needing to know which domains to try (the SNI equivalent of the
    /// bridge scanner's "Load bridges"). Switches to domain mode and doesn't clobber a non-empty box.
    /// </summary>
    [RelayCommand]
    private void LoadCandidates()
    {
        IsRangeMode = false;
        var existing = SplitLines(DomainList);
        var merged = existing
            .Concat(SniScanService.DefaultSniCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        DomainList = string.Join(Environment.NewLine, merged);
        ProgressText = L("Sni.CandidatesLoaded", "Loaded a starter list of SNI candidates. Press Start Scan.");
    }

    /// <summary>Apply the working SNI hosts as the app's custom SNI/front hosts (used by fronted bridges).</summary>
    [RelayCommand]
    private void ApplyWorkingAsSni()
    {
        if (_workingSnis.Count == 0)
        {
            return;
        }

        _state.CustomSniHosts = string.Join(", ", _workingSnis.Distinct(StringComparer.OrdinalIgnoreCase));
        _state.AppendLog($"SNI scanner: applied {_workingSnis.Distinct(StringComparer.OrdinalIgnoreCase).Count()} SNI host(s) as custom SNI hosts.");
        ProgressText = "Applied working SNI hosts to your custom SNI setting.";
    }

    /// <summary>Save the working SNI hosts to the library.</summary>
    [RelayCommand]
    private void SaveWorkingToLibrary()
    {
        if (_workingEntries.Count == 0)
        {
            return;
        }

        var added = _store.AddRange(_workingEntries);
        ProgressText = added > 0
            ? $"Saved {added} SNI host(s) to your library."
            : "Those SNI hosts are already in your library.";
    }

    private void OnResult(SniScanResult result)
    {
        var (tone, label) = result.Reachability switch
        {
            SniReachability.Reachable => ("success", $"✔ {result.Detail}"),
            SniReachability.Slow => ("warning", $"✔ {result.Detail}"),
            SniReachability.Blocked => ("danger", $"✘ {result.Detail}"),
            _ => ("neutral", result.Detail)
        };

        Results.Add(new SniScanRow
        {
            Sni = result.Sni,
            Ip = result.Ip,
            Port = result.Port.ToString(CultureInfo.InvariantCulture),
            Ping = result.PingMs.HasValue ? result.PingMs.Value.ToString(CultureInfo.InvariantCulture) : "—",
            StatusText = label,
            Tone = tone
        });

        HasResults = true;
        if (result.IsWorking)
        {
            // Domain mode: the SNI is the domain that passed. Range mode: the SNI is fixed and the
            // useful finding is which IPs served it - keep the SNI as the applyable value either way.
            if (!_workingSnis.Contains(result.Sni, StringComparer.OrdinalIgnoreCase))
            {
                _workingSnis.Add(result.Sni);
            }

            _workingEntries.Add(new SavedBridge
            {
                Line = result.Sni,
                Kind = SavedBridgeKind.Sni,
                Source = "sni-scan",
                Label = IsRangeMode ? $"via {result.Ip}" : string.Empty,
                AddedUtc = DateTime.UtcNow.ToString("o"),
                LastStatus = result.Reachability == SniReachability.Slow ? "slow" : "reachable",
                LastPingMs = result.PingMs
            });
        }

        _completed++;
        ProgressValue = _total > 0 ? _completed * 100.0 / _total : 0;
        ProgressText = $"{L("Sni.Scanning", "Scanning…")} {_completed}/{_total}";
        OnPropertyChanged(nameof(HasWorking));
        OnPropertyChanged(nameof(WorkingSniText));
        UpdateSummary(final: false);
    }

    private void ResetState(int total)
    {
        Results.Clear();
        _workingSnis.Clear();
        _workingEntries.Clear();
        _total = total;
        _completed = 0;
        ProgressValue = 0;
        SummaryText = string.Empty;
        HasResults = false;
        OnPropertyChanged(nameof(HasWorking));
        OnPropertyChanged(nameof(WorkingSniText));
    }

    private void UpdateSummary(bool final)
    {
        var working = _workingSnis.Count;
        SummaryText = final
            ? $"✔ {working} working  /  {_completed}/{_total} checked"
            : $"{working} working so far  /  {_completed}/{_total} checked";
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .ToList();
    }
}

public sealed class SniScanRow
{
    public string Sni { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public string Port { get; init; } = string.Empty;
    public string Ping { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string Tone { get; init; } = "neutral";
}
