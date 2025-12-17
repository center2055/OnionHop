using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Media;
using System.Windows.Input;
using Microsoft.Win32;

namespace OnionHop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex SingBoxConnectionToRegex = new(@"connection to (?<dest>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] BrowserProcessNames =
    {
        "firefox.exe",
        "chrome.exe",
        "msedge.exe"
    };

    private const int SocksPort = 9050;
    private const string DefaultTorRelativePath = "tor\\tor.exe";
    private const string DefaultSingBoxRelativePath = "vpn\\sing-box.exe";
    private const string DefaultWintunRelativePath = "vpn\\wintun.dll";

    private bool _isConnecting;
    private bool _isConnected;
    private string _selectedLocation = "United States";
    private string _statusMessage = "Ready to route traffic through Tor.";
    private string _connectionStatus = "Disconnected";
    private string _currentIp = "--.--.--.--";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
    private double _connectionProgress;

    private Process? _torProcess;
    private Process? _singBoxProcess;
    private CancellationTokenSource? _connectCts;
    private TaskCompletionSource<bool>? _bootstrapSource;

    private DateTime _lastVpnMessageUtc = DateTime.MinValue;
    private readonly object _singBoxLogLock = new();
    private readonly Queue<string> _singBoxRecentLines = new();

    public ObservableCollection<string> LogLines { get; } = new();

    private readonly object _logLock = new();
    private bool _showLogs;
    private bool _showAbout;
    private bool _showSettings;

    private string? _previousProxy;
    private int? _previousProxyEnabled;
    private bool _systemProxyApplied;

    private bool _loadingSettings;
    private CancellationTokenSource? _settingsSaveCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Locations { get; } = new()
    {
        "United States",
        "United Kingdom",
        "Germany",
        "France",
        "Switzerland",
        "Netherlands",
        "Canada",
        "Singapore"
    };

    public ObservableCollection<string> ConnectionModes { get; } = new()
    {
        "Proxy Mode (Recommended)",
        "TUN/VPN Mode (Admin)"
    };

    public string SelectedLocation
    {
        get => _selectedLocation;
        set => SetField(ref _selectedLocation, value);
    }

    private void OnTorExited(object? sender, EventArgs e)
    {
        if (_isDisconnecting)
        {
            return;
        }

        if (_isConnected && IsTunMode && KillSwitchEnabled && !UseHybridRouting)
        {
            EnableKillSwitchEmergencyBlock();
        }

        AppendLog("Tor exited unexpectedly.");
        Dispatcher.BeginInvoke(new Action(() => _ = DisconnectAsync()));
    }

    public string SelectedConnectionMode
    {
        get => _selectedConnectionMode;
        set
        {
            if (SetField(ref _selectedConnectionMode, value))
            {
                Raise(nameof(IsTunMode));
                Raise(nameof(IsProxyMode));
            }
        }
    }
    private string _selectedConnectionMode = "Proxy Mode (Recommended)";

    public bool IsTunMode => string.Equals(SelectedConnectionMode, "TUN/VPN Mode (Admin)", StringComparison.Ordinal);
    public bool IsProxyMode => !IsTunMode;

    public bool SystemWideMode
    {
        get => IsTunMode;
        set => SelectedConnectionMode = value ? "TUN/VPN Mode (Admin)" : "Proxy Mode (Recommended)";
    }

    public bool UseHybridRouting
    {
        get => _useHybridRouting;
        set => SetField(ref _useHybridRouting, value);
    }
    private bool _useHybridRouting;

    public bool AutoConnect
    {
        get => _autoConnect;
        set => SetField(ref _autoConnect, value);
    }
    private bool _autoConnect;

    public bool KillSwitchEnabled
    {
        get => _killSwitchEnabled;
        set
        {
            if (value)
            {
                if (!IsTunMode || UseHybridRouting)
                {
                    StatusMessage = "Kill switch is available only in TUN/VPN Mode with Hybrid disabled.";
                    SetField(ref _killSwitchEnabled, false);
                    return;
                }

                if (_isConnected && !IsAdministrator())
                {
                    StatusMessage = "Kill switch requires Administrator. Disconnect and reconnect in TUN mode.";
                    SetField(ref _killSwitchEnabled, false);
                    return;
                }
            }

            if (SetField(ref _killSwitchEnabled, value) && !_killSwitchEnabled)
            {
                _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
            }
        }
    }
    private bool _killSwitchEnabled;

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (SetField(ref _isDarkMode, value))
            {
                ApplyTheme(_isDarkMode);
            }
        }
    }
    private bool _isDarkMode;

    public bool ShowLogs
    {
        get => _showLogs;
        set
        {
            if (SetField(ref _showLogs, value))
            {
                if (value)
                {
                    ShowAbout = false;
                    ShowSettings = false;
                }
                Raise(nameof(IsOverlayVisible));
            }
        }
    }

    public bool ShowAbout
    {
        get => _showAbout;
        set
        {
            if (SetField(ref _showAbout, value))
            {
                if (value)
                {
                    ShowLogs = false;
                    ShowSettings = false;
                }
                Raise(nameof(IsOverlayVisible));
            }
        }
    }

    public bool ShowSettings
    {
        get => _showSettings;
        set
        {
            if (SetField(ref _showSettings, value))
            {
                if (value)
                {
                    ShowLogs = false;
                    ShowAbout = false;
                }
                Raise(nameof(IsOverlayVisible));
            }
        }
    }

    public bool IsOverlayVisible => ShowLogs || ShowAbout || ShowSettings;

    public string AboutText =>
        "OnionHop Modes\n" +
        "\n" +
        "Proxy Mode (Recommended)\n" +
        "- Starts Tor (SOCKS5 on 127.0.0.1:9050)\n" +
        "- Sets Windows proxy to use Tor for apps that respect proxy settings\n" +
        "- Does NOT require Administrator\n" +
        "- Most stable, best for everyday browsing\n" +
        "\n" +
        "TUN/VPN Mode (Admin)\n" +
        "- Starts Tor + sing-box + Wintun (virtual adapter)\n" +
        "- Can force routing rules at the OS level\n" +
        "- REQUIRES Administrator\n" +
        "\n" +
        "Hybrid (browser via Tor)\n" +
        "- In TUN mode, only browsers are routed through Tor; everything else goes direct\n" +
        "- Useful when you want Tor browsing without breaking other apps\n" +
        "\n" +
        "Settings\n" +
        "- Auto-Connect, Dark Mode, and Kill Switch are in the Settings tab.\n" +
        "\n" +
        "Exit Location\n" +
        "- A hint for which country Tor should try to exit from. Not guaranteed.\n" +
        "\n" +
        "Auto-Connect\n" +
        "- Connect automatically when OnionHop starts.\n" +
        "\n" +
        "Kill Switch\n" +
        "- Available only in TUN/VPN Mode with Hybrid disabled (strict).\n" +
        "- If the tunnel drops unexpectedly, OnionHop blocks outbound traffic via Windows Firewall to prevent leaks.\n" +
        "- Disconnect (as Administrator) to restore normal traffic.\n";

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetField(ref _connectionStatus, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set => SetField(ref _statusBrush, value);
    }

    public string CurrentIp
    {
        get => _currentIp;
        set => SetField(ref _currentIp, value);
    }

    public double ConnectionProgress
    {
        get => _connectionProgress;
        set => SetField(ref _connectionProgress, value);
    }

    public string ConnectButtonText
        => _isConnected ? "Disconnect"
            : _isDisconnecting ? "Disconnecting..."
            : _isConnecting ? "Connecting..."
            : "Connect";
    private bool _isDisconnecting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        LoadUserSettings();
        ApplyStartupArguments(Environment.GetCommandLineArgs());
        ApplyTheme(IsDarkMode);
        UpdateConnectVisualState();
        UpdateMaximizeGlyph();

        AppendLog("OnionHop started.");

        if (IsKillSwitchEmergencyBlockActive() && !IsAdministrator())
        {
            StatusMessage = "Kill switch is active and blocking traffic. Restart OnionHop as Administrator and disconnect to restore.";
        }
        else
        {
            _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
        }
    }

    private sealed class UserSettings
    {
        public bool AutoConnect { get; set; }
        public bool KillSwitchEnabled { get; set; }
        public bool IsDarkMode { get; set; }
        public string? SelectedLocation { get; set; }
        public string? SelectedConnectionMode { get; set; }
        public bool UseHybridRouting { get; set; }
    }

    private static string GetSettingsPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnionHop");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private void LoadUserSettings()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);
            if (settings == null)
            {
                return;
            }

            _loadingSettings = true;

            AutoConnect = settings.AutoConnect;
            IsDarkMode = settings.IsDarkMode;

            if (!string.IsNullOrWhiteSpace(settings.SelectedLocation) && Locations.Contains(settings.SelectedLocation))
            {
                SelectedLocation = settings.SelectedLocation;
            }

            if (!string.IsNullOrWhiteSpace(settings.SelectedConnectionMode) && ConnectionModes.Contains(settings.SelectedConnectionMode))
            {
                SelectedConnectionMode = settings.SelectedConnectionMode;
            }

            UseHybridRouting = settings.UseHybridRouting;
            KillSwitchEnabled = settings.KillSwitchEnabled;
        }
        catch (Exception ex)
        {
            AppendLog($"Settings load failed: {ex.Message}");
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void ScheduleSaveUserSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();
        _settingsSaveCts = new CancellationTokenSource();
        var token = _settingsSaveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                SaveUserSettings();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Settings save failed: {ex.Message}");
            }
        }, token);
    }

    private void SaveUserSettings()
    {
        var settings = new UserSettings
        {
            AutoConnect = AutoConnect,
            KillSwitchEnabled = KillSwitchEnabled,
            IsDarkMode = IsDarkMode,
            SelectedLocation = SelectedLocation,
            SelectedConnectionMode = SelectedConnectionMode,
            UseHybridRouting = UseHybridRouting
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetSettingsPath(), json);
    }

    private void ApplyTheme(bool dark)
    {
        var appBg = dark ? Color.FromRgb(12, 16, 26) : Color.FromRgb(233, 237, 245);
        var cardBg = dark ? Color.FromRgb(26, 33, 48) : Colors.White;
        var navBg = dark ? Color.FromRgb(18, 24, 38) : Color.FromRgb(247, 249, 253);
        var titleBarBg = dark ? Color.FromRgb(18, 24, 38) : Color.FromRgb(246, 248, 253);
        var titleBtnBg = dark ? Color.FromRgb(26, 33, 48) : Color.FromRgb(238, 242, 251);
        var titleBtnHover = dark ? Color.FromRgb(33, 42, 61) : Color.FromRgb(226, 233, 255);
        var titleBtnPressed = dark ? Color.FromRgb(40, 51, 74) : Color.FromRgb(207, 216, 247);
        var navBtnFg = dark ? Color.FromRgb(221, 232, 248) : Color.FromRgb(54, 65, 82);
        var navBtnBg = dark ? Color.FromRgb(26, 33, 48) : Color.FromRgb(243, 246, 251);
        var navBtnHover = dark ? Color.FromRgb(33, 42, 61) : Color.FromRgb(227, 236, 255);
        var segmentedBg = dark ? Color.FromRgb(26, 33, 48) : Color.FromRgb(246, 247, 251);
        var primaryText = dark ? Color.FromRgb(226, 234, 248) : Color.FromRgb(42, 50, 66);
        var secondaryText = dark ? Color.FromRgb(163, 178, 205) : Color.FromRgb(123, 131, 150);
        var tertiaryText = dark ? Color.FromRgb(136, 151, 179) : Color.FromRgb(107, 116, 136);
        var inputBorder = dark ? Color.FromRgb(46, 58, 84) : Color.FromRgb(224, 228, 237);
        var toggleThumb = dark ? Color.FromRgb(240, 245, 255) : Colors.White;
        var toggleTrack = dark ? Color.FromRgb(76, 86, 108) : Color.FromRgb(211, 215, 225);
        var comboBg = dark ? Color.FromRgb(26, 33, 48) : Color.FromRgb(246, 248, 252);
        var comboBorder = dark ? Color.FromRgb(46, 58, 84) : Color.FromRgb(224, 228, 237);
        var comboItemHover = dark ? Color.FromRgb(33, 42, 61) : Color.FromRgb(238, 242, 251);
        var comboItemSelected = dark ? Color.FromRgb(40, 51, 74) : Color.FromRgb(221, 229, 251);
        var hero = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromRgb(dark ? (byte)15 : (byte)47, dark ? (byte)23 : (byte)42, dark ? (byte)48 : (byte)124), 0),
                new(Color.FromRgb(dark ? (byte)28 : (byte)51, dark ? (byte)43 : (byte)76, dark ? (byte)77 : (byte)139), 0.4),
                new(Color.FromRgb(dark ? (byte)15 : (byte)45, dark ? (byte)118 : (byte)165, dark ? (byte)110 : (byte)181), 1)
            }
        };

        Resources["AppBackgroundBrush"] = new SolidColorBrush(appBg);
        Resources["CardBackgroundBrush"] = new SolidColorBrush(cardBg);
        Resources["NavBackgroundBrush"] = new SolidColorBrush(navBg);
        Resources["TitleBarBrush"] = new SolidColorBrush(titleBarBg);
        Resources["TitleButtonBrush"] = new SolidColorBrush(titleBtnBg);
        Resources["TitleButtonHoverBrush"] = new SolidColorBrush(titleBtnHover);
        Resources["TitleButtonPressedBrush"] = new SolidColorBrush(titleBtnPressed);
        Resources["NavButtonForegroundBrush"] = new SolidColorBrush(navBtnFg);
        Resources["NavButtonBackgroundBrush"] = new SolidColorBrush(navBtnBg);
        Resources["NavButtonHoverBrush"] = new SolidColorBrush(navBtnHover);
        Resources["SegmentedButtonBackgroundBrush"] = new SolidColorBrush(segmentedBg);
        Resources["HeroGradient"] = hero;
        Resources["PrimaryTextBrush"] = new SolidColorBrush(primaryText);
        Resources["SecondaryTextBrush"] = new SolidColorBrush(secondaryText);
        Resources["TertiaryTextBrush"] = new SolidColorBrush(tertiaryText);
        Resources["InputBorderBrush"] = new SolidColorBrush(inputBorder);
        Resources["ToggleThumbBrush"] = new SolidColorBrush(toggleThumb);
        Resources["ToggleTrackBrush"] = new SolidColorBrush(toggleTrack);
        Resources["ToggleTrackCheckedBrush"] = new SolidColorBrush(Color.FromRgb(54, 193, 122));
        Resources["ComboBackgroundBrush"] = new SolidColorBrush(comboBg);
        Resources["ComboBorderBrush"] = new SolidColorBrush(comboBorder);
        Resources["ComboForegroundBrush"] = new SolidColorBrush(primaryText);
        Resources["ComboItemHoverBrush"] = new SolidColorBrush(comboItemHover);
        Resources["ComboItemSelectedBrush"] = new SolidColorBrush(comboItemSelected);
        Resources["WindowControlBorderBrush"] = new SolidColorBrush(dark ? Color.FromRgb(46, 58, 84) : Color.FromRgb(224, 228, 237));
        Background = (Brush)Resources["AppBackgroundBrush"];
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (AutoConnect)
        {
            await ConnectAsync();
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnecting)
        {
            return;
        }

        if (_isConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async void RefreshIpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected)
        {
            StatusMessage = "Connect first to refresh the exit IP.";
            return;
        }

        await UpdateCurrentIpAsync();
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_isConnecting)
        {
            return;
        }

        if (IsTunMode && !EnsureAdministratorOrRelaunch())
        {
            return;
        }

        _isDisconnecting = false;
        UpdateConnectVisualState();
        Raise(nameof(ConnectButtonText));

        var torPath = Path.Combine(AppContext.BaseDirectory, DefaultTorRelativePath);
        if (!File.Exists(torPath))
        {
            ConnectionStatus = "Tor missing";
            StatusMessage = "Place tor.exe inside a 'tor' folder next to OnionHop.exe.";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            return;
        }

        _connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _isConnecting = true;
        Raise(nameof(ConnectButtonText));
        UpdateConnectVisualState();
        ConnectionStatus = "Connecting...";
        StatusMessage = "Starting Tor and bootstrapping network...";
        StatusBrush = new SolidColorBrush(Color.FromRgb(255, 166, 43));
        ConnectionProgress = 0.1;
        CurrentIp = "Resolving...";

        try
        {
            AppendLog($"Connecting. Mode={SelectedConnectionMode}, Hybrid={UseHybridRouting}, Exit={SelectedLocation}");
            await StartTorAsync(torPath, SelectedLocation, _connectCts.Token);

            if (IsTunMode)
            {
                ConnectionProgress = Math.Max(ConnectionProgress, 0.9);
                StatusMessage = UseHybridRouting
                    ? "Tor is running. Starting Hybrid tunnel (web via Tor)..."
                    : "Tor is running. Starting VPN tunnel (all traffic via Tor)...";
                await StartSingBoxVpnAsync(_connectCts.Token);

                if (KillSwitchEnabled && !UseHybridRouting)
                {
                    AppendLog("Kill switch armed (will block traffic if tunnel drops unexpectedly). ");
                }
            }
            else
            {
                ApplySystemProxy(true);
            }

            _isConnected = true;
            Raise(nameof(ConnectButtonText));
            ConnectionStatus = "Connected";
            StatusBrush = new SolidColorBrush(Color.FromRgb(69, 201, 147));
            StatusMessage = IsTunMode
                ? (UseHybridRouting
                    ? "Tor is running. Hybrid routing is active (browser via Tor)."
                    : "Tor is running. VPN tunnel is active (all traffic via Tor).")
                : "Tor is running. Proxy mode is active (apps must respect proxy settings).";

            ConnectionProgress = 1;
            UpdateConnectVisualState();

            await UpdateCurrentIpAsync();
        }
        catch (OperationCanceledException)
        {
            AppendLog("Connect timed out.");
            StopSingBoxProcess();

            _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
            StatusMessage = "Tor connection timed out.";
            ConnectionStatus = "Disconnected";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            ConnectionProgress = 0;
            StopTorProcess();
        }
        catch (Exception ex)
        {
            AppendLog($"Connect failed: {ex.Message}");
            StopSingBoxProcess();
            StatusMessage = $"Failed to connect: {ex.Message}";
            ConnectionStatus = "Disconnected";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            ConnectionProgress = 0;
            StopTorProcess();
        }
        finally
        {
            _isConnecting = false;
            Raise(nameof(ConnectButtonText));
            UpdateConnectVisualState();
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    private async Task DisconnectAsync()
    {
        if (_isConnecting)
        {
            return;
        }

        _isDisconnecting = true;
        Raise(nameof(ConnectButtonText));
        UpdateConnectVisualState();
        StatusMessage = "Stopping Tor...";
        ConnectionStatus = "Disconnecting...";
        ConnectionProgress = 0.2;

        StopSingBoxProcess();

        _ = Task.Run(() => DisableKillSwitchEmergencyBlock());

        if (_systemProxyApplied)
        {
            ApplySystemProxy(false);
        }

        StopTorProcess();
        await Task.Delay(300);

        _isConnected = false;
        _isDisconnecting = false;
        Raise(nameof(ConnectButtonText));
        UpdateConnectVisualState();
        ConnectionStatus = "Disconnected";
        ConnectionProgress = 0;
        StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
        StatusMessage = "Tor stopped. Traffic is back to normal.";
        CurrentIp = "--.--.--.--";
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Dispatcher.Invoke(() =>
        {
            lock (_logLock)
            {
                LogLines.Add(line);
                while (LogLines.Count > 500)
                {
                    LogLines.RemoveAt(0);
                }
            }
        });
    }

    private void CloseOverlay_Click(object sender, RoutedEventArgs e)
    {
        ShowLogs = false;
        ShowAbout = false;
        ShowSettings = false;
    }

    private void OverlayBackground_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only close if the click was on the semi-transparent background itself,
        // not bubbled up from the card content.
        if (e.OriginalSource == sender)
        {
            CloseOverlay_Click(sender, e);
        }
    }

    private string GetLogsText()
    {
        lock (_logLock)
        {
            return string.Join(Environment.NewLine, LogLines);
        }
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = GetLogsText();
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusMessage = "No logs to copy.";
                return;
            }

            Clipboard.SetText(text);
            StatusMessage = "Logs copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
            AppendLog($"Copy logs failed: {ex.Message}");
        }
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export OnionHop Logs",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"OnionHop-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var text = GetLogsText();
            File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
            StatusMessage = $"Logs exported to {Path.GetFileName(dialog.FileName)}";
            AppendLog($"Logs exported: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            AppendLog($"Export logs failed: {ex.Message}");
        }
    }

    private async Task StartSingBoxVpnAsync(CancellationToken token)
    {
        StopSingBoxProcess();

        var singBoxPath = Path.Combine(AppContext.BaseDirectory, DefaultSingBoxRelativePath);
        if (!File.Exists(singBoxPath))
        {
            throw new FileNotFoundException("VPN component missing: vpn\\sing-box.exe", singBoxPath);
        }

        var wintunPath = Path.Combine(AppContext.BaseDirectory, DefaultWintunRelativePath);
        if (!File.Exists(wintunPath))
        {
            throw new FileNotFoundException("VPN component missing: vpn\\wintun.dll", wintunPath);
        }

        var workDir = Path.GetDirectoryName(singBoxPath) ?? AppContext.BaseDirectory;
        var configDir = Path.Combine(Path.GetTempPath(), "OnionHop", "sing-box");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "sing-box.json");
        await File.WriteAllTextAsync(configPath, BuildSingBoxConfigJson(UseHybridRouting), token);

        AppendLog($"Starting sing-box with config: {configPath}");

        var psi = new ProcessStartInfo(singBoxPath, $"run -c \"{configPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        };

        _singBoxProcess = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _singBoxProcess.Exited += OnSingBoxExited;

        _singBoxProcess.OutputDataReceived += OnSingBoxDataReceived;
        _singBoxProcess.ErrorDataReceived += OnSingBoxDataReceived;

        if (!_singBoxProcess.Start())
        {
            throw new InvalidOperationException("Unable to launch sing-box.exe");
        }

        _singBoxProcess.BeginOutputReadLine();
        _singBoxProcess.BeginErrorReadLine();
        await Task.Delay(750, token);

        if (_singBoxProcess.HasExited)
        {
            throw new InvalidOperationException("sing-box exited unexpectedly during startup.");
        }
    }

    private static string BuildSingBoxConfigJson(bool hybridRouting)
    {
        var rules = new List<object>
        {
            new { action = "sniff" },
            new { process_name = "tor.exe", outbound = "direct" },
            new { ip_is_private = true, outbound = "direct" }
        };

        if (!hybridRouting)
        {
            rules.Insert(1, new { protocol = "dns", action = "hijack-dns" });
            rules.Add(new { network = "udp", outbound = "block" });
        }
        else
        {
            rules.Insert(1, new { protocol = "dns", action = "hijack-dns" });
            rules.Add(new { process_name = BrowserProcessNames, network = "udp", port = 443, outbound = "block" });
            rules.Add(new { process_name = BrowserProcessNames, network = "udp", outbound = "block" });
            rules.Add(new { process_name = BrowserProcessNames, outbound = "tor" });
            rules.Add(new { network = "tcp", port = new[] { 80, 443 }, outbound = "tor" });
        }

        object dnsServer = hybridRouting
             ? new
             {
                 tag = "remote",
                 type = "udp",
                 server = "1.1.1.1",
                 server_port = 53
             }
             : new
             {
                 tag = "remote",
                 type = "tcp",
                 server = "1.1.1.1",
                 server_port = 53,
                 detour = "tor"
             };

        var config = new
        {
            log = new
            {
                level = "info",
                timestamp = true
            },
            dns = new
            {
                servers = new object[]
                {
                    dnsServer
                },
                final = "remote"
            },
            inbounds = new object[]
            {
                new
                {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = "OnionHop",
                    address = new[] { "172.19.0.1/30" },
                    auto_route = true,
                    strict_route = true
                }
            },
            outbounds = new object[]
            {
                new
                {
                    type = "socks",
                    tag = "tor",
                    server = "127.0.0.1",
                    server_port = SocksPort,
                    version = "5"
                },
                new
                {
                    type = "direct",
                    tag = "direct"
                },
                new
                {
                    type = "block",
                    tag = "block"
                }
            },
            route = new
            {
                auto_detect_interface = true,
                rules = rules,
                final = hybridRouting ? "direct" : "tor"
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private void OnSingBoxDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        var line = AnsiEscapeRegex.Replace(e.Data, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        AppendLog($"sing-box: {line}");

        lock (_singBoxLogLock)
        {
            _singBoxRecentLines.Enqueue(line);
            while (_singBoxRecentLines.Count > 40)
            {
                _singBoxRecentLines.Dequeue();
            }
        }

        if (line.Contains("socks5: request rejected", StringComparison.OrdinalIgnoreCase))
        {
            var destMatch = SingBoxConnectionToRegex.Match(line);
            var dest = destMatch.Success ? destMatch.Groups["dest"].Value : "a destination";
            var now = DateTime.UtcNow;
            if (now - _lastVpnMessageUtc >= TimeSpan.FromSeconds(10))
            {
                _lastVpnMessageUtc = now;
                Dispatcher.Invoke(() =>
                    StatusMessage = $"VPN tunnel: Tor rejected a connection to {dest}. Non-web ports are often blocked by Tor exits.");
            }
            return;
        }

        if (line.Contains("outbound/direct", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) || line.Contains("connectex", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (line.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("panic", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.Invoke(() => StatusMessage = $"VPN tunnel: {line}");
        }
    }

    private void OnSingBoxExited(object? sender, EventArgs e)
    {
        var exitCode = 0;
        try
        {
            exitCode = _singBoxProcess?.ExitCode ?? 0;
        }
        catch
        {
        }

        AppendLog($"sing-box exited with code {exitCode}.");

        if (_isConnected && IsTunMode && KillSwitchEnabled && !UseHybridRouting && !_isDisconnecting)
        {
            EnableKillSwitchEmergencyBlock();
        }

        string lastLines;
        lock (_singBoxLogLock)
        {
            lastLines = string.Join("\n", _singBoxRecentLines.Count > 6
                ? _singBoxRecentLines.Skip(Math.Max(0, _singBoxRecentLines.Count - 6))
                : _singBoxRecentLines);
        }

        Dispatcher.Invoke(() =>
        {
            if (_isDisconnecting)
            {
                return;
            }

            ConnectionStatus = "VPN stopped";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            StatusMessage = string.IsNullOrWhiteSpace(lastLines)
                ? $"VPN tunnel stopped unexpectedly (exit code {exitCode}). Disconnecting..."
                : $"VPN tunnel stopped unexpectedly (exit code {exitCode}). Last logs:\n{lastLines}";
        });

        try
        {
            Dispatcher.BeginInvoke(new Action(() => _ = DisconnectAsync()));
        }
        catch
        {
        }
    }

    private void StopSingBoxProcess()
    {
        if (_singBoxProcess == null)
        {
            return;
        }

        try
        {
            if (!_singBoxProcess.HasExited)
            {
                try
                {
                    _singBoxProcess.CloseMainWindow();
                    _singBoxProcess.WaitForExit(1500);
                }
                catch
                {
                }

                _singBoxProcess.Kill(true);
                _singBoxProcess.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to stop sing-box: {ex.Message}");
        }
        finally
        {
            _singBoxProcess.Exited -= OnSingBoxExited;
            _singBoxProcess.OutputDataReceived -= OnSingBoxDataReceived;
            _singBoxProcess.ErrorDataReceived -= OnSingBoxDataReceived;
            _singBoxProcess.Dispose();
            _singBoxProcess = null;

            lock (_singBoxLogLock)
            {
                _singBoxRecentLines.Clear();
            }
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private bool EnsureAdministratorOrRelaunch()
    {
        if (IsAdministrator())
        {
            return true;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            StatusMessage = "VPN mode requires Administrator. Please restart OnionHop as admin.";
            return false;
        }

        var args = new StringBuilder();
        args.Append("--connect ");
        args.Append("--vpn ");
        args.Append(UseHybridRouting ? "--hybrid " : "--strict ");
        args.Append("--location ");
        args.Append('"').Append(SelectedLocation.Replace("\"", string.Empty)).Append('"');

        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = args.ToString()
            };
            Process.Start(psi);
            Application.Current.Shutdown();
            return false;
        }
        catch
        {
            StatusMessage = "VPN mode requires Administrator permission.";
            return false;
        }
    }

    private void ApplyStartupArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--vpn", StringComparison.OrdinalIgnoreCase))
            {
                SystemWideMode = true;
            }

            if (string.Equals(args[i], "--hybrid", StringComparison.OrdinalIgnoreCase))
            {
                UseHybridRouting = true;
            }

            if (string.Equals(args[i], "--strict", StringComparison.OrdinalIgnoreCase))
            {
                UseHybridRouting = false;
            }

            if (string.Equals(args[i], "--location", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                SelectedLocation = args[i + 1];
                i++;
            }

            if (string.Equals(args[i], "--connect", StringComparison.OrdinalIgnoreCase))
            {
                AutoConnect = true;
            }
        }
    }

    private async Task StartTorAsync(string torPath, string location, CancellationToken token)
    {
        _bootstrapSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => _bootstrapSource.TrySetCanceled(token));

        var dataDir = Path.Combine(Path.GetTempPath(), "OnionHop", "tor-data");
        Directory.CreateDirectory(dataDir);

        var argsBuilder = new StringBuilder();
        argsBuilder.Append($"--SocksPort {SocksPort} ");
        argsBuilder.Append($"--DataDirectory \"{dataDir}\" ");
        argsBuilder.Append("--ClientOnly 1 ");
        argsBuilder.Append("--Log \"notice stdout\" ");
        var torDir = Path.GetDirectoryName(torPath) ?? AppContext.BaseDirectory;
        argsBuilder.Append($"--GeoIPFile \"{Path.Combine(torDir, "geoip")}\" ");
        argsBuilder.Append($"--GeoIPv6File \"{Path.Combine(torDir, "geoip6")}\" ");

        var countryCode = GetCountryCode(location);
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            argsBuilder.Append($"--ExitNodes {{{countryCode}}} ");
        }

        var psi = new ProcessStartInfo(torPath, argsBuilder.ToString())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(torPath) ?? AppContext.BaseDirectory
        };

        _torProcess = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _torProcess.Exited += OnTorExited;

        _torProcess.OutputDataReceived += OnTorDataReceived;
        _torProcess.ErrorDataReceived += OnTorDataReceived;

        if (!_torProcess.Start())
        {
            throw new InvalidOperationException("Unable to launch tor.exe");
        }

        _torProcess.BeginOutputReadLine();
        _torProcess.BeginErrorReadLine();

        await _bootstrapSource.Task;
    }

    private void OnTorDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        var line = e.Data;
        if (line.Contains("Bootstrapped", StringComparison.OrdinalIgnoreCase))
        {
            var percent = ExtractProgress(line);
            Dispatcher.Invoke(() =>
            {
                ConnectionProgress = percent / 100d;
                if (percent >= 100)
                {
                    _bootstrapSource?.TrySetResult(true);
                }
            });

            AppendLog($"Tor bootstrapped: {line}");
        }
        else if (line.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Tor error: {line}");
            Dispatcher.Invoke(() => _bootstrapSource?.TrySetException(new InvalidOperationException(line)));
        }
        else if (line.Contains("warn", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Tor log: {line}");
        }
    }

    private static int ExtractProgress(string line)
    {
        var percentIndex = line.IndexOf('%');
        if (percentIndex <= 0)
        {
            return 0;
        }

        var start = percentIndex - 1;
        while (start >= 0 && char.IsDigit(line[start]))
        {
            start--;
        }

        var number = line.Substring(start + 1, percentIndex - start - 1);
        return int.TryParse(number, out var value) ? value : 0;
    }

    private async Task UpdateCurrentIpAsync()
    {
        try
        {
            if (SystemWideMode && !UseHybridRouting)
            {
                using var handler = new HttpClientHandler
                {
                    UseProxy = true
                };

                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(35)
                };

                var response = await client.GetAsync("https://check.torproject.org/api/ip");
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync();
                var doc = await JsonDocument.ParseAsync(stream);
                if (doc.RootElement.TryGetProperty("IP", out var ipProperty))
                {
                    CurrentIp = ipProperty.GetString() ?? CurrentIp;
                }
                else
                {
                    CurrentIp = await response.Content.ReadAsStringAsync();
                }

                StatusMessage = "IP refreshed via Tor route.";
            }
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                CurrentIp = await client.GetStringAsync("https://api.ipify.org");
                StatusMessage = SystemWideMode
                    ? "Hybrid mode: your browser is routed via Tor. Current IP shows your normal route."
                    : "IP refreshed.";
            }
        }
        catch (Exception)
        {
            try
            {
                using var fallbackClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                CurrentIp = await fallbackClient.GetStringAsync("https://api.ipify.org");
                StatusMessage = "IP fetched via standard route (Tor lookup failed).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Unable to fetch IP: {ex.Message}";
            }
        }
    }

    private void StopTorProcess()
    {
        if (_torProcess == null)
        {
            return;
        }

        try
        {
            if (_torProcess != null)
            {
                _torProcess.Exited -= OnTorExited;
                if (!_torProcess.HasExited)
                {
                    try
                    {
                        _torProcess.CloseMainWindow();
                        _torProcess.WaitForExit(1500);
                    }
                    catch
                    {
                    }

                    _torProcess.Kill(true);
                    _torProcess.WaitForExit(5000);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to stop Tor: {ex.Message}");
        }
        finally
        {
            _torProcess.OutputDataReceived -= OnTorDataReceived;
            _torProcess.ErrorDataReceived -= OnTorDataReceived;
            _torProcess.Dispose();
            _torProcess = null;
        }
    }

    private void ApplySystemProxy(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
            if (key == null)
            {
                StatusMessage = "Unable to edit proxy settings.";
                AppendLog("Proxy update failed: registry key not found.");
                return;
            }

            if (enable)
            {
                _previousProxy ??= key.GetValue("ProxyServer") as string;
                if (_previousProxyEnabled == null && key.GetValue("ProxyEnable") is int enabledValue)
                {
                    _previousProxyEnabled = enabledValue;
                }

                key.SetValue("ProxyServer", $"socks=127.0.0.1:{SocksPort}", RegistryValueKind.String);
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                _systemProxyApplied = true;
                AppendLog("Proxy enabled: socks=127.0.0.1:9050");
            }
            else if (_systemProxyApplied)
            {
                key.SetValue("ProxyEnable", _previousProxyEnabled ?? 0, RegistryValueKind.DWord);
                if (_previousProxy is not null)
                {
                    key.SetValue("ProxyServer", _previousProxy, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue("ProxyServer", false);
                }

                _systemProxyApplied = false;
                AppendLog("Proxy disabled (restored previous settings).");
            }

            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Proxy update failed: {ex.Message}";
            AppendLog($"Proxy update failed: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_systemProxyApplied)
        {
            ApplySystemProxy(false);
        }

        DisableKillSwitchEmergencyBlock();

        StopSingBoxProcess();
        StopTorProcess();
    }

    private static string GetKillSwitchRuleName() => "OnionHop KillSwitch Emergency Block";

    private void EnableKillSwitchEmergencyBlock()
    {
        try
        {
            if (!IsAdministrator())
            {
                AppendLog("Kill switch could not be enabled (admin required).");
                return;
            }

            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{GetKillSwitchRuleName()}\" dir=out action=block profile=any enable=yes");
            AppendLog("Kill switch engaged: outbound traffic blocked.");
            Dispatcher.Invoke(() =>
                StatusMessage = "Kill switch engaged: traffic blocked to prevent leaks. Disconnect to restore.");
        }
        catch (Exception ex)
        {
            AppendLog($"Kill switch enable failed: {ex.Message}");
        }
    }

    private void DisableKillSwitchEmergencyBlock()
    {
        try
        {
            if (!IsAdministrator())
            {
                return;
            }

            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
        }
        catch
        {
        }
    }

    private bool IsKillSwitchEmergencyBlockActive()
    {
        try
        {
            var output = RunNetshWithOutput($"advfirewall firewall show rule name=\"{GetKillSwitchRuleName()}\"");
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            return !output.Contains("No rules match", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return;
        }

        proc.WaitForExit(8000);
    }

    private static string RunNetshWithOutput(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return string.Empty;
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(8000);
        return string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string GetCountryCode(string location)
    {
        return location switch
        {
            "United States" => "us",
            "United Kingdom" => "gb",
            "Germany" => "de",
            "France" => "fr",
            "Switzerland" => "ch",
            "Netherlands" => "nl",
            "Canada" => "ca",
            "Singapore" => "sg",
            _ => string.Empty
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(_isConnected) or nameof(_isConnecting))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectButtonText)));
        }

        if (propertyName is nameof(AutoConnect)
            or nameof(KillSwitchEnabled)
            or nameof(IsDarkMode)
            or nameof(SelectedLocation)
            or nameof(SelectedConnectionMode)
            or nameof(UseHybridRouting))
        {
            ScheduleSaveUserSettings();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings = true;
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLogs = true;
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAbout = true;
    }

    private void DashboardButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLogs = false;
        ShowAbout = false;
        ShowSettings = false;
        StatusMessage = "Dashboard is active.";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // ignored
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeGlyph();
    }

    private void ToggleWindowState()
    {
        if (ResizeMode is ResizeMode.CanMinimize or ResizeMode.NoResize)
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeIcon == null)
        {
            return;
        }

        MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void UpdateConnectVisualState()
    {
        if (ConnectBubbleFill is not RadialGradientBrush fill || fill.GradientStops.Count < 2 || ConnectBubbleGlow is not DropShadowEffect glow)
        {
            return;
        }

        var (inner, outer, shadow) = _isConnected
            ? (Color.FromRgb(56, 255, 156), Color.FromRgb(29, 159, 100), Color.FromRgb(44, 255, 156))
            : (_isConnecting || _isDisconnecting)
                ? (Color.FromRgb(255, 229, 138), Color.FromRgb(244, 174, 44), Color.FromRgb(248, 197, 91))
                : (Color.FromRgb(126, 201, 255), Color.FromRgb(60, 120, 216), Color.FromRgb(137, 185, 255));

        fill.GradientStops[0].Color = inner;
        fill.GradientStops[1].Color = outer;
        glow.Color = shadow;
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int lpdwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;
}
