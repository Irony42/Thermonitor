using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Thermonitor.App;

/// <summary>Small Win32 helpers for the app window.</summary>
internal static class Win32
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int SwRestore = 9;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    /// <summary>Dark or light title bar (Windows 10 2004+ / 11).</summary>
    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    /// <summary>The "app mode" chosen in Windows settings (Personalisation > Colours).</summary>
    public static bool AppsUseDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    /// <summary>Display scale of the monitor at this point (1.0 = 100 %).</summary>
    public static double ScaleAt(Point p)
    {
        var monitor = MonitorFromPoint(p, MonitorDefaultToNearest);
        return GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
    }

    /// <summary>Brings the window of another running Thermonitor to the front. False if there is none (yet).</summary>
    public static bool FocusOtherInstance()
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("Thermonitor"))
        {
            using (p)
            {
                if (p.Id == me || p.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(p.MainWindowHandle)) _ = ShowWindow(p.MainWindowHandle, SwRestore);   // keeps a maximized window maximized
                _ = SetForegroundWindow(p.MainWindowHandle);
                return true;
            }
        }
        return false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
