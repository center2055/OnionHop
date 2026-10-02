using Avalonia.Controls;
using Avalonia.Layout;
using OnionHopV3.App.Services;
using OnionHopV3.App.ViewModels;

namespace OnionHopV3.App.Views;

public partial class HomePageView : UserControl
{
    // Below this status-card width the IP panel sits under the status text instead of beside it:
    // next to the orb and the panel, the text column would be too narrow for the title and buttons.
    private const double IpPanelBesideMinWidth = 640;

    public HomePageView()
    {
        InitializeComponent();
    }

    private void OnStatusGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (this.FindControl<Border>("IpPanel") is not { } panel)
        {
            return;
        }

        var beside = e.NewSize.Width >= IpPanelBesideMinWidth;
        Grid.SetRow(panel, beside ? 0 : 1);
        Grid.SetColumn(panel, beside ? 2 : 1);
        panel.HorizontalAlignment = beside ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        // A grid RowSpacing would also pad the empty second row when the panel sits beside the text.
        panel.Margin = beside ? new Avalonia.Thickness(0) : new Avalonia.Thickness(0, 8, 0, 0);
    }

    private async void OnCopyIpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is HomePageViewModel viewModel)
        {
            await ClipboardHelper.SetTextAsync(
                this,
                viewModel.State.CurrentIp,
                viewModel.State.ClipboardProtectionEnabled);
        }
    }
}
