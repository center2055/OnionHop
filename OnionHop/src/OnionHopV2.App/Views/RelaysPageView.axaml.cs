using Avalonia.Controls;
using OnionHopV2.App.Services;
using OnionHopV2.App.ViewModels;

namespace OnionHopV2.App.Views;

public partial class RelaysPageView : UserControl
{
    public RelaysPageView()
    {
        InitializeComponent();
    }

    private async void OnCopyFingerprintClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is RelaysPageViewModel viewModel && viewModel.SelectedRelay != null)
        {
            await ClipboardHelper.SetTextAsync(
                this,
                viewModel.SelectedRelay.Fingerprint,
                viewModel.State.ClipboardProtectionEnabled);
        }
    }
}
