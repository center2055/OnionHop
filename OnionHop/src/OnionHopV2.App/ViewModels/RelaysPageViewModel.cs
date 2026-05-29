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
using OnionHopV2.Core.Models;
using OnionHopV2.Core.Services;

namespace OnionHopV2.App.ViewModels;

public sealed partial class RelaysPageViewModel : PageViewModelBase
{
    private readonly TorRelayDirectoryService _relayDirectoryService = new();
    private readonly List<RelayDirectoryItem> _allRelays = [];
    private bool _hasLoadedRelays;

    public RelaysPageViewModel(AppStateViewModel state)
        : base("Nav.Relays", MaterialIconKind.ViewListOutline, state, 0xE774)
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ToggleFavoriteCommand = new RelayCommand<RelayDirectoryItem>(ToggleFavorite);
        UseAsPreferredExitCommand = new AsyncRelayCommand(ApplyPreferredExitAsync, CanApplyPreferredExit);
        UseAsPreferredGuardCommand = new AsyncRelayCommand(ApplyPreferredGuardAsync, CanApplyPreferredGuard);
        UseAsPreferredMiddleCommand = new AsyncRelayCommand(ApplyPreferredMiddleAsync, CanApplyPreferredMiddle);
        UseAsPreferredRoleCommand = new AsyncRelayCommand(ApplyPreferredRoleAsync, () => SelectedRelay != null);

        CountryOptions.Add(FilterOption.All(LocalizationService.Get("Relays.AllCountries")));
        RoleOptions.Add(FilterOption.All(LocalizationService.Get("Relays.AllRoles")));
        FlagOptions.Add(FilterOption.All(LocalizationService.Get("Relays.AllFlags")));
        BandwidthOptions.Add(FilterOption.All(LocalizationService.Get("Relays.AnyBandwidth")));

