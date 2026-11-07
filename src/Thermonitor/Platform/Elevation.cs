using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Thermonitor.Platform;

internal static class Elevation
{
    private const int UserCancelled = 1223;   // ERROR_CANCELLED: "No" on the UAC prompt

    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts this exe again with admin rights (UAC prompt). False if the user said no.</summary>
    public static bool TryRelaunch(string args)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (Win32Exception ex)
        {
            Log.Write(ex.NativeErrorCode == UserCancelled
                ? "elevation refused by the user"
                : $"elevation failed ({ex.NativeErrorCode}): {ex.Message}");
            return false;
        }
    }
}
