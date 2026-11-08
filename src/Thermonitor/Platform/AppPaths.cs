namespace Thermonitor.Platform;

/// <summary>Per-user files, all under %LOCALAPPDATA%\Thermonitor.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thermonitor");

    public static string LogFile => Path.Combine(Root, "thermonitor.log");

    /// <summary>WebView2 profile (cache, local storage). Never next to the exe, which may be read-only.</summary>
    public static string WebViewData => Path.Combine(Root, "WebView2");

    /// <summary>Last window position and size.</summary>
    public static string WindowFile => Path.Combine(Root, "window.json");
}
