using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace OnionHopV3.App.Views;

public partial class MainWindow : Window
{
    // Height (px) of the integrated drag strip at the top of the window.
    private const double TitleBarDragHeight = 36;

    // Segoe Fluent Icons only exists on Windows. Elsewhere the caption glyphs came out as empty
    // "missing glyph" boxes (the AppImage catalog's screenshot of the Linux build shows three), so
    // other platforms get the same icons drawn as 10px vector paths. Settable for render checks.
    internal static bool UseGlyphCaptionIcons = OperatingSystem.IsWindows();

    private const string MinimizeIcon = "M0,5.5 L10,5.5";
    private const string MaximizeIcon = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreIcon = "M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z M2.5,2.5 L2.5,0.5 L9.5,0.5 L9.5,7.5 L7.5,7.5";
    private const string CloseIcon = "M0.5,0.5 L9.5,9.5 M9.5,0.5 L0.5,9.5";

    public MainWindow()
    {
        InitializeComponent();

        if (!UseGlyphCaptionIcons)
        {
            if (this.FindControl<Button>("MinimizeButton") is { } minimize)
            {
                minimize.Content = CaptionIcon(MinimizeIcon);
            }

            if (this.FindControl<Button>("CloseButton") is { } close)
            {
                close.Content = CaptionIcon(CloseIcon);
            }

            UpdateMaximizeGlyph();
        }

        Initialized += (_, _) => ApplyWindowChrome();
        Opened += (_, _) =>
        {
            ApplyWindowChrome();
            UpdateMaximizeGlyph();
            FitToScreenWorkingArea();
        };
        ActualThemeVariantChanged += (_, _) => ApplyWindowChrome();

        // The window has no title bar (NoChrome + extended client area), so re-add a drag region:
        // an unhandled left click in the top strip drags the window; a double-click toggles maximize.
        // Caption buttons and other interactive controls handle their own pointer events, so this
        // (unhandled-only) handler does not interfere with them.
        AddHandler(PointerPressedEvent, OnTitleBarPointerPressed, RoutingStrategies.Bubble, handledEventsToo: false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizeGlyph();
        }
    }

    // The default window size (and minimum size) can exceed the visible work area on displays scaled
    // above 100 percent, because scaling shrinks the logical space the window is measured in. Since the
    // window is chromeless (its own caption buttons live at the top of the client area), an oversized
    // window pushes those buttons off screen with no OS title bar to grab (issue #67). Clamp the size
    // and minimum to the current screen's working area, then re-center so everything stays reachable.
    private void FitToScreenWorkingArea()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        // WorkingArea is in physical pixels; Width/Height/MinWidth/MinHeight are logical (DIP) units.
        var workWidth = screen.WorkingArea.Width / scaling;
        var workHeight = screen.WorkingArea.Height / scaling;

        // Leave a small margin so the window border/shadow stays visible.
        const double margin = 24;
        var maxWidth = Math.Max(320, workWidth - margin);
        var maxHeight = Math.Max(320, workHeight - margin);

        // The minimum must never exceed the screen, or the window cannot be shrunk to fit and its
        // off-screen edges (with the caption buttons) stay unreachable.
        if (MinWidth > maxWidth)
        {
            MinWidth = maxWidth;
        }

        if (MinHeight > maxHeight)
        {
            MinHeight = maxHeight;
        }

        var resized = false;
        if (Width > maxWidth)
        {
            Width = maxWidth;
            resized = true;
        }

        if (Height > maxHeight)
        {
            Height = maxHeight;
            resized = true;
        }

        // Re-center in the working area if we shrank the window or if it currently spills off screen.
        var physWidth = (int)Math.Round(Width * scaling);
        var physHeight = (int)Math.Round(Height * scaling);
        var area = screen.WorkingArea;
        var offScreen = Position.X < area.X || Position.Y < area.Y ||
                        Position.X + physWidth > area.X + area.Width ||
                        Position.Y + physHeight > area.Y + area.Height;

        if (resized || offScreen)
        {
            var x = area.X + Math.Max(0, (area.Width - physWidth) / 2);
            var y = area.Y + Math.Max(0, (area.Height - physHeight) / 2);
            Position = new PixelPoint(x, y);
        }
    }

    private void UpdateMaximizeGlyph()
    {
        if (this.FindControl<Button>("MaximizeButton") is { } button)
        {
            var maximized = WindowState == WindowState.Maximized;
            // 0xE922 = maximize, 0xE923 = restore (Segoe Fluent Icons).
            button.Content = UseGlyphCaptionIcons
                ? ((char)(maximized ? 0xE923 : 0xE922)).ToString()
                : CaptionIcon(maximized ? RestoreIcon : MaximizeIcon);
        }
    }

    private static Path CaptionIcon(string data) => new()
    {
        Data = StreamGeometry.Parse(data),
        Width = 10,
        Height = 10,
        StrokeThickness = 1,
        Stretch = Stretch.None
    };

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || point.Position.Y > TitleBarDragHeight)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    // Win11 rounded corners + a window border that follows the app's light/dark variant. There is no
    // OS title bar to theme (NoChrome), so this only affects the border/corner.
    private void ApplyWindowChrome()
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

        var hwnd = handle.Handle;
        try
        {
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            var useDark = ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            }
        }
        catch
        {
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