        SelectedCountryOption = CountryOptions[0];
        SelectedRoleOption = RoleOptions[0];
        SelectedFlagOption = FlagOptions[0];
        SelectedBandwidthOption = BandwidthOptions[0];

    }

    public ObservableCollection<RelayDirectoryItem> FilteredRelays { get; } = [];
    public ObservableCollection<FilterOption> CountryOptions { get; } = [];
    public ObservableCollection<FilterOption> RoleOptions { get; } = [];
    public ObservableCollection<FilterOption> FlagOptions { get; } = [];
    public ObservableCollection<FilterOption> BandwidthOptions { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<RelayDirectoryItem> ToggleFavoriteCommand { get; }
    public IAsyncRelayCommand UseAsPreferredExitCommand { get; }
    public IAsyncRelayCommand UseAsPreferredGuardCommand { get; }
    public IAsyncRelayCommand UseAsPreferredMiddleCommand { get; }
    public IAsyncRelayCommand UseAsPreferredRoleCommand { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FilterOption? _selectedCountryOption;
    [ObservableProperty] private FilterOption? _selectedRoleOption;
    [ObservableProperty] private FilterOption? _selectedFlagOption;
    [ObservableProperty] private FilterOption? _selectedBandwidthOption;
    [ObservableProperty] private RelayDirectoryItem? _selectedRelay;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _relayPreferenceFeedback = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool HasSelectedRelay => SelectedRelay != null;
    public bool ShowSelectionEmptyState => SelectedRelay == null;
    public bool HasRelays => FilteredRelays.Count > 0;
    public bool ShowNoResults => !IsLoading && string.IsNullOrWhiteSpace(ErrorText) && FilteredRelays.Count == 0;
    public bool HasRelayPreferenceFeedback => !string.IsNullOrWhiteSpace(RelayPreferenceFeedback);
    public string ManualSelectionWarning => LocalizationService.Get("Relays.Warning");
    public string SelectedFingerprintShort => SelectedRelay?.FingerprintShort ?? "--";
    public string SelectedFlagsText => SelectedRelay == null ? "--" : string.Join("  ", SelectedRelay.Flags);
    public IReadOnlyList<string> SelectedFlags => SelectedRelay?.Flags ?? Array.Empty<string>();
    public string SelectedAddressesText => SelectedRelay == null ? "--" : string.Join(Environment.NewLine, SelectedRelay.Addresses);
    public string PreferredExitActionText => SelectedRelay?.IsExit == true
        ? LocalizationService.Get("Relays.UseAsPreferredExit")
        : LocalizationService.Get("Relays.SelectExitRelayFirst");
    public string PreferredGuardActionText => SelectedRelay?.IsGuard == true
        ? LocalizationService.Get("Relays.UseAsPreferredGuard")
        : LocalizationService.Get("Relays.SelectGuardRelayFirst");
    public string PreferredMiddleActionText => SelectedRelay?.IsMiddle == true
        ? LocalizationService.Get("Relays.UseAsPreferredMiddle")
        : LocalizationService.Get("Relays.SelectMiddleRelayFirst");

    // Single action: pin the selected relay as whatever role it actually is (exit/guard/middle),
    // so the user gets one relevant button instead of three (two of which are always disabled).
    public string PreferredRoleActionText => SelectedRelay?.RoleKey switch
    {
        "exit" => LocalizationService.Get("Relays.UseAsPreferredExit"),
        "guard" => LocalizationService.Get("Relays.UseAsPreferredGuard"),
        "middle" => LocalizationService.Get("Relays.UseAsPreferredMiddle"),
        _ => LocalizationService.Get("Relays.UseAsPreferredGuard")
    };

    public override void OnActivated()
    {
        if (_hasLoadedRelays)
        {
            return;
        }

        _hasLoadedRelays = true;
        _ = RefreshCommand.ExecuteAsync(null);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedCountryOptionChanged(FilterOption? value) => ApplyFilters();
    partial void OnSelectedRoleOptionChanged(FilterOption? value) => ApplyFilters();
    partial void OnSelectedFlagOptionChanged(FilterOption? value) => ApplyFilters();
    partial void OnSelectedBandwidthOptionChanged(FilterOption? value) => ApplyFilters();
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowNoResults));
    partial void OnErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    partial void OnSelectedRelayChanged(RelayDirectoryItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedRelay));
        OnPropertyChanged(nameof(ShowSelectionEmptyState));
        OnPropertyChanged(nameof(SelectedFingerprintShort));
        OnPropertyChanged(nameof(SelectedFlagsText));
        OnPropertyChanged(nameof(SelectedFlags));
        OnPropertyChanged(nameof(SelectedAddressesText));
        OnPropertyChanged(nameof(PreferredExitActionText));
        OnPropertyChanged(nameof(PreferredGuardActionText));
        OnPropertyChanged(nameof(PreferredMiddleActionText));
        OnPropertyChanged(nameof(PreferredRoleActionText));
        RelayPreferenceFeedback = string.Empty;
        UseAsPreferredExitCommand.NotifyCanExecuteChanged();
        UseAsPreferredGuardCommand.NotifyCanExecuteChanged();
        UseAsPreferredMiddleCommand.NotifyCanExecuteChanged();
        UseAsPreferredRoleCommand.NotifyCanExecuteChanged();
    }

    partial void OnRelayPreferenceFeedbackChanged(string value) => OnPropertyChanged(nameof(HasRelayPreferenceFeedback));

    private async Task RefreshAsync()
    {
        _hasLoadedRelays = true;
        IsLoading = true;
        ErrorText = string.Empty;

        try
        {
            var relays = await _relayDirectoryService.GetRelaysAsync(State.AppendLog, CancellationToken.None);
            _allRelays.Clear();
            _allRelays.AddRange(relays.Select(info => new RelayDirectoryItem(info, ToggleFavoriteCommand)));
            RebuildFilterOptions();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            OnPropertyChanged(nameof(HasError));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildFilterOptions()
    {
        SelectedCountryOption = RebuildOptions(
            CountryOptions,
            _allRelays
                .GroupBy(relay => relay.CountryCode)
                .OrderBy(group => group.First().CountryName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FilterOption(group.Key, $"{group.First().CountryName} ({group.Count()})")),
            LocalizationService.Get("Relays.AllCountries"),
            SelectedCountryOption);

        SelectedRoleOption = RebuildOptions(
            RoleOptions,
            new[]
            {
                new FilterOption("guard", LocalizationService.Get("Relays.RoleGuard")),
                new FilterOption("middle", LocalizationService.Get("Relays.RoleMiddle")),
                new FilterOption("exit", LocalizationService.Get("Relays.RoleExit"))
            },
            LocalizationService.Get("Relays.AllRoles"),
            SelectedRoleOption);

        SelectedFlagOption = RebuildOptions(
            FlagOptions,
            _allRelays
                .SelectMany(relay => relay.Flags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(flag => flag, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(flag => new FilterOption(flag, flag)),
            LocalizationService.Get("Relays.AllFlags"),
            SelectedFlagOption);

        SelectedBandwidthOption = RebuildOptions(
            BandwidthOptions,
            new[]
            {
                new FilterOption("1000000", LocalizationService.Get("Relays.Bandwidth1Mb")),
                new FilterOption("10000000", LocalizationService.Get("Relays.Bandwidth10Mb")),
                new FilterOption("50000000", LocalizationService.Get("Relays.Bandwidth50Mb"))
            },
            LocalizationService.Get("Relays.AnyBandwidth"),
            SelectedBandwidthOption);
    }

    private static FilterOption? RebuildOptions(
        ObservableCollection<FilterOption> target,
        IEnumerable<FilterOption> items,
        string allLabel,
        FilterOption? selectedOption)
    {
        var selectedValue = selectedOption?.Value ?? string.Empty;
        target.Clear();
        target.Add(FilterOption.All(allLabel));
        foreach (var item in items)
        {
            target.Add(item);
        }

        return target.FirstOrDefault(option => string.Equals(option.Value, selectedValue, StringComparison.Ordinal))
               ?? target.FirstOrDefault();
    }

    private void ApplyFilters()
    {
        var search = SearchText?.Trim() ?? string.Empty;
        var filtered = _allRelays
            .Where(relay => string.IsNullOrWhiteSpace(search) || relay.SearchIndex.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(relay => SelectedCountryOption == null || SelectedCountryOption.IsAll || relay.CountryCode == SelectedCountryOption.Value)
            .Where(relay => SelectedRoleOption == null || SelectedRoleOption.IsAll || relay.RoleKey == SelectedRoleOption.Value)
            .Where(relay => SelectedFlagOption == null || SelectedFlagOption.IsAll || relay.Flags.Contains(SelectedFlagOption.Value, StringComparer.OrdinalIgnoreCase))
            .Where(relay => SelectedBandwidthOption == null || SelectedBandwidthOption.IsAll || relay.AdvertisedBandwidth >= long.Parse(SelectedBandwidthOption.Value, CultureInfo.InvariantCulture))
            .ToList();

        FilteredRelays.Clear();
        foreach (var relay in filtered)
        {
            FilteredRelays.Add(relay);
        }

        if (SelectedRelay == null || !FilteredRelays.Contains(SelectedRelay))
        {
            SelectedRelay = FilteredRelays.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasRelays));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(HasError));
    }

    private void ToggleFavorite(RelayDirectoryItem? relay)
    {
        if (relay != null)
        {
            relay.IsFavorite = !relay.IsFavorite;
        }
    }

    private bool CanApplyPreferredExit()
    {
        return SelectedRelay?.IsExit == true;
    }

    private bool CanApplyPreferredGuard()
    {
        return SelectedRelay?.IsGuard == true;
    }

    private bool CanApplyPreferredMiddle()
    {
        return SelectedRelay?.IsMiddle == true;
    }

    private async Task ApplyPreferredExitAsync()
    {
        if (SelectedRelay == null)
        {
            return;
        }

        await State
            .ApplyPreferredExitFingerprintAsync(SelectedRelay.Fingerprint, SelectedRelay.Nickname, CancellationToken.None)
            .ConfigureAwait(true);
        RelayPreferenceFeedback = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Get("Relays.PreferenceSaved"),
            LocalizationService.Get("Relays.RoleExit"),
            SelectedRelay.Nickname);
    }

    private async Task ApplyPreferredGuardAsync()
    {
        if (SelectedRelay == null)
        {
            return;
        }

        await State
            .ApplyPreferredGuardFingerprintAsync(SelectedRelay.Fingerprint, SelectedRelay.Nickname, CancellationToken.None)
            .ConfigureAwait(true);
        RelayPreferenceFeedback = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Get("Relays.PreferenceSaved"),
            LocalizationService.Get("Relays.RoleGuard"),
            SelectedRelay.Nickname);
    }

    private async Task ApplyPreferredMiddleAsync()
    {
        if (SelectedRelay == null)
        {
            return;
        }

        await State
            .ApplyPreferredMiddleFingerprintAsync(SelectedRelay.Fingerprint, SelectedRelay.Nickname, CancellationToken.None)
            .ConfigureAwait(true);
        RelayPreferenceFeedback = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Get("Relays.PreferenceSaved"),
            LocalizationService.Get("Relays.RoleMiddle"),
            SelectedRelay.Nickname);
    }

    // Pin the selected relay according to its actual role.
    private Task ApplyPreferredRoleAsync()
    {
        return SelectedRelay?.RoleKey switch
        {
            "exit" => ApplyPreferredExitAsync(),
            "guard" => ApplyPreferredGuardAsync(),
            "middle" => ApplyPreferredMiddleAsync(),
            _ => Task.CompletedTask
        };
    }
}

