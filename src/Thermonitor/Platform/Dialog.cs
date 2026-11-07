using System.Runtime.InteropServices;

namespace Thermonitor.Platform;

/// <summary>The app has no console: fatal start-up problems are shown in a Windows message box.</summary>
internal static partial class Dialog
{
    private const uint IconError = 0x10;

    public static void Error(string title, string message)
    {
        Log.Write($"{title}: {message}");
        try { _ = MessageBoxW(0, message, title, IconError); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* headless */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
