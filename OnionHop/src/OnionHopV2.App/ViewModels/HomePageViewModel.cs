using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using OnionHopV2.App.Services;

namespace OnionHopV2.App.ViewModels;

public sealed class HomePageViewModel : PageViewModelBase
{
    private const int MaxRecentHomeEvents = 5;
    private readonly Action _openAdvancedPreferences;

    public HomePageViewModel(AppStateViewModel state, Action openAdvancedPreferences)
        : base("Nav.Home", MaterialIconKind.HomeOutline, state, 0xE80F)
    {
        _openAdvancedPreferences = openAdvancedPreferences;
        OpenAdvancedPreferencesCommand = new RelayCommand(() => _openAdvancedPreferences());

        State.LogLines.CollectionChanged += OnLogsCollectionChanged;
        State.PropertyChanged += OnStatePropertyChanged;
        RefreshRecentEvents();
    }

    public ObservableCollection<HomeActivityItem> RecentEvents { get; } = [];

    public IRelayCommand OpenAdvancedPreferencesCommand { get; }

    public string SelectedExitLabel => State.SelectedLocationOption?.Label ?? LocalizationService.Get("Home.Automatic");
    public string SelectedEntryLabel => State.SelectedEntryLocationOption?.Label ?? LocalizationService.Get("Home.Automatic");
    public string GuardNodeLabel => State.UseTorBridges ? LocalizationService.Get("Home.CircuitBridge") : SelectedEntryLabel;
    public string MiddleNodeLabel => State.IsConnected ? LocalizationService.Get("Home.CircuitMiddleActive") : LocalizationService.Get("Home.CircuitMiddleIdle");
    public string ExitNodeLabel => State.IsManualExitNodeFingerprintSet ? State.ManualExitFingerprintSummary : SelectedExitLabel;
    public string InternetNodeLabel => State.IsConnected ? State.CurrentIp : LocalizationService.Get("Home.CircuitInternetIdle");
    public bool HasRecentEvents => RecentEvents.Count > 0;
    public bool ShowRecentEventsEmptyState => RecentEvents.Count == 0;

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshRecentEvents();
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppStateViewModel.SelectedLocationOption)
            or nameof(AppStateViewModel.SelectedEntryLocationOption)
            or nameof(AppStateViewModel.SelectedLocation)
            or nameof(AppStateViewModel.SelectedEntryLocation)
            or nameof(AppStateViewModel.UseTorBridges)
            or nameof(AppStateViewModel.CurrentIp)
            or nameof(AppStateViewModel.IsConnected)
            or nameof(AppStateViewModel.ExitNodeFingerprint))
        {
            OnPropertyChanged(nameof(SelectedExitLabel));
            OnPropertyChanged(nameof(SelectedEntryLabel));
            OnPropertyChanged(nameof(GuardNodeLabel));
            OnPropertyChanged(nameof(MiddleNodeLabel));
            OnPropertyChanged(nameof(ExitNodeLabel));
            OnPropertyChanged(nameof(InternetNodeLabel));
        }
    }

    private void RefreshRecentEvents()
    {
        RecentEvents.Clear();
        foreach (var line in State.LogLines.Reverse().Take(MaxRecentHomeEvents).Reverse())
        {
            RecentEvents.Add(ParseEvent(line));
        }

        OnPropertyChanged(nameof(HasRecentEvents));
        OnPropertyChanged(nameof(ShowRecentEventsEmptyState));
    }

    private static HomeActivityItem ParseEvent(string line)
    {
        var timestamp = line.Length >= 8 ? line[..8] : "--:--:--";
        var message = NormalizeActivityMessage(line.Length > 9 ? line[9..] : line);
        var tone = message.Contains("error", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            ? "danger"
            : message.Contains("warn", StringComparison.OrdinalIgnoreCase)
                ? "warning"
                : message.Contains("connected", StringComparison.OrdinalIgnoreCase)
                  || message.Contains("refreshed", StringComparison.OrdinalIgnoreCase)
                    ? "success"
                    : "neutral";

        return new HomeActivityItem(timestamp, message, tone);
    }

    private static string NormalizeActivityMessage(string message)
    {
        var trimmed = message.Trim();

        if (trimmed.StartsWith("IP check via DIRECT", StringComparison.OrdinalIgnoreCase))
        {
            return "Direct IP check completed.";
        }

        if (trimmed.StartsWith("IP check:", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection IP check completed.";
        }

        if (trimmed.Length <= 96)
        {
            return trimmed;
        }

        return $"{trimmed[..93]}...";
    }
}

public sealed record HomeActivityItem(string Time, string Message, string Tone);
