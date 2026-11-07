using System.Diagnostics;
using Microsoft.Win32;

namespace Thermonitor.Platform;

internal static class Browser
{
    public static void Open(string url, bool elevated)
    {
        try
        {
            if (elevated)
            {
                // Never start the browser itself elevated: let the (un-elevated) shell do it.
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false });
                return;
            }

            var app = FindAppBrowser();
            if (app is not null)
            {
                Process.Start(new ProcessStartInfo(app, $"--app={url} --window-size=1440,940") { UseShellExecute = false });
                return;
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("browser: " + ex.Message);
        }
    }

    // Chrome or Edge in "app" mode gives a clean window with no tabs or address bar.
    private static string? FindAppBrowser()
    {
        foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string path && File.Exists(path)) return path;
        }
        return null;
    }
}
