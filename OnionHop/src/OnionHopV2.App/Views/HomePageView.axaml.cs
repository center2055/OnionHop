using Avalonia.Controls;
using OnionHopV2.App.Services;
using OnionHopV2.App.ViewModels;

namespace OnionHopV2.App.Views;

public partial class HomePageView : UserControl
{
    public HomePageView()
    {
        InitializeComponent();
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
