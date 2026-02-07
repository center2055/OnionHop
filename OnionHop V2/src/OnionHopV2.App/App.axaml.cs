using Avalonia;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OnionHopV2.App.ViewModels;
using OnionHopV2.App.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SukiUI.Controls;
using OnionHopV2.App.Services;

namespace OnionHopV2.App;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayShowItem;
    private NativeMenuItem? _trayConnectItem;
    private NativeMenuItem? _trayDisconnectItem;
    private NativeMenuItem? _trayExitItem;
    private bool _allowShutdown;
    private CancellationTokenSource? _ipcCts;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        LocalizationService.ApplyLanguage("en");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            var shell = new ShellViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = shell,
            };

            desktop.ShutdownRequested += (_, _) => _allowShutdown = true;
            _ipcCts = new CancellationTokenSource();
            SingleInstanceIpc.StartServer(message =>
            {
                if (string.Equals(message, "show", StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.UIThread.Post(() => ShowMainWindow(desktop));
                    return Task.CompletedTask;
                }

                if (string.Equals(message, "shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _allowShutdown = true;
                        desktop.Shutdown();
                    });
                    return Task.CompletedTask;
                }

                return Task.CompletedTask;
            }, _ipcCts.Token);

            ApplyWindowChrome(desktop.MainWindow, shell.State.UseNativeTheme);
            shell.State.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(shell.State.UseNativeTheme) && desktop.MainWindow != null)
                {
                    ApplyWindowChrome(desktop.MainWindow, shell.State.UseNativeTheme);
                }
            };

            ConfigureTray(desktop, shell);
            ConfigureWindowCloseToTray(desktop, shell);
            ApplyStartupMinimize(desktop, shell);

            desktop.Exit += (_, _) => shell.Dispose();
            desktop.Exit += (_, _) =>
            {
                try { _ipcCts?.Cancel(); } catch { }
                try { _ipcCts?.Dispose(); } catch { }
                _ipcCts = null;
            };

            Dispatcher.UIThread.Post(async () => await shell.State.InitializeAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyWindowChrome(Window window, bool useNativeChrome)
    {
        // Windows: force custom chrome so we can fully remove the native titlebar strip and use our in-window buttons.
        if (OperatingSystem.IsWindows())
        {
            useNativeChrome = false;
        }

        if (useNativeChrome)
        {
            window.SystemDecorations = SystemDecorations.Full;
            window.ExtendClientAreaToDecorationsHint = false;
            window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.Default;
            window.ExtendClientAreaTitleBarHeightHint = 0;

            if (window is SukiWindow sukiWindow)
            {
                sukiWindow.IsTitleBarVisible = false;
                sukiWindow.ShowTitlebarBackground = false;
                sukiWindow.CanFullScreen = false;
                sukiWindow.CanPin = false;
            }

            return;
        }

        window.SystemDecorations = SystemDecorations.None;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        window.ExtendClientAreaTitleBarHeightHint = 0;

        if (window is SukiWindow sw)
        {
            // Hide Suki titlebar strip; we'll render our own buttons in-window.
            sw.IsTitleBarVisible = false;
            sw.ShowTitlebarBackground = false;
            sw.CanFullScreen = false;
            sw.CanPin = false;
        }
    }

    private void ConfigureTray(IClassicDesktopStyleApplicationLifetime desktop, ShellViewModel shell)
    {
        try
        {
            var iconStream = AssetLoader.Open(new Uri("avares://OnionHopV2/Assets/OnionHop.ico"));
            var icon = new WindowIcon(iconStream);

            var menu = new NativeMenu();

            _trayShowItem = new NativeMenuItem(LocalizationService.Get("Tray.Show"))
            {
                IsEnabled = true
            };
            _trayShowItem.Click += (_, _) => ShowMainWindow(desktop);

            _trayConnectItem = new NativeMenuItem(LocalizationService.Get("Tray.Connect"));
            _trayConnectItem.Click += (_, _) => Dispatcher.UIThread.Post(async () => await shell.State.ConnectCommand.ExecuteAsync(null));

            _trayDisconnectItem = new NativeMenuItem(LocalizationService.Get("Tray.Disconnect"));
            _trayDisconnectItem.Click += (_, _) => Dispatcher.UIThread.Post(async () => await shell.State.DisconnectCommand.ExecuteAsync(null));

            _trayExitItem = new NativeMenuItem(LocalizationService.Get("Tray.Exit"));
            _trayExitItem.Click += (_, _) =>
            {
                _allowShutdown = true;
                desktop.Shutdown();
            };

            menu.Items.Add(_trayShowItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(_trayConnectItem);
            menu.Items.Add(_trayDisconnectItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(_trayExitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = LocalizationService.Get("Tray.Tooltip"),
                Menu = menu,
                IsVisible = shell.State.MinimizeToTray
            };

            _trayIcon.Clicked += (_, _) => ShowMainWindow(desktop);
            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });

            shell.State.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(shell.State.MinimizeToTray) && _trayIcon != null)
                {
                    _trayIcon.IsVisible = shell.State.MinimizeToTray;
                }
            };

            LocalizationService.LanguageChanged += (_, _) => UpdateTrayLocalization();
        }
        catch
        {
            // Tray is optional; ignore failures on platforms/DEs without support.
        }
    }

    private void UpdateTrayLocalization()
    {
        if (_trayShowItem == null)
        {
            return;
        }

        _trayShowItem.Header = LocalizationService.Get("Tray.Show");
        _trayConnectItem!.Header = LocalizationService.Get("Tray.Connect");
        _trayDisconnectItem!.Header = LocalizationService.Get("Tray.Disconnect");
        _trayExitItem!.Header = LocalizationService.Get("Tray.Exit");
        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = LocalizationService.Get("Tray.Tooltip");
        }
    }

    private void ConfigureWindowCloseToTray(IClassicDesktopStyleApplicationLifetime desktop, ShellViewModel shell)
    {
        if (desktop.MainWindow == null)
        {
            return;
        }

        desktop.MainWindow.Closing += (_, e) =>
        {
            if (_allowShutdown)
            {
                return;
            }

            if (!shell.State.MinimizeToTray)
            {
                return;
            }

            // Only intercept user-initiated closes. Allow programmatic closes so installers/updaters can shut down the app.
            if (e.IsProgrammatic)
            {
                return;
            }

            e.Cancel = true;
            desktop.MainWindow.Hide();
            if (_trayIcon != null)
            {
                _trayIcon.IsVisible = true;
            }
        };
    }

    private void ApplyStartupMinimize(IClassicDesktopStyleApplicationLifetime desktop, ShellViewModel shell)
    {
        if (desktop.MainWindow == null)
        {
            return;
        }

        var minimizedArg = desktop.Args?.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;
        var shouldMinimize = minimizedArg || shell.State.StartMinimized;
        if (!shouldMinimize)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (shell.State.MinimizeToTray)
            {
                desktop.MainWindow.Hide();
                if (_trayIcon != null)
                {
                    _trayIcon.IsVisible = true;
                }
                return;
            }

            desktop.MainWindow.WindowState = WindowState.Minimized;
        });
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = desktop.MainWindow;
        if (window == null)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
