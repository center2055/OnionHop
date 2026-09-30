using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using OnionHopV3.App.Services;
using OnionHopV3.Core;

namespace OnionHopV3.App.ViewModels;

/// <summary>
/// Home page (#73). Built around one rule: the status card describes the state the app is actually
/// in, derived from live values, never an echo of whatever message happened to be set last. The old
/// headline showed the client's connect-time message, so toggling the system proxy after connecting
/// left "System Proxy: ON" on the button beside "System proxy is off" in the headline, and any
/// passing event ("Tor bridge data updated.") could replace the status outright.
/// </summary>
public sealed class HomePageViewModel : PageViewModelBase
{
    private const int MaxLatestMessageLength = 140;

    private static readonly Regex ConnectedWord = new(@"\bconnected\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // State changes that can alter anything the status card, the connection card or the session
    // figures show. One set, so nothing is forgotten when a new input is added.
    private static readonly HashSet<string> StatusInputs = new(StringComparer.Ordinal)
    {
        nameof(AppStateViewModel.IsConnected),
        nameof(AppStateViewModel.IsConnecting),
        nameof(AppStateViewModel.IsDisconnecting),
        nameof(AppStateViewModel.IsPreparingConnection),
        nameof(AppStateViewModel.IsBusy),
        nameof(AppStateViewModel.StatusMessage),
        nameof(AppStateViewModel.TunnelCheck),
        nameof(AppStateViewModel.KillSwitchHolding),
        nameof(AppStateViewModel.SelectedConnectionMode),
        nameof(AppStateViewModel.UseHybridRouting),
        nameof(AppStateViewModel.SystemProxyEnabled),
        nameof(AppStateViewModel.ProxyScopeMode),
        nameof(AppStateViewModel.SocksProxyPort),
        nameof(AppStateViewModel.DownloadSpeed),
        nameof(AppStateViewModel.UploadSpeed),
        nameof(AppStateViewModel.SelectedLocationOption),
        nameof(AppStateViewModel.SelectedLocation),
        nameof(AppStateViewModel.ExitNodeFingerprint),
        nameof(AppStateViewModel.CurrentIp)
    };

    private static readonly string[] DerivedProperties =
    [
        nameof(HeroTitle),
        nameof(HeroDetail),
        nameof(HeroTone),
        nameof(HeroIcon),
        nameof(ShowTunnelWarning),
        nameof(ShowTunnelVerified),
        nameof(ShowKillSwitchHold),
        nameof(ModeHint),
        nameof(CanChangeMode),
        nameof(SystemProxyHint),
        nameof(DownloadRateText),
        nameof(UploadRateText),
        nameof(SelectedExitLabel),
        nameof(CanChangeIdentity),
        nameof(HasRealIp)
    ];

    private readonly Action _openSettings;
    private readonly Action _openLogs;
    private HomeActivityItem? _latestEvent;

    public HomePageViewModel(AppStateViewModel state, Action openSettings, Action? openLogs = null)
        : base("Nav.Home", MaterialIconKind.HomeOutline, state, 0xE80F)
    {
        _openSettings = openSettings;
        _openLogs = openLogs ?? (() => { });
        OpenSettingsCommand = new RelayCommand(() => _openSettings());
        OpenLogsCommand = new RelayCommand(() => _openLogs());

        State.LogLines.CollectionChanged += OnLogsCollectionChanged;
        State.PropertyChanged += OnStatePropertyChanged;
        RefreshLatestEvent();
    }

    public IRelayCommand OpenSettingsCommand { get; }

    public IRelayCommand OpenLogsCommand { get; }

    // ----- Status card -------------------------------------------------------------------------

    /// <summary>
    /// What the status card is showing. Title, detail, tone and icon all derive from this one value,
    /// so they cannot disagree with each other.
    /// </summary>
    internal enum HeroState
    {
        Disconnected,
        Connecting,
        Connected,
        /// <summary>Connected, but the tunnel check saw traffic leave with the real IP (#83).</summary>
        NotProtected,
        Disconnecting,
        /// <summary>The kill switch fired and holds all traffic back until the user lifts it.</summary>
        KillSwitch
    }

    internal static HeroState DeriveHeroState(bool isConnected, bool isStarting, bool isDisconnecting, bool leaking, bool killSwitchHolding) =>
        isDisconnecting ? HeroState.Disconnecting
        : isStarting ? HeroState.Connecting
        : isConnected ? (leaking ? HeroState.NotProtected : HeroState.Connected)
        : killSwitchHolding ? HeroState.KillSwitch
        : HeroState.Disconnected;

    private HeroState Hero => DeriveHeroState(
        State.IsConnected,
        State.IsConnecting || State.IsPreparingConnection,
        State.IsDisconnecting,
        State.TunnelCheck == OnionHopClient.TunnelCheckState.Leaking,
        State.KillSwitchHolding);

    /// <summary>One word or two: the answer to "am I protected right now".</summary>
    public string HeroTitle => L(HeroTitleKey(Hero));

    internal static string HeroTitleKey(HeroState state) => state switch
    {
        HeroState.Disconnecting => "Home.StatusDisconnecting",
        HeroState.Connecting => "Home.StatusConnecting",
        HeroState.NotProtected => "Home.StatusNotProtected",
        HeroState.Connected => "Home.StatusConnected",
        HeroState.KillSwitch => "Home.StatusKillSwitch",
        _ => "Home.StatusDisconnected"
    };

    /// <summary>
    /// One plain sentence on what is actually going through Tor. While connecting it is the live
    /// bootstrap line, so progress is visible without opening the Logs page (#73).
    /// </summary>
    public string HeroDetail
    {
        get
        {
            var hero = Hero;
            if (hero == HeroState.Connecting && !string.IsNullOrWhiteSpace(State.StatusMessage))
            {
                return State.StatusMessage;
            }

            return L(HeroDetailKey(hero, State.IsTunMode, State.UseHybridRouting, State.IsSystemProxyScope, State.SystemProxyEnabled));
        }
    }

    /// <summary>
    /// The system proxy line is read live, not from the connect-time message: that is the line that
    /// used to contradict the System Proxy button after a mid-session toggle.
    /// </summary>
    internal static string HeroDetailKey(HeroState state, bool tunMode, bool hybridRouting, bool systemProxyScope, bool systemProxyOn) => state switch
    {
        HeroState.Disconnecting => "Home.DetailDisconnecting",
        HeroState.Connecting => "Home.DetailConnecting",
        HeroState.Disconnected => "Home.DetailDisconnected",
        HeroState.KillSwitch => "Home.DetailKillSwitch",
        HeroState.NotProtected => "Home.DetailLeaking",
        _ => tunMode ? (hybridRouting ? "Home.DetailTunHybrid" : "Home.DetailTunFull")
            : !systemProxyScope ? "Home.DetailLocalOnly"
            : systemProxyOn ? "Home.DetailProxyOn"
            : "Home.DetailProxyOff"
    };

    public string HeroTone => HeroToneFor(Hero);

    internal static string HeroToneFor(HeroState state) => state switch
    {
        HeroState.NotProtected => "danger",
        HeroState.Connected => "success",
        HeroState.Connecting => "info",
        HeroState.Disconnecting or HeroState.KillSwitch => "warning",
        _ => "neutral"
    };

    public MaterialIconKind HeroIcon => Hero switch
    {
        HeroState.NotProtected => MaterialIconKind.ShieldAlert,
        HeroState.Connected => MaterialIconKind.ShieldCheck,
        HeroState.Connecting or HeroState.Disconnecting => MaterialIconKind.ShieldSync,
        HeroState.KillSwitch => MaterialIconKind.ShieldLock,
        _ => MaterialIconKind.ShieldOffOutline
    };

    /// <summary>The tunnel check found a fresh connection leaving with the real IP (#83).</summary>
    public bool ShowTunnelWarning => Hero == HeroState.NotProtected;

    /// <summary>Offers "Restore internet" while the kill switch holds.</summary>
    public bool ShowKillSwitchHold => Hero == HeroState.KillSwitch;

    public bool ShowTunnelVerified =>
        Hero == HeroState.Connected && State.TunnelCheck == OnionHopClient.TunnelCheckState.Verified;

    public string SelectedExitLabel => State.IsManualExitNodeFingerprintSet
        ? State.ManualExitFingerprintSummary
        : State.SelectedLocationOption?.Label ?? L("Home.Automatic");

    public bool CanChangeIdentity => State.IsConnected && !State.IsBusy;

    /// <summary>False while the IP is a "--.--.--.--" placeholder, so there is nothing to copy.</summary>
    public bool HasRealIp => System.Net.IPAddress.TryParse(State.CurrentIp?.Trim(), out _);

    // ----- Latest activity ---------------------------------------------------------------------

    /// <summary>The most recent log line worth a glance, so the Logs page is not the only window
    /// into what the app is doing (#73). Routine IP polling is skipped: it would win every time.</summary>
    public HomeActivityItem? LatestEvent
    {
        get => _latestEvent;
        private set
        {
            if (Equals(_latestEvent, value))
            {
                return;
            }

            _latestEvent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLatestEvent));
        }
    }

    public bool HasLatestEvent => LatestEvent != null;

    // ----- Connection card ---------------------------------------------------------------------

    /// <summary>
    /// What the selected mode means, right where it is chosen. "Proxy Mode only covers some apps"
    /// is the single most common misunderstanding in the reports, and the old Home never said it.
    /// </summary>
    public string ModeHint => !CanChangeMode ? L("Home.ModeLockedHint")
        : State.IsTunMode ? (State.UseHybridRouting ? L("Home.ModeHintHybrid") : L("Home.ModeHintTun"))
        : L("Home.ModeHintProxy");

    /// <summary>The mode only takes effect on the next connect, so it is locked while a session is up
    /// rather than silently doing nothing.</summary>
    public bool CanChangeMode => !State.IsConnected && !State.IsBusy;

    public string SystemProxyHint => string.Format(L("Home.SystemProxyHint"), $"127.0.0.1:{State.SocksProxyPort}");

    // ----- Session figures ---------------------------------------------------------------------

    public string DownloadRateText => $"↓ {State.DownloadSpeed}";

    public string UploadRateText => $"↑ {State.UploadSpeed}";

    // -------------------------------------------------------------------------------------------

    private static string L(string key) => LocalizationService.Get(key);

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshLatestEvent();

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null || !StatusInputs.Contains(e.PropertyName))
        {
            return;
        }

        foreach (var property in DerivedProperties)
        {
            OnPropertyChanged(property);
        }
    }

    private void RefreshLatestEvent()
    {
        for (var i = State.LogLines.Count - 1; i >= 0; i--)
        {
            var line = State.LogLines[i];
            if (IsRoutineLine(line))
            {
                continue;
            }

            LatestEvent = ParseEvent(line);
            return;
        }

        LatestEvent = null;
    }

    /// <summary>Lines that are either constant background polling or too long and technical to mean
    /// anything at a glance. They stay in the Logs page, just not on Home. That includes the
    /// background relay-list and IP lookups: they have nothing to do with connecting, yet offline
    /// they failed in red as Home's headline event (the AppImage catalog's screenshot shows one).</summary>
    internal static bool IsRoutineLine(string line)
    {
        var message = line.Length > 9 ? line[9..] : line;
        return message.StartsWith("IP check", StringComparison.OrdinalIgnoreCase)
               || message.StartsWith("Auto IP refresh", StringComparison.OrdinalIgnoreCase)
               || message.StartsWith("Tor arguments:", StringComparison.OrdinalIgnoreCase)
               || message.StartsWith("Paths:", StringComparison.OrdinalIgnoreCase)
               || message.StartsWith("Tor node DB", StringComparison.OrdinalIgnoreCase)
               || message.StartsWith("Country DB update", StringComparison.OrdinalIgnoreCase)
               || message.StartsWith("Direct IP lookup", StringComparison.OrdinalIgnoreCase)
               || message.StartsWith("Startup IP lookup", StringComparison.OrdinalIgnoreCase);
    }

    internal static HomeActivityItem ParseEvent(string line)
    {
        var timestamp = line.Length >= 8 ? line[..8] : "--:--:--";
        var message = (line.Length > 9 ? line[9..] : line).Trim();
        if (message.Length > MaxLatestMessageLength)
        {
            message = $"{message[..(MaxLatestMessageLength - 3)]}...";
        }

        var tone = message.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("error", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            ? "danger"
            : message.Contains("warn", StringComparison.OrdinalIgnoreCase)
              || message.StartsWith("Note:", StringComparison.OrdinalIgnoreCase)
              || message.StartsWith("Kill switch engaged", StringComparison.OrdinalIgnoreCase)
                ? "warning"
                // Whole word: "Disconnected" contains "connected" and used to get the green dot.
                : ConnectedWord.IsMatch(message)
                  || message.Contains("passed", StringComparison.OrdinalIgnoreCase)
                    ? "success"
                    : "neutral";

        return new HomeActivityItem(timestamp, message, tone);
    }
}

public sealed record HomeActivityItem(string Time, string Message, string Tone);
