using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OnionHopV3.App.Services;
using OnionHopV3.Core;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.ViewModels;

public sealed partial class AppStateViewModel
{
    public const string AccentViolet = "violet";
    public const string AccentIndigo = "indigo";
    public const string AccentCyan = "cyan";
    public const string AccentRose = "rose";

    public const string TorEngineAutomatic = "automatic";
    public const string TorEngineClassic = "classic";
    public const string TorEngineArti = "arti";
    public const string TorEngineArtiHop = "artihop";

    public const string RelayRefreshSixHours = "6h";
    public const string RelayRefreshTwelveHours = "12h";
    public const string RelayRefreshTwentyFourHours = "24h";

    public const string UpdateChannelStable = "stable";
    public const string UpdateChannelPreview = "preview";

    private readonly UpdateService _updateService = new();

    public ObservableCollection<LocalizedOption> AccentColorOptions { get; } = [];
    public ObservableCollection<LocalizedOption> TorEngineOptions { get; } = [];
    public ObservableCollection<LocalizedOption> RelayRefreshIntervalOptions { get; } = [];
    public ObservableCollection<LocalizedOption> UpdateChannelOptions { get; } = [];

    [ObservableProperty] private string _selectedAccentColor = AccentViolet;
    [ObservableProperty] private LocalizedOption? _selectedAccentColorOption;
    [ObservableProperty] private string _selectedTorEngineMode = TorEngineAutomatic;
    [ObservableProperty] private LocalizedOption? _selectedTorEngineOption;
    [ObservableProperty] private string _selectedRelayRefreshInterval = RelayRefreshTwelveHours;
    [ObservableProperty] private LocalizedOption? _selectedRelayRefreshIntervalOption;
    [ObservableProperty] private string _selectedUpdateChannel = UpdateChannelStable;
    [ObservableProperty] private LocalizedOption? _selectedUpdateChannelOption;
    [ObservableProperty] private bool _clearSessionDataOnDisconnect;
    [ObservableProperty] private bool _dnsLeakProtectionEnabled = true;
    [ObservableProperty] private bool _clipboardProtectionEnabled;
    [ObservableProperty] private bool _persistentAdminHelperEnabled;
    [ObservableProperty] private bool _blockUdpTraffic = true;
    [ObservableProperty] private string _updateStatusText = "Checking updates";
    [ObservableProperty] private string _updateStatusTone = "neutral";
    [ObservableProperty] private string _latestAvailableVersion = string.Empty;
    [ObservableProperty] private long _sessionBytesReceived;
    [ObservableProperty] private long _sessionBytesSent;
    [ObservableProperty] private int _sessionCircuitCount;
    [ObservableProperty] private int _sessionIdentityChanges;

    partial void OnPersistentAdminHelperEnabledChanged(bool value)
    {
        // Keep the runtime gate in sync immediately so the next connect sees the right value.
        _client.SetPersistentAdminHelperOptIn(value);

        if (_loadingSettings)
        {
            return;
        }

        // Turning the helper off should start removing any existing at-logon task right away, not only
        // on the next connect. This is a best-effort non-elevated attempt; the elevated cleanup still
        // runs on the next connect. Swallow failures (e.g. no rights / task absent).
        if (!value && OperatingSystem.IsWindows())
        {
            try
            {
                _client.TryRemovePersistentAdminHelper();
            }
            catch
            {
            }
        }
    }

    public bool BlockLanAccess
    {
        get => !AllowLanProxyAccess;
        set
        {
            if (AllowLanProxyAccess == !value)
            {
                return;
            }

            AllowLanProxyAccess = !value;
            OnPropertyChanged();
        }
    }

    public string TotalTransferredText => FormatDataSize(SessionBytesReceived + SessionBytesSent);
    public string SessionCircuitCountText => SessionCircuitCount.ToString(CultureInfo.CurrentCulture);
    public string SessionIdentityChangesText => SessionIdentityChanges.ToString(CultureInfo.CurrentCulture);
    public string SessionUptimeText => ShowConnectionElapsed && !string.IsNullOrWhiteSpace(ConnectionElapsed)
        ? ConnectionElapsed
        : LocalizationService.Get("Common.Idle");
    public string AdvancedConnectionSummary =>
        $"SOCKS {SocksProxyPort} | HTTP {HttpProxyPort} | Guard {ManualEntryFingerprintSummary} | Middle {ManualMiddleFingerprintSummary} | Exit {ManualExitFingerprintSummary}";

