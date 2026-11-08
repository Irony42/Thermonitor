using System.Text.Json;

namespace Thermonitor.App;

/// <summary>Window position and size in screen pixels.</summary>
internal sealed record WindowBounds(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>Remembers where the window was, and never restores it off-screen (unplugged monitor...).</summary>
internal static class WindowPlacement
{
    public const int MinWidth = 720;
    public const int MinHeight = 480;
    private const int DefaultWidth = 1440;
    private const int DefaultHeight = 940;
    private const int TitleBarHeight = 32;
    private const int MinVisible = 120;   // px of title bar that must stay grabbable

    public static WindowBounds Fit(WindowBounds? saved, IReadOnlyList<Rectangle> workAreas, Rectangle primary)
    {
        if (saved is not null)
        {
            var w = Math.Max(MinWidth, saved.Width);
            var h = Math.Max(MinHeight, saved.Height);
            var titleBar = new Rectangle(saved.X, saved.Y, w, TitleBarHeight);
            foreach (var area in workAreas)
            {
                var visible = Rectangle.Intersect(area, titleBar);
                if (visible.Width >= MinVisible && visible.Height >= TitleBarHeight / 2)
                    return saved with { Width = w, Height = h };
            }
        }

        // Default: centred on the primary screen, shrunk if the screen is small.
        var width = Math.Max(MinWidth, Math.Min(DefaultWidth, primary.Width - 40));
        var height = Math.Max(MinHeight, Math.Min(DefaultHeight, primary.Height - 40));
        return new WindowBounds(
            primary.X + (primary.Width - width) / 2,
            primary.Y + (primary.Height - height) / 2,
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
