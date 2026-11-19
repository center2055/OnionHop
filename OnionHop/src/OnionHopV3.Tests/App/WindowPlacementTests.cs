using System;
using System.Collections.Generic;
using System.IO;
using OnionHopV3.App.Services;
using Xunit;

namespace OnionHopV3.Tests.App;

/// <summary>
/// The main window opened centered at its default size on every start. Its position, size and
/// maximized state are now remembered, and put back only where the window can be reached.
/// </summary>
public sealed class WindowPlacementTests : IDisposable
{
    private const double MinWidth = 1040;
    private const double MinHeight = 700;

    private static readonly ScreenArea Primary = new(0, 0, 1920, 1040, 1.0);
    private static readonly ScreenArea RightOfPrimary = new(1920, 0, 2560, 1400, 1.0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onionhop-window-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void A_saved_window_comes_back_where_it_was()
    {
        var saved = new WindowPlacement(300, 120, 1400, 900, false);

        var placement = WindowPlacementStore.Resolve(saved, [Primary], MinWidth, MinHeight);

        Assert.Equal(new ResolvedWindowPlacement(1400, 900, 300, 120, false), placement);
    }

    [Fact]
    public void A_window_on_a_second_monitor_stays_there_and_keeps_being_maximized()
    {
        var saved = new WindowPlacement(2200, 200, 1500, 1000, true);

        var placement = WindowPlacementStore.Resolve(saved, [Primary, RightOfPrimary], MinWidth, MinHeight);

        Assert.Equal(new ResolvedWindowPlacement(1500, 1000, 2200, 200, true), placement);
    }

    // The second monitor is gone: the size is kept, the position is not, so it opens centered.
    [Fact]
    public void A_window_on_a_monitor_that_is_gone_opens_centered()
    {
        var saved = new WindowPlacement(2200, 200, 1500, 1000, false);

        var placement = WindowPlacementStore.Resolve(saved, [Primary], MinWidth, MinHeight);

        Assert.Equal(new ResolvedWindowPlacement(1500, 1000, null, null, false), placement);
    }

    // The window has no title bar of its own: its caption buttons and drag strip are at its top,
    // so a window hanging off an edge is moved back onto the screen.
    [Fact]
    public void A_window_hanging_off_the_screen_is_moved_onto_it()
    {
        var saved = new WindowPlacement(1000, 500, 1400, 900, false);

        var placement = WindowPlacementStore.Resolve(saved, [Primary], MinWidth, MinHeight);

        Assert.Equal(1920 - 1400, placement.X);
        Assert.Equal(1040 - 900, placement.Y);
    }

    [Fact]
    public void The_size_stays_between_the_minimum_and_the_screen()
    {
        var tooSmall = WindowPlacementStore.Resolve(new WindowPlacement(10, 10, 200, 100, false), [Primary], MinWidth, MinHeight);
        Assert.Equal(MinWidth, tooSmall.Width);
        Assert.Equal(MinHeight, tooSmall.Height);

        var tooLarge = WindowPlacementStore.Resolve(new WindowPlacement(0, 0, 5000, 4000, false), [Primary], MinWidth, MinHeight);
        Assert.Equal(1920, tooLarge.Width);
        Assert.Equal(1040, tooLarge.Height);
        Assert.Equal(0, tooLarge.X);
        Assert.Equal(0, tooLarge.Y);
    }

    // At 150 percent a 1280 wide window covers 1920 screen pixels.
    [Fact]
    public void Scaled_screens_count_in_screen_pixels()
    {
        var scaled = new ScreenArea(0, 0, 2880, 1700, 1.5);
        var saved = new WindowPlacement(1200, 100, 1280, 840, false);

        var placement = WindowPlacementStore.Resolve(saved, [scaled], MinWidth, MinHeight);

        Assert.Equal(1280, placement.Width);
        Assert.Equal(2880 - 1920, placement.X);
        Assert.Equal(100, placement.Y);
    }

    [Fact]
    public void It_survives_a_round_trip_and_ignores_a_broken_file()
    {
        var path = Path.Combine(_dir, "window.json");
        Assert.Null(WindowPlacementStore.Load(path));

        var placement = new WindowPlacement(-1800, 40, 1333.5, 801, true);
        WindowPlacementStore.Save(placement, path);
        Assert.Equal(placement, WindowPlacementStore.Load(path));

        File.WriteAllText(path, "{ not json");
        Assert.Null(WindowPlacementStore.Load(path));

        File.WriteAllText(path, """{"X":0,"Y":0,"Width":0,"Height":-5,"Maximized":false}""");
        Assert.Null(WindowPlacementStore.Load(path));
    }
}
