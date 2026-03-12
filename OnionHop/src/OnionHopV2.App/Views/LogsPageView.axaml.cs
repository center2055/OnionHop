using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
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

        await CopyToClipboardAsync(text);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (OperatingSystem.IsMacOS())
        {
            // When running as root on macOS, pbcopy writes to root's pasteboard, not the user's.
            // Detect the console user and run pbcopy as them (root can su without password).
            try
            {
                var isRoot = string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase);
                string? consoleUser = null;

                if (isRoot)
                {
                    var cuPsi = new ProcessStartInfo("/usr/bin/stat", "-f %Su /dev/console")
                    {
                        UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                    };
                    var cuProc = Process.Start(cuPsi);
                    if (cuProc != null)
                    {
                        consoleUser = (await cuProc.StandardOutput.ReadToEndAsync()).Trim();
                        await cuProc.WaitForExitAsync();
                    }
                }

                ProcessStartInfo psi;
                if (isRoot && !string.IsNullOrEmpty(consoleUser) && consoleUser != "root")
                {
                    // Run pbcopy as the console user so it writes to their pasteboard.
                    psi = new ProcessStartInfo("/usr/bin/su", $"{consoleUser} -c /usr/bin/pbcopy")
                    {
                        RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true
                    };
                }
                else
                {
                    psi = new ProcessStartInfo("/usr/bin/pbcopy")
                    {
                        RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true
                    };
                }

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.StandardInput.WriteAsync(text);
                    proc.StandardInput.Close();
                    await proc.WaitForExitAsync();
                }

                return;
            }
            catch
            {
            }

            return;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var tool = File.Exists("/usr/bin/wl-copy") ? "wl-copy" : "xclip";
                var args = tool == "xclip" ? "-selection clipboard" : "";
                var psi = new ProcessStartInfo(tool, args) { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.StandardInput.WriteAsync(text);
                    proc.StandardInput.Close();
                    await proc.WaitForExitAsync();
                }
            }
            catch
            {
            }

            return;
        }

        // Windows: use Avalonia clipboard.
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
            }
        }
        catch
        {
        }
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

    private void OnClearCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var state = State;
        if (state == null)
        {
            return;
        }

        var tabIndex = LogsTabs?.SelectedIndex ?? 0;
        switch (tabIndex)
        {
            case 1:
                state.ClearDnsLogs();
                break;
            case 2:
                state.ClearVpnLogs();
                break;
            default:
                state.ClearAppLogs();
                break;
        }
    }

    private string GetCurrentLogText()
    {
        var state = State;
        if (state == null)
        {
            return string.Empty;
        }

        var tabIndex = LogsTabs?.SelectedIndex ?? 0;
        var lines = tabIndex switch
        {
            1 => state.DnsLogLines,
            2 => state.VpnLogLines,
            _ => state.LogLines
        };

        return string.Join(Environment.NewLine, lines);
    }

    private string? GetSelectedTabName()
    {
        var tabIndex = LogsTabs?.SelectedIndex ?? 0;
        return tabIndex switch
        {
            1 => "dns",
            2 => GetTunEngineTabName(),
            _ => "app"
        };
    }

    private string GetTunEngineTabName()
    {
        var state = State;
        if (state != null &&
            string.Equals(state.TunCoreMode, AppStateViewModel.TunCoreXray, StringComparison.OrdinalIgnoreCase))
        {
            return "xray";
        }

        return "sing-box";
    }
}