    public string ConnectionTone => IsConnected
        ? "success"
        : IsPreparingConnection || IsConnecting
            ? "info"
            : IsDisconnecting
                ? "warning"
                : "neutral";

    public string ProtectionTone => IsConnected ? "success" : "danger";
    public string ProtectionStatusText => IsConnected
        ? IsTunMode
            ? LocalizationService.Get("Status.ProtectionSystem")
            : LocalizationService.Get("Status.ProtectionProxy")
        : LocalizationService.Get("Status.ProtectionOff");
    public string ProtectionStatusDetail => IsConnected
        ? StatusMessage
        : LocalizationService.Get("Status.ProtectionOffDetail");

    public string StatusBarEngineText => SelectedTorEngineMode switch
    {
        TorEngineArti => LocalizationService.Get("Settings.TorEngineArti"),
        TorEngineArtiHop => LocalizationService.Get("Settings.TorEngineArtiHop"),
        TorEngineClassic => LocalizationService.Get("Settings.TorEngineClassic"),
        _ => LocalizationService.Get("Settings.TorEngineAutomatic")
    };

    public string StatusBarEngineDetailText => SelectedTorEngineMode switch
    {
        TorEngineArti => LocalizationService.Get("Settings.TorEnginePreviewHint"),
        TorEngineArtiHop => LocalizationService.Get("Settings.TorEngineArtiHopHint"),
        TorEngineClassic => LocalizationService.Get("Status.EngineClassicDetail"),
        _ => LocalizationService.Get("Status.EngineAutomaticDetail")
    };

    public string StatusBarVersionText => $"v{FormatVersion(GetCurrentAppVersion())}";
    public string TorEngineSelectionHint => SelectedTorEngineMode switch
    {
        TorEngineArti => LocalizationService.Get("Settings.TorEnginePreviewHint"),
        TorEngineArtiHop => LocalizationService.Get("Settings.TorEngineArtiHopHint"),
        _ => LocalizationService.Get("Settings.TorEngineHint")
    };

    partial void OnSelectedAccentColorChanged(string value)
    {
        var normalized = NormalizeAccentColor(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            SelectedAccentColor = normalized;
            return;
        }

        SelectedAccentColorOption = AccentColorOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
    }

    partial void OnSelectedAccentColorOptionChanged(LocalizedOption? value)
    {
        if (value != null && !string.Equals(SelectedAccentColor, value.Value, StringComparison.Ordinal))
        {
            SelectedAccentColor = value.Value;
        }
    }

    partial void OnSelectedTorEngineModeChanged(string value)
    {
        var normalized = NormalizeTorEngineMode(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            SelectedTorEngineMode = normalized;
            return;
        }

        SelectedTorEngineOption = TorEngineOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
    }

    partial void OnSelectedTorEngineOptionChanged(LocalizedOption? value)
    {
        if (value != null && !string.Equals(SelectedTorEngineMode, value.Value, StringComparison.Ordinal))
        {
            SelectedTorEngineMode = value.Value;
        }
    }

    partial void OnSelectedRelayRefreshIntervalChanged(string value)
    {
        var normalized = NormalizeRelayRefreshInterval(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            SelectedRelayRefreshInterval = normalized;
            return;
        }

        SelectedRelayRefreshIntervalOption = RelayRefreshIntervalOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
        StartBridgeDataRefreshTimer();
    }

    partial void OnSelectedRelayRefreshIntervalOptionChanged(LocalizedOption? value)
    {
        if (value != null && !string.Equals(SelectedRelayRefreshInterval, value.Value, StringComparison.Ordinal))
        {
            SelectedRelayRefreshInterval = value.Value;
        }
    }

    partial void OnSelectedUpdateChannelChanged(string value)
    {
        var normalized = NormalizeUpdateChannel(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            SelectedUpdateChannel = normalized;
            return;
        }

        SelectedUpdateChannelOption = UpdateChannelOptions
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
    }

