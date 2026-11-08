using System.Text.Json;

namespace Thermonitor.App;

/// <summary>Window position and size in screen pixels.</summary>
internal sealed record WindowBounds(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>Remembers where the window was, and never restores it off-screen (unplugged monitor...).</summary>
/// <remarks>The process is per-monitor DPI aware: everything is in physical pixels, sizes are scaled by the monitor's DPI.</remarks>
internal static class WindowPlacement
{
    public const int MinWidth = 720;     // at 100 % scale
    public const int MinHeight = 480;
    private const int DefaultWidth = 1440;
    private const int DefaultHeight = 940;
    private const int TitleBarHeight = 32;
    private const int MinVisible = 120;   // px of title bar that must stay grabbable

    /// <param name="preferred">Work area to centre a new window in (where the user launched it from).</param>
    /// <param name="scale">Display scale of that area (1.0 = 100 %, 2.0 = 200 %).</param>
    public static WindowBounds Fit(WindowBounds? saved, IReadOnlyList<Rectangle> workAreas, Rectangle preferred, double scale = 1.0)
    {
        var minW = (int)Math.Round(MinWidth * scale);
        var minH = (int)Math.Round(MinHeight * scale);

        if (saved is not null)
        {
            // The screen holding most of the title bar is the one the window is "on".
            var titleBar = new Rectangle(saved.X, saved.Y, Math.Max(minW, saved.Width), TitleBarHeight);
            var best = workAreas
                .Select(area => (area, visible: Rectangle.Intersect(area, titleBar)))
                .Where(x => x.visible.Width >= MinVisible && x.visible.Height >= TitleBarHeight / 2)
                .OrderByDescending(x => x.visible.Width)
                .Select(x => (Rectangle?)x.area)
                .FirstOrDefault();
            if (best is { } area)
            {
                // Never bigger than that screen (saved on a larger monitor), never below the minimum.
                var w = Math.Clamp(saved.Width, Math.Min(minW, area.Width), area.Width);
                var h = Math.Clamp(saved.Height, Math.Min(minH, area.Height), area.Height);
                return saved with { Width = w, Height = h };
            }
        }

        // Default: centred on the preferred screen, shrunk if that screen is small.
        var width = Math.Max(Math.Min(minW, preferred.Width), Math.Min((int)Math.Round(DefaultWidth * scale), preferred.Width - 40));
        var height = Math.Max(Math.Min(minH, preferred.Height), Math.Min((int)Math.Round(DefaultHeight * scale), preferred.Height - 40));
        return new WindowBounds(
            preferred.X + (preferred.Width - width) / 2,
            preferred.Y + (preferred.Height - height) / 2,
            width, height, Maximized: false);
    }

    public static WindowBounds? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;   // unreadable: fall back to the default placement
        }
    }

    public static void Save(string path, WindowBounds bounds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(bounds));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not worth bothering the user: next launch uses the default placement
        }
    }
}
