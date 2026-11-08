using System.Drawing;
using Thermonitor.App;

namespace Thermonitor.Tests;

public class WindowPlacementTests
{
    private static readonly Rectangle Primary = new(0, 0, 3840, 2112);        // 4K minus taskbar
    private static readonly Rectangle Secondary = new(3840, 0, 1920, 1032);
    private static readonly Rectangle[] Both = [Primary, Secondary];

    [Fact]
    public void First_launch_is_centred_on_the_primary_screen()
    {
        var b = WindowPlacement.Fit(null, Both, Primary);

        Assert.Equal(new WindowBounds(1200, 586, 1440, 940, false), b);
    }

    [Fact]
    public void A_visible_saved_position_is_restored()
    {
        var saved = new WindowBounds(4000, 100, 1200, 800, Maximized: true);

        Assert.Equal(saved, WindowPlacement.Fit(saved, Both, Primary));
    }

    [Fact]
    public void A_window_left_on_an_unplugged_screen_comes_back_to_the_primary()
    {
        var saved = new WindowBounds(4000, 100, 1200, 800, false);

        var b = WindowPlacement.Fit(saved, [Primary], Primary);

        Assert.Equal(new WindowBounds(1200, 586, 1440, 940, false), b);
    }

    [Fact]
    public void A_title_bar_barely_on_screen_does_not_count()
    {
        var saved = new WindowBounds(3800, 100, 1200, 800, false);   // 40 px left on the primary

        var b = WindowPlacement.Fit(saved, [Primary], Primary);

        Assert.NotEqual(saved, b);
    }

    [Fact]
    public void Saved_sizes_never_go_below_the_minimum()
    {
        var b = WindowPlacement.Fit(new WindowBounds(10, 10, 200, 100, false), Both, Primary);

        Assert.Equal(WindowPlacement.MinWidth, b.Width);
        Assert.Equal(WindowPlacement.MinHeight, b.Height);
    }

    [Fact]
    public void The_default_shrinks_on_a_small_screen()
    {
        var small = new Rectangle(0, 0, 1280, 680);

        var b = WindowPlacement.Fit(null, [small], small);

        Assert.True(b.Width <= 1240 && b.Height <= 640);
        Assert.True(b.X >= 0 && b.Y >= 0);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"thermonitor-window-{Guid.NewGuid():N}.json");
        try
        {
            var b = new WindowBounds(12, 34, 1500, 900, true);
            WindowPlacement.Save(path, b);
            Assert.Equal(b, WindowPlacement.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_corrupt_or_missing_file_gives_no_placement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"thermonitor-window-{Guid.NewGuid():N}.json");
        Assert.Null(WindowPlacement.Load(path));
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(WindowPlacement.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