    partial void OnSelectedUpdateChannelOptionChanged(LocalizedOption? value)
    {
        if (value != null && !string.Equals(SelectedUpdateChannel, value.Value, StringComparison.Ordinal))
        {
            SelectedUpdateChannel = value.Value;
        }
    }

    partial void OnSessionBytesReceivedChanged(long value) => OnPropertyChanged(nameof(TotalTransferredText));
    partial void OnSessionBytesSentChanged(long value) => OnPropertyChanged(nameof(TotalTransferredText));
    partial void OnSessionCircuitCountChanged(int value) => OnPropertyChanged(nameof(SessionCircuitCountText));
    partial void OnSessionIdentityChangesChanged(int value) => OnPropertyChanged(nameof(SessionIdentityChangesText));
    partial void OnConnectionElapsedChanged(string value) => OnPropertyChanged(nameof(SessionUptimeText));
    partial void OnAllowLanProxyAccessChanged(bool value) => OnPropertyChanged(nameof(BlockLanAccess));
    partial void OnSocksProxyPortChanged(string value) => OnPropertyChanged(nameof(AdvancedConnectionSummary));
    partial void OnHttpProxyPortChanged(string value) => OnPropertyChanged(nameof(AdvancedConnectionSummary));
    partial void OnShowAdvancedHomeConnectionDetailsChanged(bool value) => OnPropertyChanged(nameof(AdvancedConnectionSummary));