public sealed partial class RelayDirectoryItem : ObservableObject
{
    public RelayDirectoryItem(TorRelayInfo info, IRelayCommand<RelayDirectoryItem> toggleFavoriteCommand)
    {
        ToggleFavoriteCommand = toggleFavoriteCommand;
        Nickname = string.IsNullOrWhiteSpace(info.Nickname) ? info.Fingerprint[..12] : info.Nickname;
        Fingerprint = info.Fingerprint;
        CountryCode = string.IsNullOrWhiteSpace(info.CountryCode) ? "??" : info.CountryCode;
        CountryDisplay = CountryCode.Length == 2 ? CountryCode.ToUpperInvariant() : "--";
        CountryName = info.CountryName;
        Addresses = info.Addresses;
        Flags = info.Flags;
        AdvertisedBandwidth = info.AdvertisedBandwidth;
        AddedText = info.FirstSeenUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "--";
        LastSeenText = info.LastSeenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "--";
        UptimeText = info.LastRestartedUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - info.LastRestartedUtc.Value)
            : "--";

        var primaryAddress = info.Addresses.FirstOrDefault() ?? "--";
        PrimaryAddress = ExtractHost(primaryAddress);
        OrPort = ExtractPort(primaryAddress);

        RoleKey = info.Flags.Contains("Exit", StringComparer.OrdinalIgnoreCase)
            ? "exit"
            : info.Flags.Contains("Guard", StringComparer.OrdinalIgnoreCase)
                ? "guard"
                : "middle";

