using System;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OnionHopV3.App.Services;
using OnionHopV3.App.ViewModels;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.Views;

public partial class LogsPageView : UserControl
{
    public LogsPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private AppStateViewModel? State => (DataContext as PageViewModelBase)?.State;
    private LogsPageViewModel? ViewModel => DataContext as LogsPageViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.VisibleEntries.CollectionChanged -= OnVisibleEntriesChanged;
            ViewModel.VisibleEntries.CollectionChanged += OnVisibleEntriesChanged;
        }
    }

    private void OnVisibleEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel?.AutoScroll != true)
        {
            return;
        }

        // Scroll the newest entry into view. With the virtualizing ListBox this realizes only the
        // tail rows, so auto-scroll no longer forces the whole list to materialize.
        var entries = ViewModel.VisibleEntries;
        if (EntriesListBox != null && entries.Count > 0)
        {
            EntriesListBox.ScrollIntoView(entries[^1]);
        }
    }

    private async void OnCopyCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // On the bridges tab, copy only the bridge(s) Tor is actually using so the user can carry a
        // working bridge to another device, not the whole supplemented list (issue #56).
        var text = ViewModel?.IsBridgesExport == true
            ? ViewModel.GetBridgeCopyText()
            : GetCurrentLogText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await ClipboardHelper.SetTextAsync(this, text, State?.ClipboardProtectionEnabled == true);
    }

    private async void OnExportCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // The bridges tab exports a CSV with status columns (issue #56); every other tab exports the
        // plain log text.
        var isBridges = ViewModel?.IsBridgesExport == true;
        var text = isBridges ? ViewModel!.GetBridgeCsv() : GetCurrentLogText();
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

        var viewModel = DataContext as LogsPageViewModel;
        var name = viewModel?.GetSelectedFileNameStem() ?? "logs";
        var extension = isBridges ? "csv" : "txt";
        var fileType = isBridges
            ? new FilePickerFileType("CSV file")
            {
                Patterns = new[] { "*.csv" },
                MimeTypes = new[] { "text/csv" },
                AppleUniformTypeIdentifiers = new[] { "public.comma-separated-values-text" }
            }
            : new FilePickerFileType("Text file")
            {
                Patterns = new[] { "*.txt" },
                MimeTypes = new[] { "text/plain" },
                AppleUniformTypeIdentifiers = new[] { "public.plain-text" }
            };
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = isBridges ? "Export bridges" : "Export logs",
            SuggestedFileName = $"onionhop-{name.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
            DefaultExtension = extension,
            // Offer an explicit file type so the OS dialog appends the extension instead of saving an
            // extension-less / unknown file type (defaults to "All files" otherwise).
            FileTypeChoices = new[]
            {
                fileType,
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
            // An async-void handler must not throw to the dispatcher (it would crash the app). A
            // failed export (read-only path, full disk, locked file) is logged and otherwise ignored.
            StartupLogger.Write("Log export failed.", ex);
        }
    }

    private async void OnCopyBridgeRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Copy just this row's bridge line (issue #69). The button's DataContext is the BridgeRowEntry.
        if ((sender as Control)?.DataContext is not BridgeRowEntry row || string.IsNullOrWhiteSpace(row.RawLine))
        {
            return;
        }

        await ClipboardHelper.SetTextAsync(this, row.RawLine, State?.ClipboardProtectionEnabled == true);
    }

    private void OnClearCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var state = State;
        if (state == null)
        {
            return;
        }

        if (ViewModel != null)
        {
            ViewModel.ClearSelectedLogsCommand.Execute(null);
        }
    }

    private string GetCurrentLogText()
    {
        if (DataContext is not LogsPageViewModel viewModel)
        {
            return string.Empty;
        }

        return viewModel.GetVisibleLogText();
    }
}
