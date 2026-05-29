using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OnionHopV2.App.ViewModels;

namespace OnionHopV2.App.Views;

public partial class BridgeScannerPageView : UserControl
{
    public BridgeScannerPageView()
    {
        InitializeComponent();
    }

    private BridgeScannerPageViewModel? ViewModel => DataContext as BridgeScannerPageViewModel;

    private async void OnExportWorkingClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel == null || !viewModel.HasWorkingBridges)
        {
            return;
        }

        var text = viewModel.WorkingBridgeText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider == null)
        {
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export working bridges",
            SuggestedFileName = $"onionhop-working-bridges-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt"
        });

        if (file == null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(text);
    }
}
