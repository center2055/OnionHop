using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace OnionHopV3.App.Services;

public static class ClipboardHelper
{
    public static async Task SetTextAsync(Control control, string text)
        => await SetTextAsync(control, text, clearAfterDelay: false);

    public static async Task SetTextAsync(Control control, string text, bool clearAfterDelay)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var isRoot = string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase);
                string? consoleUser = null;

                if (isRoot)
                {
                    var userProbe = new ProcessStartInfo("/usr/bin/stat", "-f %Su /dev/console")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    var userProcess = Process.Start(userProbe);
                    if (userProcess != null)
                    {
                        consoleUser = (await userProcess.StandardOutput.ReadToEndAsync()).Trim();
                        await userProcess.WaitForExitAsync();
                    }
                }

                ProcessStartInfo clipboardProcess;
                if (isRoot && !string.IsNullOrWhiteSpace(consoleUser) && consoleUser != "root")
                {
                    clipboardProcess = new ProcessStartInfo("/usr/bin/su", $"{consoleUser} -c /usr/bin/pbcopy")
                    {
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                }
                else
                {
                    clipboardProcess = new ProcessStartInfo("/usr/bin/pbcopy")
                    {
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                }

                var process = Process.Start(clipboardProcess);
                if (process != null)
                {
                    await process.StandardInput.WriteAsync(text);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync();
                }

                if (clearAfterDelay)
                {
                    _ = ClearMacClipboardIfUnchangedAsync(text);
                }

                return;
            }
            catch
            {
                return;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var tool = File.Exists("/usr/bin/wl-copy") ? "wl-copy" : "xclip";
                var arguments = tool == "xclip" ? "-selection clipboard" : string.Empty;
                var process = Process.Start(new ProcessStartInfo(tool, arguments)
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    await process.StandardInput.WriteAsync(text);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync();
                }

                if (clearAfterDelay)
                {
                    _ = ClearLinuxClipboardIfUnchangedAsync(text);
                }
            }
            catch
            {
            }

            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(control);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
                if (clearAfterDelay)
                {
                    _ = ClearAvaloniaClipboardIfUnchangedAsync(topLevel, text);
                }
            }
        }
        catch
        {
        }
    }

    private static async Task ClearAvaloniaClipboardIfUnchangedAsync(TopLevel topLevel, string originalText)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (topLevel.Clipboard == null)
            {
                return;
            }

            var current = await topLevel.Clipboard.GetTextAsync();
            if (string.Equals(current, originalText, StringComparison.Ordinal))
            {
                await topLevel.Clipboard.SetTextAsync(string.Empty);
            }
        }
        catch
        {
        }
    }

    private static async Task ClearMacClipboardIfUnchangedAsync(string originalText)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            var process = Process.Start(new ProcessStartInfo("/usr/bin/pbpaste")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null)
            {
                return;
            }

            var current = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (string.Equals(current, originalText, StringComparison.Ordinal))
            {
                var clearProcess = Process.Start(new ProcessStartInfo("/usr/bin/pbcopy")
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (clearProcess != null)
                {
                    clearProcess.StandardInput.Close();
                    await clearProcess.WaitForExitAsync();
                }
            }
        }
        catch
        {
        }
    }

    private static async Task ClearLinuxClipboardIfUnchangedAsync(string originalText)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            var isWayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            var readTool = isWayland && File.Exists("/usr/bin/wl-paste") ? "wl-paste" : "xclip";
            var readArgs = readTool == "xclip" ? "-selection clipboard -o" : string.Empty;
            var readProcess = Process.Start(new ProcessStartInfo(readTool, readArgs)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (readProcess == null)
            {
                return;
            }

            var current = await readProcess.StandardOutput.ReadToEndAsync();
            await readProcess.WaitForExitAsync();
            if (!string.Equals(current, originalText, StringComparison.Ordinal))
            {
                return;
            }

            var writeTool = isWayland && File.Exists("/usr/bin/wl-copy") ? "wl-copy" : "xclip";
            var writeArgs = writeTool == "xclip" ? "-selection clipboard" : string.Empty;
            var writeProcess = Process.Start(new ProcessStartInfo(writeTool, writeArgs)
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (writeProcess != null)
            {
                writeProcess.StandardInput.Close();
                await writeProcess.WaitForExitAsync();
            }
        }
        catch
        {
        }
    }
}