    private void StartV3BackgroundTasks()
    {
        Dispatcher.UIThread.Post(StartBridgeDataRefreshTimer);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            await CheckForUpdatesAsync().ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            await RefreshBridgeDataIfStaleAsync().ConfigureAwait(false);
        });
    }

    private void StartBridgeDataRefreshTimer()
    {
        if (_disposed)
        {
            return;
        }

        _bridgeRefreshTimer?.Stop();
        _bridgeRefreshTimer = new DispatcherTimer
        {
            Interval = GetBridgeRefreshInterval()
        };
        _bridgeRefreshTimer.Tick += (_, _) => _ = Task.Run(RefreshBridgeDataIfStaleAsync);
        _bridgeRefreshTimer.Start();
    }

    private TimeSpan GetBridgeRefreshInterval()
    {
        return SelectedRelayRefreshInterval switch
        {
            RelayRefreshSixHours => TimeSpan.FromHours(6),
            RelayRefreshTwentyFourHours => TimeSpan.FromHours(24),
            _ => TimeSpan.FromHours(12)
        };
    }

    private bool IsBridgeDataStale(DateTimeOffset? lastUpdatedUtc)
    {
        if (!lastUpdatedUtc.HasValue || lastUpdatedUtc.Value == DateTimeOffset.MinValue)
        {
            return true;
        }

        return DateTimeOffset.UtcNow - lastUpdatedUtc.Value >= GetBridgeRefreshInterval();
    }

    private async Task RefreshBridgeDataIfStaleAsync()
    {
        if (_disposed || IsBridgeDataUpdateInProgress || !IsBridgeDataStale(LastBridgeDataUpdateUtc))
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RefreshBridgeDataAsync();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        await completion.Task.ConfigureAwait(false);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!AutoUpdate)
        {
            UpdateStatusText = LocalizationService.Get("Status.UpdateChecksOff");
            UpdateStatusTone = "neutral";
            return;
        }

        try
        {
            UpdateStatusText = LocalizationService.Get("Status.UpdateChecking");
            UpdateStatusTone = "info";

            var latest = SelectedUpdateChannel == UpdateChannelPreview
                ? await _updateService.GetLatestReleaseFromListAsync(UpdatePreviewApiUrl, includePrereleases: true).ConfigureAwait(false)
                : await _updateService.GetLatestReleaseAsync(UpdateApiUrl).ConfigureAwait(false);
            if (latest?.Version == null)
            {
                UpdateStatusText = LocalizationService.Get("Status.UpdateUnknown");
                UpdateStatusTone = "warning";
                return;
            }

            var current = GetCurrentAppVersion();
            if (IsVersionNewer(latest.Version, current))
            {
                LatestAvailableVersion = $"v{FormatVersion(latest.Version)}";
                UpdateStatusText = string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationService.Get("Status.UpdateAvailable"),
                    LatestAvailableVersion);
                UpdateStatusTone = "warning";
                return;
            }

            LatestAvailableVersion = $"v{FormatVersion(current)}";
            UpdateStatusText = LocalizationService.Get("Status.UpdateCurrent");
            UpdateStatusTone = "success";
        }
        catch
        {
            UpdateStatusText = LocalizationService.Get("Status.UpdateUnknown");
            UpdateStatusTone = "warning";
        }
    }

    private void UpdateSessionTrafficTotals(long bytesReceived, long bytesSent)
    {
        SessionBytesReceived = Math.Max(0, bytesReceived);
        SessionBytesSent = Math.Max(0, bytesSent);
    }

    private void ApplyV3ClientStatus(bool previouslyConnected, OnionHopClient.StatusUpdate update)
    {
        if (!previouslyConnected && update.IsConnected)
        {
            SessionCircuitCount++;
        }

        if (previouslyConnected && !update.IsConnected && ClearSessionDataOnDisconnect)
        {
            ResetSessionMetrics();
            LogLines.Clear();
            DnsLogLines.Clear();
            VpnLogLines.Clear();
            // Also purge the on-disk diagnostic log, which embeds paths/usernames, so "clear session
            // data" leaves nothing behind on a shared machine.
            OnionHopV3.Core.Services.StartupLogger.Clear();
        }
    }

    private void RegisterSessionIdentityChange()
    {
        SessionIdentityChanges++;
        if (IsConnected)
        {
            SessionCircuitCount++;
        }
    }

    private void ResetSessionMetrics()
    {
        SessionBytesReceived = 0;
        SessionBytesSent = 0;
        SessionCircuitCount = 0;
        SessionIdentityChanges = 0;
    }

    private void ResetV3Settings()
    {
        SelectedAccentColor = AccentViolet;
        SelectedTorEngineMode = TorEngineAutomatic;
        SelectedRelayRefreshInterval = RelayRefreshTwelveHours;
        SelectedUpdateChannel = UpdateChannelStable;
        ClearSessionDataOnDisconnect = false;
        DnsLeakProtectionEnabled = true;
        ClipboardProtectionEnabled = false;
        PersistentAdminHelperEnabled = false;
        BlockUdpTraffic = true;
        MiddleNodeFingerprint = string.Empty;
        StrictManualMiddleNodeFingerprint = true;
        ResetSessionMetrics();
    }

    private void HandleV3PropertyChange(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(SelectedAccentColor):
                ApplyTheme();
                break;
            case nameof(IsConnected):
            case nameof(IsConnecting):
            case nameof(IsPreparingConnection):
            case nameof(IsDisconnecting):
            case nameof(ConnectionStatus):
            case nameof(StatusMessage):
            case nameof(SelectedConnectionMode):
                OnPropertyChanged(nameof(ConnectionTone));
                OnPropertyChanged(nameof(ProtectionTone));
                OnPropertyChanged(nameof(ProtectionStatusText));
                OnPropertyChanged(nameof(ProtectionStatusDetail));
                break;
            case nameof(SelectedTorEngineMode):
            case nameof(TunCoreMode):
                OnPropertyChanged(nameof(StatusBarEngineText));
                OnPropertyChanged(nameof(StatusBarEngineDetailText));
                OnPropertyChanged(nameof(TorEngineSelectionHint));
                break;
            case nameof(AutoUpdate):
            case nameof(SelectedUpdateChannel):
                _ = Task.Run(CheckForUpdatesAsync);
                break;
        }
    }

    private void LoadV3Settings(UserSettings settings)
    {
        SelectedAccentColor = NormalizeAccentColor(settings.AccentColor);
        SelectedTorEngineMode = NormalizeTorEngineMode(settings.TorEngineMode);
        SelectedRelayRefreshInterval = NormalizeRelayRefreshInterval(settings.RelayRefreshInterval);
        SelectedUpdateChannel = NormalizeUpdateChannel(settings.UpdateChannel);
        ClearSessionDataOnDisconnect = settings.ClearSessionDataOnDisconnect;
        DnsLeakProtectionEnabled = settings.DnsLeakProtectionEnabled ?? true;
        ClipboardProtectionEnabled = settings.ClipboardProtectionEnabled;
        PersistentAdminHelperEnabled = settings.PersistentAdminHelperEnabled;
        SnowflakeProxyAutoStart = settings.SnowflakeProxyAutoStart;
        SnowflakeProxyCapacity = settings.SnowflakeProxyCapacity is >= 0 ? settings.SnowflakeProxyCapacity.Value : 0;

        // Honor the optional "start Snowflake proxy on launch" setting.
        StartSnowflakeProxyIfAutoStart();
    }

    private void SaveV3Settings(UserSettings settings)
    {
        settings.AccentColor = SelectedAccentColor;
        settings.TorEngineMode = SelectedTorEngineMode;
        settings.RelayRefreshInterval = SelectedRelayRefreshInterval;
        settings.UpdateChannel = SelectedUpdateChannel;
        settings.ClearSessionDataOnDisconnect = ClearSessionDataOnDisconnect;
        settings.DnsLeakProtectionEnabled = DnsLeakProtectionEnabled;
        settings.ClipboardProtectionEnabled = ClipboardProtectionEnabled;
        settings.PersistentAdminHelperEnabled = PersistentAdminHelperEnabled;
        settings.SnowflakeProxyAutoStart = SnowflakeProxyAutoStart;
        settings.SnowflakeProxyCapacity = SnowflakeProxyCapacity;
    }

    private void RefreshV3LocalizedOptions()
    {
        RefreshAccentOptions();
        RefreshTorEngineOptions();
        RefreshRelayRefreshOptions();
        RefreshUpdateChannelOptions();
        OnPropertyChanged(nameof(SessionUptimeText));
        OnPropertyChanged(nameof(StatusBarEngineText));
        OnPropertyChanged(nameof(StatusBarEngineDetailText));
        OnPropertyChanged(nameof(TorEngineSelectionHint));
        OnPropertyChanged(nameof(ProtectionStatusText));
        OnPropertyChanged(nameof(ProtectionStatusDetail));
    }

    private void RefreshAccentOptions()
    {
        AccentColorOptions.Clear();
        AccentColorOptions.Add(new LocalizedOption(AccentViolet, LocalizationService.Get("Accent.Violet")));
        AccentColorOptions.Add(new LocalizedOption(AccentIndigo, LocalizationService.Get("Accent.Indigo")));
        AccentColorOptions.Add(new LocalizedOption(AccentCyan, LocalizationService.Get("Accent.Cyan")));
        AccentColorOptions.Add(new LocalizedOption(AccentRose, LocalizationService.Get("Accent.Rose")));
        SelectedAccentColorOption = AccentColorOptions
            .FirstOrDefault(option => string.Equals(option.Value, SelectedAccentColor, StringComparison.Ordinal))
            ?? AccentColorOptions.FirstOrDefault();
    }

    private void RefreshTorEngineOptions()
    {
        TorEngineOptions.Clear();
        TorEngineOptions.Add(new LocalizedOption(TorEngineAutomatic, LocalizationService.Get("Settings.TorEngineAutomatic")));
        TorEngineOptions.Add(new LocalizedOption(TorEngineClassic, LocalizationService.Get("Settings.TorEngineClassic")));
        TorEngineOptions.Add(new LocalizedOption(TorEngineArti, LocalizationService.Get("Settings.TorEngineArti")));
        TorEngineOptions.Add(new LocalizedOption(TorEngineArtiHop, LocalizationService.Get("Settings.TorEngineArtiHop")));
        SelectedTorEngineOption = TorEngineOptions
            .FirstOrDefault(option => string.Equals(option.Value, SelectedTorEngineMode, StringComparison.Ordinal))
            ?? TorEngineOptions.FirstOrDefault();
    }

    private void RefreshRelayRefreshOptions()
    {
        RelayRefreshIntervalOptions.Clear();
        RelayRefreshIntervalOptions.Add(new LocalizedOption(RelayRefreshSixHours, LocalizationService.Get("Settings.RelayRefreshSixHours")));
        RelayRefreshIntervalOptions.Add(new LocalizedOption(RelayRefreshTwelveHours, LocalizationService.Get("Settings.RelayRefreshTwelveHours")));
        RelayRefreshIntervalOptions.Add(new LocalizedOption(RelayRefreshTwentyFourHours, LocalizationService.Get("Settings.RelayRefreshTwentyFourHours")));
        SelectedRelayRefreshIntervalOption = RelayRefreshIntervalOptions
            .FirstOrDefault(option => string.Equals(option.Value, SelectedRelayRefreshInterval, StringComparison.Ordinal))
            ?? RelayRefreshIntervalOptions.FirstOrDefault();
    }

    private void RefreshUpdateChannelOptions()
    {
        UpdateChannelOptions.Clear();
        UpdateChannelOptions.Add(new LocalizedOption(UpdateChannelStable, LocalizationService.Get("Settings.UpdateChannelStable")));
        UpdateChannelOptions.Add(new LocalizedOption(UpdateChannelPreview, LocalizationService.Get("Settings.UpdateChannelPreview")));
        SelectedUpdateChannelOption = UpdateChannelOptions
            .FirstOrDefault(option => string.Equals(option.Value, SelectedUpdateChannel, StringComparison.Ordinal))
            ?? UpdateChannelOptions.FirstOrDefault();
    }

    private void ApplyV3ThemeResources()
    {
        if (Application.Current == null)
        {
            return;
        }

        var (primary, secondary, soft, outline, heroStart, heroEnd) = SelectedAccentColor switch
        {
            AccentIndigo => ("#8A74FF", "#4F7CFF", "#248A74FF", "#4A8A74FF", "#7B5CFF", "#4167F5"),
            AccentCyan => ("#5FD8FF", "#4F8CFF", "#245FD8FF", "#465FD8FF", "#2CA6D8", "#3558F8"),
            AccentRose => ("#FF7BCB", "#7A6BFF", "#24FF7BCB", "#4CFF7BCB", "#E056B0", "#6753F4"),
            _ => ("#A970FF", "#5D7CFF", "#2AA970FF", "#4AA970FF", "#7B4DDB", "#4D63F1")
        };

        Application.Current.Resources["AccentPrimaryBrush"] = new SolidColorBrush(Color.Parse(primary));
        Application.Current.Resources["AccentSecondaryBrush"] = new SolidColorBrush(Color.Parse(secondary));
        Application.Current.Resources["AccentSoftBrush"] = new SolidColorBrush(Color.Parse(soft));
        Application.Current.Resources["AccentOutlineBrush"] = new SolidColorBrush(Color.Parse(outline));

        // Drive FluentAvalonia's accent so the chosen color flows through every native control
        // (accent buttons, NavigationView selection, toggles, etc.), not just OnionHop's tokens.
        var fluentTheme = Application.Current.Styles
            .OfType<FluentAvalonia.Styling.FluentAvaloniaTheme>()
            .FirstOrDefault();
        if (fluentTheme != null)
        {
            fluentTheme.CustomAccentColor = Color.Parse(primary);
        }
        Application.Current.Resources["AccentHeroBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse(heroStart), 0),
                new GradientStop(Color.Parse(heroEnd), 1)
            ]
        };
    }

    private static string NormalizeAccentColor(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            AccentIndigo => AccentIndigo,
            AccentCyan => AccentCyan,
            AccentRose => AccentRose,
            _ => AccentViolet
        };
    }

    private static string NormalizeTorEngineMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            TorEngineClassic => TorEngineClassic,
            TorEngineArti => TorEngineArti,
            TorEngineArtiHop => TorEngineArtiHop,
            _ => TorEngineAutomatic
        };
    }

    private static string NormalizeRelayRefreshInterval(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            RelayRefreshSixHours => RelayRefreshSixHours,
            RelayRefreshTwentyFourHours => RelayRefreshTwentyFourHours,
            _ => RelayRefreshTwelveHours
        };
    }

    private static string NormalizeUpdateChannel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            UpdateChannelPreview => UpdateChannelPreview,
            _ => UpdateChannelStable
        };
    }

    private static string FormatDataSize(long bytes)
    {
        const double kilo = 1024d;
        const double mega = kilo * 1024d;
        const double giga = mega * 1024d;

        if (bytes < kilo)
        {
            return $"{bytes} B";
        }

        if (bytes < mega)
        {
            return $"{bytes / kilo:0.0} KB";
        }

        if (bytes < giga)
        {
            return $"{bytes / mega:0.00} MB";
        }

        return $"{bytes / giga:0.00} GB";
    }
}
