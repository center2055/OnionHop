using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.VisualTree;
using SukiUI.Controls;
using OnionHopV2.App.ViewModels;
using Avalonia;
using Avalonia.Controls.Primitives;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OnionHopV2.App.Views;

public partial class MainWindow : SukiWindow
{
    private static readonly CornerRadius RoundedWindowCornerRadius = new(12);
    private ShellViewModel? _shellViewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = Avalonia.Controls.WindowState.Minimized;
    }

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? Avalonia.Controls.WindowState.Normal
            : Avalonia.Controls.WindowState.Maximized;
        UpdateCustomChromeCornerRadius();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell && shell.State.MinimizeToTray)
        {
            Hide();
            return;
        }

        Close();
    }

    private void OnDragRegionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ShellViewModel { State.UseCustomChrome: true })
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || WindowState == WindowState.Maximized)
        {
            return;
        }

        if (point.Position.Y > 72 || IsInteractivePointerSource(e.Source))
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || WindowState == WindowState.Maximized)
        {
            return;
        }

        if (sender is not Control { Tag: string edgeTag })
        {
            return;
        }

        var edge = edgeTag switch
        {
            "Left" => WindowEdge.West,
            "Right" => WindowEdge.East,
            "Top" => WindowEdge.North,
            "Bottom" => WindowEdge.South,
            "TopLeft" => WindowEdge.NorthWest,
            "TopRight" => WindowEdge.NorthEast,
            "BottomLeft" => WindowEdge.SouthWest,
            "BottomRight" => WindowEdge.SouthEast,
            _ => (WindowEdge?)null
        };

        if (edge.HasValue)
        {
            BeginResizeDrag(edge.Value, e);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_shellViewModel != null)
        {
            _shellViewModel.State.PropertyChanged -= OnStatePropertyChanged;
        }

        base.OnDataContextChanged(e);

        _shellViewModel = DataContext as ShellViewModel;
        if (_shellViewModel != null)
        {
            _shellViewModel.State.PropertyChanged += OnStatePropertyChanged;
        }

        UpdateCustomChromeCornerRadius();
        ApplyMacNativeFullScreenCapability();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateCustomChromeCornerRadius();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateCustomChromeCornerRadius();
        ApplyMacNativeFullScreenCapability();
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppStateViewModel.UseNativeTheme) ||
            e.PropertyName == nameof(AppStateViewModel.UseCustomChrome))
        {
            UpdateCustomChromeCornerRadius();
            ApplyMacNativeFullScreenCapability();
        }
    }

    private void UpdateCustomChromeCornerRadius()
    {
        var shouldRound = DataContext is ShellViewModel { State.UseCustomChrome: true }
                          && WindowState != WindowState.Maximized
                          && WindowState != WindowState.FullScreen;

        RootCornerRadius = shouldRound ? RoundedWindowCornerRadius : new CornerRadius(0);
        ApplyNativeWindowCornerPreference(shouldRound);
    }

    private void ApplyNativeWindowCornerPreference(bool shouldRound)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero ||
            !string.Equals(handle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cornerPref = shouldRound ? DwmWindowCornerPreference.RoundSmall : DwmWindowCornerPreference.DoNotRound;
        var prefValue = (int)cornerPref;
        _ = DwmSetWindowAttribute(handle.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref prefValue, sizeof(int));

        var borderColor = shouldRound ? DWMWA_COLOR_NONE : DWMWA_COLOR_DEFAULT;
        _ = DwmSetWindowAttribute(handle.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));
    }

    private System.Threading.Timer? _macFullScreenWiringTimer;

    private void ApplyMacNativeFullScreenCapability()
    {
        _macFullScreenWiringTimer?.Dispose();
        _macFullScreenWiringTimer = null;

        if (!OperatingSystem.IsMacOS() ||
            DataContext is not ShellViewModel { State.UseNativeTheme: true })
        {
            return;
        }

        // Defer the wiring so it runs AFTER SukiUI/Avalonia have finished their
        // own window setup (which can reset the zoom button's action/target).
        // We wire multiple times with increasing delays to ensure we win the race.
        var attempt = 0;
        _macFullScreenWiringTimer = new System.Threading.Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WireMacFullScreenButton());
            if (++attempt >= 5)
            {
                _macFullScreenWiringTimer?.Dispose();
                _macFullScreenWiringTimer = null;
            }
        }, null, 200, 500);
    }

    private void WireMacFullScreenButton()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero)
        {
            return;
        }

        var nsWindow = ResolveMacWindowHandle(handle);
        if (nsWindow == IntPtr.Zero)
        {
            return;
        }

        // Enable fullscreen capability via collection behavior.
        const nuint fullScreenPrimary = 1u << 7;
        var collectionBehaviorSelector = sel_registerName("collectionBehavior");
        var setCollectionBehaviorSelector = sel_registerName("setCollectionBehavior:");
        var collectionBehavior = nuint_objc_msgSend(nsWindow, collectionBehaviorSelector);
        var updatedBehavior = collectionBehavior | fullScreenPrimary;
        if (updatedBehavior != collectionBehavior)
        {
            void_objc_msgSend_nuint(nsWindow, setCollectionBehaviorSelector, updatedBehavior);
        }

        // Wire the green (zoom) button directly to toggleFullScreen:.
        // Avalonia/SukiUI intercepts the default zoom: action, preventing the native
        // fullscreen button from working on click. By re-targeting the button, we
        // bypass the interception.
        var zoomButton = IntPtr_objc_msgSend_long(nsWindow, sel_registerName("standardWindowButton:"), 2); // NSWindowZoomButton = 2
        if (zoomButton != IntPtr.Zero)
        {
            void_objc_msgSend_IntPtr(zoomButton, sel_registerName("setAction:"), sel_registerName("toggleFullScreen:"));
            void_objc_msgSend_IntPtr(zoomButton, sel_registerName("setTarget:"), nsWindow);
        }
    }

    private static IntPtr ResolveMacWindowHandle(IPlatformHandle handle)
    {
        if (string.Equals(handle.HandleDescriptor, "NSWindow", StringComparison.OrdinalIgnoreCase))
        {
            return handle.Handle;
        }

        if (!string.Equals(handle.HandleDescriptor, "NSView", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var windowSelector = sel_registerName("window");
        return IntPtr_objc_msgSend(handle.Handle, windowSelector);
    }

    private static bool IsInteractivePointerSource(object? source)
    {
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is Button or TextBox or ComboBox or ToggleSwitch or Slider or ScrollBar or TabStripItem or ListBoxItem)
            {
                return true;
            }
        }

        return false;
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint nuint_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend_nuint(IntPtr receiver, IntPtr selector, nuint value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend_long(IntPtr receiver, IntPtr selector, long arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
}
