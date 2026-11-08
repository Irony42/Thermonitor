namespace Thermonitor.App;

/// <summary>Command-line flags.</summary>
/// <param name="Server">Run only the sensor server (the elevated child).</param>
/// <param name="Takeover">Wait for the port: an older instance is stepping down for this one.</param>
/// <param name="NoWindow">No window (<c>--no-window</c>, or <c>--no-browser</c> from earlier versions).</param>
/// <param name="NoElevate">Never ask for admin rights (limited sensors).</param>
/// <param name="KeepAlive">The server keeps running when no window is connected, or after the window closed.</param>
internal sealed record LaunchOptions(bool Server, bool Takeover, bool NoWindow, bool NoElevate, bool KeepAlive)
{
    public static LaunchOptions Parse(IEnumerable<string> args)
    {
        var f = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        return new LaunchOptions(
            Server: f.Contains("--server"),
            Takeover: f.Contains("--takeover"),
            NoWindow: f.Contains("--no-window") || f.Contains("--no-browser"),
            NoElevate: f.Contains("--no-elevate"),
            KeepAlive: f.Contains("--keep-alive"));
    }

    /// <summary>Arguments for the elevated server child.</summary>
    public string ElevatedServerArgs => "--server --no-window" + (KeepAlive ? " --keep-alive" : "");
}

internal enum ServerPlan
{
    /// <summary>A server already answers on the port: use it, leave it running afterwards.</summary>
    Reuse,

    /// <summary>Run the server inside this process (already admin, limited mode, or the elevated child itself).</summary>
    InProcess,

    /// <summary>Start an elevated copy (UAC) as the server.</summary>
    Elevated,
}

internal enum WindowPlan
{
    None,

    /// <summary>Show the WebView2 window in this (un-elevated) process.</summary>
    Here,

    /// <summary>
    /// This process is elevated: a web engine must not run here, so ask the shell to start an
    /// un-elevated copy, which finds the server running and shows the window.
    /// </summary>
    Unelevated,
}

/// <summary>What a launch does. Pure decision, so it can be tested without UAC prompts or windows.</summary>
/// <param name="StopServerOnClose">Ask the server to quit when the window closes (a server this launch started elsewhere).</param>
internal sealed record LaunchPlan(ServerPlan Server, WindowPlan Window, bool StopServerOnClose)
{
    public static LaunchPlan Decide(LaunchOptions o, bool isAdmin, bool serverRunning)
    {
        var wantsWindow = !o.Server && !o.NoWindow;
        var window = !wantsWindow ? WindowPlan.None : isAdmin ? WindowPlan.Unelevated : WindowPlan.Here;

        if (serverRunning && !o.Takeover)
            return new LaunchPlan(ServerPlan.Reuse, window, StopServerOnClose: false);
        if (o.Server)
            return new LaunchPlan(ServerPlan.InProcess, WindowPlan.None, StopServerOnClose: false);
        if (isAdmin || o.NoElevate)
            return new LaunchPlan(ServerPlan.InProcess, window, StopServerOnClose: false);
        // Sensors need admin rights, the window must not have them: an elevated copy serves.
        // With --keep-alive the server outlives the window; without a window this process just exits.
        return new LaunchPlan(ServerPlan.Elevated, window, StopServerOnClose: window == WindowPlan.Here && !o.KeepAlive);
    }
}
