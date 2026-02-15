using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using OnionHopV2.App.ViewModels;

namespace OnionHopV2.App.Views;

public partial class LogsPageView : UserControl
{
    public LogsPageView()
    {
        InitializeComponent();
    }

    private AppStateViewModel? State => (DataContext as PageViewModelBase)?.State;

    private async void OnCopyCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = GetCurrentLogText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard == null)
        {
            return;
        }

        await topLevel.Clipboard.SetTextAsync(text);
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

        var name = GetSelectedTabName() ?? "logs";
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export logs",
            SuggestedFileName = $"onionhop-{name.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
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

    private string GetCurrentLogText()
    {
        var state = State;
        if (state == null)
        {
            return string.Empty;
        }

        var tab = GetSelectedTabName();
        var lines = string.Equals(tab, "DNS", StringComparison.OrdinalIgnoreCase)
            ? state.DnsLogLines
            : state.LogLines;

        return string.Join(Environment.NewLine, lines);
    }

    private string? GetSelectedTabName()
    {
        if (LogsTabs?.SelectedItem is TabItem item && item.Header is string header)
        {
            return header;
        }

        return null;
    }
}
