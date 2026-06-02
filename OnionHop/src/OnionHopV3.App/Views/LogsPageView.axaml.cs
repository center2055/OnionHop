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
        var text = GetCurrentLogText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await ClipboardHelper.SetTextAsync(this, text, State?.ClipboardProtectionEnabled == true);
    }

    private async void OnExportCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = GetCurrentLogText();
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
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export logs",
            SuggestedFileName = $"onionhop-{name.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            // Offer an explicit text file type so the OS dialog appends .txt instead of saving an
            // extension-less / unknown file type (defaults to "All files" otherwise).
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

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(text);
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
