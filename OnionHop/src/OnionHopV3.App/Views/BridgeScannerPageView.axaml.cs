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

    // Persist an edited label when the row's label TextBox loses focus (saved-library subtab, v3.6).
    private void OnSavedLabelLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SavedBridgeRow row)
        {
            ViewModel?.Saved.CommitLabel(row);
        }
    }

    // Import bridge lines from a file into the bridge scanner's input (BridgeHop parity).
    private async void OnImportBridgeFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = await PickAndReadTextFileAsync("Import bridge list");
        if (text != null)
        {
            ViewModel?.LoadBridgesFromText(text);
        }
    }

    // Import candidate domains from a file into the SNI scanner's input.
    private async void OnImportSniFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = await PickAndReadTextFileAsync("Import domains");
        if (text != null && ViewModel != null)
        {
            ViewModel.Sni.DomainList = text;
            ViewModel.Sni.UseCustomDomains = true;
        }
    }

    // Save the working SNI hosts to a .txt file, mirroring the bridge scanner's Export Working.
    private async void OnExportSniWorkingClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = ViewModel?.Sni.WorkingSniText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await SaveTextFileAsync("Export working SNI hosts", $"onionhop-working-sni-{DateTime.Now:yyyyMMdd-HHmmss}.txt", text);
    }

    private async System.Threading.Tasks.Task SaveTextFileAsync(string title, string suggestedName, string text)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null)
        {
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text file")
                {
                    Patterns = new[] { "*.txt" },
                    MimeTypes = new[] { "text/plain" },
                    AppleUniformTypeIdentifiers = new[] { "public.plain-text" }
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
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
            StartupLogger.Write("SNI export failed.", ex);
        }
    }

    private async System.Threading.Tasks.Task<string?> PickAndReadTextFileAsync(string title)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null)
        {
            return null;
        }

        try
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Text file") { Patterns = new[] { "*.txt", "*.csv" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } }
                }
            });

            var file = files.Count > 0 ? files[0] : null;
            if (file == null)
            {
                return null;
            }

            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            // An async-void caller must never throw to the dispatcher; a failed import is logged.
            StartupLogger.Write("Import file failed.", ex);
            return null;
        }
    }

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
