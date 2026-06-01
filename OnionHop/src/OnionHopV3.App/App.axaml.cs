using Avalonia;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OnionHopV3.App.ViewModels;
using OnionHopV3.App.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Material.Icons;
using System;
using System.Linq;
using System.Threading.Tasks;
using OnionHopV3.App.Services;

namespace OnionHopV3.App;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayShowItem;
    private NativeMenuItem? _trayConnectItem;
    private NativeMenuItem? _trayDisconnectItem;
    private NativeMenuItem? _trayExitItem;
    private bool _allowShutdown;
    private BrowserExtensionBridgeServer? _browserExtensionBridgeServer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        LocalizationService.ApplyLanguage("en");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The app lives in the tray; only an explicit Shutdown() (tray Exit, installer, or a
            // non-tray window close) should quit it. The default OnLastWindowClose can race with
            // the close-to-tray Hide() and terminate the app right after it minimizes to the tray.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            var shell = new ShellViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = shell,
            };

            desktop.ShutdownRequested += (_, _) => _allowShutdown = true;
            Program.IpcMessageHandler = message =>
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
            };

            // Use native OS chrome when the user opted in OR we're on macOS (where the custom chrome
            // is never used). UseCustomChrome is the macOS-aware source of truth.
            ApplyWindowChrome(desktop.MainWindow, !shell.State.UseCustomChrome);
            shell.State.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(shell.State.UseNativeTheme) && desktop.MainWindow != null)
                {
                    ApplyWindowChrome(desktop.MainWindow, !shell.State.UseCustomChrome);
                }
            };

            ConfigureWindowCloseToTray(desktop, shell);
            ConfigureTrayAfterFirstFrame(desktop, shell);
            ApplyStartupMinimize(desktop, shell);
            StartBrowserExtensionBridgeAfterFirstFrame(shell);

            desktop.Exit += (_, _) => shell.Dispose();
            desktop.Exit += (_, _) =>
            {
                Program.IpcMessageHandler = null;
                try { _browserExtensionBridgeServer?.Dispose(); } catch { }
                _browserExtensionBridgeServer = null;
            };

            Dispatcher.UIThread.Post(async () => await shell.State.InitializeAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyWindowChrome(Window window, bool useNativeChrome)
    {
        window.SystemDecorations = SystemDecorations.Full;

        if (useNativeChrome)
        {
            // Opt-in: the standard OS title bar (icon + title + caption buttons).
            window.ExtendClientAreaToDecorationsHint = false;
            window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.Default;
            window.ExtendClientAreaTitleBarHeightHint = -1;
        }
        else
        {
            // Default: integrated chromeless title bar (DNS-Hop look). No OS title bar, icon, or
            // title; MainWindow draws its own caption buttons and provides the drag region.
            window.ExtendClientAreaToDecorationsHint = true;
            window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            window.ExtendClientAreaTitleBarHeightHint = 0;
        }
    }

    private void ConfigureTray(IClassicDesktopStyleApplicationLifetime desktop, ShellViewModel shell)
    {
        try
        {
            var iconStream = AssetLoader.Open(new Uri("avares://OnionHopV3/Assets/OnionHop.ico"));
            var icon = new WindowIcon(iconStream);

            var menu = new NativeMenu();

            _trayShowItem = new NativeMenuItem(LocalizationService.Get("Tray.Show"))
            {
                IsEnabled = true,
                Icon = CreateTrayMenuIcon(MaterialIconKind.OpenInApp)
            };
            _trayShowItem.Click += (_, _) => ShowMainWindow(desktop);

            _trayConnectItem = new NativeMenuItem(LocalizationService.Get("Tray.Connect"))
            {
                Icon = CreateTrayMenuIcon(MaterialIconKind.LanConnect)
            };
            _trayConnectItem.Click += (_, _) => Dispatcher.UIThread.Post(async () => await shell.State.ConnectCommand.ExecuteAsync(null));

            _trayDisconnectItem = new NativeMenuItem(LocalizationService.Get("Tray.Disconnect"))
            {
                Icon = CreateTrayMenuIcon(MaterialIconKind.LanDisconnect)
            };
            _trayDisconnectItem.Click += (_, _) => Dispatcher.UIThread.Post(async () => await shell.State.DisconnectCommand.ExecuteAsync(null));

            _trayExitItem = new NativeMenuItem(LocalizationService.Get("Tray.Exit"))
            {
                Icon = CreateTrayMenuIcon(MaterialIconKind.Power)
            };
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

    private void ConfigureTrayAfterFirstFrame(IClassicDesktopStyleApplicationLifetime desktop, ShellViewModel shell)
    {
        var needsTrayImmediately =
            shell.State.MinimizeToTray ||
            desktop.Args?.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;

        if (needsTrayImmediately)
        {
            ConfigureTray(desktop, shell);
            return;
        }

        Dispatcher.UIThread.Post(
            () => ConfigureTray(desktop, shell),
            DispatcherPriority.Background);
    }

    private void StartBrowserExtensionBridgeAfterFirstFrame(ShellViewModel shell)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    if (Program.IsIpcCancellationRequested)
                    {
                        return;
                    }

                    _browserExtensionBridgeServer = new BrowserExtensionBridgeServer(shell.State);
                    _browserExtensionBridgeServer.Start();
                }
                catch
                {
                    // Browser extension integration is optional and should never slow or block app launch.
                }
            });
        }, DispatcherPriority.Background);
    }

    private static Bitmap CreateTrayMenuIcon(MaterialIconKind kind)
    {
        const int size = 18;
        const double materialViewBox = 24d;
        var pathData = MaterialIconDataProvider.GetData(kind);
        var geometry = StreamGeometry.Parse(pathData);
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();
        using (context.PushTransform(Matrix.CreateScale(size / materialViewBox, size / materialViewBox)))
        {
            context.DrawGeometry(Brushes.White, null, geometry);
        }

        return bitmap;
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
                return; // an explicit shutdown is in progress; let the window close
            }

            // A window close (the OS button or our chromeless caption close, which calls
            // Window.Close() and is therefore "programmatic") minimizes to the tray when the
            // option is on. Genuine shutdowns (tray Exit, installer, OS) set _allowShutdown above,
            // so they fall through to close normally.
            if (shell.State.MinimizeToTray)
            {
                e.Cancel = true;
                try
                {
                    desktop.MainWindow.Hide();
                    if (_trayIcon != null)
                    {
                        _trayIcon.IsVisible = true;
                    }
                }
                catch
                {
                    // If hiding fails, quit cleanly rather than wedging in a half-closed state.
                    _allowShutdown = true;
                    Dispatcher.UIThread.Post(() => desktop.Shutdown());
                }

                return;
            }

            // Otherwise (tray-on-close is off, or a programmatic close) this should quit the app.
            // With OnExplicitShutdown the window closing alone won't exit, so request it explicitly.
            e.Cancel = true;
            _allowShutdown = true;
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        };
    }

    private void ApplyStartupMinimize(IClassicDesktopStyleApplicationLifetime desktop, ShellViewModel shell)
    {
        if (desktop.MainWindow == null)
        {
            return;
        }

        // Only honor explicit minimized launches (used by Windows autostart command line).
        // Manual app launches should always open a visible window.
        var minimizedArg = desktop.Args?.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;
        if (!minimizedArg)
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
