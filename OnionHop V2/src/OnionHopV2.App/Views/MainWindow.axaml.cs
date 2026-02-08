using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SukiUI.Controls;
using OnionHopV2.App.ViewModels;
using Avalonia;
using Avalonia.Controls.Primitives;
using System;

namespace OnionHopV2.App.Views;

public partial class MainWindow : SukiWindow
{
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
        base.OnDataContextChanged(e);
        UpdateCustomChromeCornerRadius();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateCustomChromeCornerRadius();
        }
    }

    private void UpdateCustomChromeCornerRadius()
    {
        RootCornerRadius = new CornerRadius(0);
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
}