        RoleLabel = RoleKey switch
        {
            "exit" => LocalizationService.Get("Relays.RoleExit"),
            "guard" => LocalizationService.Get("Relays.RoleGuard"),
            _ => LocalizationService.Get("Relays.RoleMiddle")
        };

        RoleTone = RoleKey switch
        {
            "exit" => "accent",
            "guard" => "success",
            _ => "info"
        };

        BandwidthText = FormatBandwidth(info.AdvertisedBandwidth);
        FingerprintShort = $"{info.Fingerprint[..6]}...{info.Fingerprint[^6..]}";
        SearchIndex = string.Join(" ", new[]
        {
            Nickname,
            Fingerprint,
            CountryName,
            CountryCode,
            PrimaryAddress
        }.Concat(Addresses));
    }

    public IRelayCommand<RelayDirectoryItem> ToggleFavoriteCommand { get; }
    public string Nickname { get; }
    public string Fingerprint { get; }
    public string FingerprintShort { get; }
    public string CountryCode { get; }
    public string CountryDisplay { get; }
    public string CountryName { get; }
    public IReadOnlyList<string> Addresses { get; }
    public IReadOnlyList<string> Flags { get; }
    public long AdvertisedBandwidth { get; }
    public string PrimaryAddress { get; }
    public string OrPort { get; }
    public string RoleKey { get; }
    public string RoleLabel { get; }
    public string RoleTone { get; }
    public string BandwidthText { get; }
    public string UptimeText { get; }
    public string AddedText { get; }
    public string LastSeenText { get; }
    public string SearchIndex { get; }
    public bool IsGuard => string.Equals(RoleKey, "guard", StringComparison.Ordinal);
    public bool IsMiddle => string.Equals(RoleKey, "middle", StringComparison.Ordinal);
    public bool IsExit => string.Equals(RoleKey, "exit", StringComparison.Ordinal);

    [ObservableProperty] private bool _isFavorite;

    public MaterialIconKind FavoriteIcon => IsFavorite
        ? MaterialIconKind.Star
        : MaterialIconKind.StarOutline;

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteIcon));
    }

    private static string ExtractHost(string value)
    {
        if (value.StartsWith("[", StringComparison.Ordinal) && value.Contains("]:", StringComparison.Ordinal))
        {
            var end = value.IndexOf("]:", StringComparison.Ordinal);
            return value[1..end];
        }

        var separator = value.LastIndexOf(':');
        return separator > 0 ? value[..separator] : value;
    }

    private static string ExtractPort(string value)
    {
        if (value.StartsWith("[", StringComparison.Ordinal) && value.Contains("]:", StringComparison.Ordinal))
        {
            var end = value.IndexOf("]:", StringComparison.Ordinal);
            return value[(end + 2)..];
        }

        var separator = value.LastIndexOf(':');
        return separator > 0 ? value[(separator + 1)..] : "--";
    }

    private static string FormatBandwidth(long bytesPerSecond)
    {
        const double mega = 1024d * 1024d;
        const double giga = mega * 1024d;

        if (bytesPerSecond >= giga)
        {
            return $"{bytesPerSecond / giga:0.00} GB/s";
        }

        if (bytesPerSecond >= mega)
        {
            return $"{bytesPerSecond / mega:0.0} MB/s";
        }

        return $"{bytesPerSecond / 1024d:0.0} KB/s";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(0, span.Minutes)}m";
    }
}

public sealed record FilterOption(string Value, string Label)
{
    public bool IsAll => string.IsNullOrEmpty(Value);

    public static FilterOption All(string label) => new(string.Empty, label);

    public override string ToString() => Label;
}
