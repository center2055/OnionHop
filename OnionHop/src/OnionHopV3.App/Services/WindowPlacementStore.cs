using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OnionHopV3.Core.Platform;

namespace OnionHopV3.App.Services;

/// <summary>
/// Where the main window was and how big: position in screen pixels, size in the window's own
/// (scaled) units, as Avalonia has them. The position and size are those of the normal window, also
/// while it is maximized, so un-maximizing after a restart gives back the old window.
/// </summary>
internal sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>A screen's working area in screen pixels, and its scaling (1.5 at 150 percent).</summary>
internal readonly record struct ScreenArea(int X, int Y, int Width, int Height, double Scaling);

/// <summary>What to open the window with. Without a position it is centered as before.</summary>
internal sealed record ResolvedWindowPlacement(double Width, double Height, int? X, int? Y, bool Maximized);

/// <summary>
/// Remembers the main window's position, size and maximized state between runs. It has its own
/// small file next to settings.json: it changes with every move of the window, belongs to this
/// computer's screens and is no setting to export, import or reset.
/// </summary>
internal static class WindowPlacementStore
{
    private static readonly object Gate = new();

    internal static string DefaultPath => Path.Combine(AppDirectories.RoamingData, "OnionHop", "window.json");

    public static WindowPlacement? Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
            return placement is { Width: > 0, Height: > 0 }
                   && double.IsFinite(placement.Width)
                   && double.IsFinite(placement.Height)
                ? placement
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WindowPlacement placement, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Written to the side and moved into place, so a kill in the middle leaves the old file.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(placement));
                File.Move(temp, path, overwrite: true);
            }
        }
        catch
        {
            // Not worth interrupting anything for: the window opens centered next time.
        }
    }

    /// <summary>
    /// Turns a saved placement into one that works on the screens connected now. The size is kept
    /// within the minimum and the screen. The position is kept only if the window's top middle, where
    /// its caption buttons and drag strip are, lands on a screen: a second monitor that is gone or a
    /// changed layout would otherwise open the window where nobody can reach it. The window is then
    /// moved fully onto that screen. With no usable position it opens centered.
    /// </summary>
    internal static ResolvedWindowPlacement Resolve(
        WindowPlacement saved,
        IReadOnlyList<ScreenArea> screens,
        double minWidth,
        double minHeight)
    {
        var width = Math.Max(saved.Width, minWidth);
        var height = Math.Max(saved.Height, minHeight);

        foreach (var screen in screens)
        {
            // No larger than this screen, measured before asking whether it is on it.
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var fittedWidth = Math.Min(width, screen.Width / scaling);
            var fittedHeight = Math.Min(height, screen.Height / scaling);
            var pixelWidth = (int)Math.Round(fittedWidth * scaling);
            var pixelHeight = (int)Math.Round(fittedHeight * scaling);

            var topMiddleX = saved.X + pixelWidth / 2;
            var topMiddleY = saved.Y + (int)Math.Round(18 * scaling);
            var onScreen = topMiddleX >= screen.X && topMiddleX < screen.X + screen.Width
                           && topMiddleY >= screen.Y && topMiddleY < screen.Y + screen.Height;
            if (!onScreen)
            {
                continue;
            }

            // Moved so that all of it is on the screen.
            var x = Math.Clamp(saved.X, screen.X, Math.Max(screen.X, screen.X + screen.Width - pixelWidth));
            var y = Math.Clamp(saved.Y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - pixelHeight));
            return new ResolvedWindowPlacement(fittedWidth, fittedHeight, x, y, saved.Maximized);
        }

        return new ResolvedWindowPlacement(width, height, null, null, saved.Maximized);
    }
}
