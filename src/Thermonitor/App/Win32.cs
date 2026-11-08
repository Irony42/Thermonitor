using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Thermonitor.App;

/// <summary>Small Win32 helpers for the app window.</summary>
internal static class Win32
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int SwRestore = 9;

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

    /// <summary>Brings the window of another running Thermonitor to the front. False if there is none.</summary>
    public static bool FocusOtherInstance()
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("Thermonitor"))
        {
            using (p)
            {
                if (p.Id == me || p.MainWindowHandle == IntPtr.Zero) continue;
                _ = ShowWindow(p.MainWindowHandle, SwRestore);
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
}
