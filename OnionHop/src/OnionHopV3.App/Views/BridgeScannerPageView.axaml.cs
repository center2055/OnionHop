using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OnionHopV3.App.ViewModels;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.Views;

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
            DefaultExtension = "txt",
            // Without an explicit text file type the OS Save dialog defaults to "All files" and does
            // not append .txt, so the export landed as an extension-less / unknown file type. Offering
            // a Text file type (and a catch-all) makes the picker save a proper .txt document.
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text file")
                {
                    Patterns = new[] { "*.txt" },
                    MimeTypes = new[] { "text/plain" },
                    AppleUniformTypeIdentifiers = new[] { "public.plain-text" }
                },
                new FilePickerFileType("All files")
                {
                    Patterns = new[] { "*" }
                }
            }
        });

        if (file == null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(text);
        }
        catch (Exception ex)
        {
            // Never let an async-void export failure crash the app; log and move on.
            StartupLogger.Write("Bridge export failed.", ex);
        }
    }
}
