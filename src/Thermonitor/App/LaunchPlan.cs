namespace Thermonitor.App;

/// <summary>Command-line flags.</summary>
/// <param name="Server">Run only the sensor server (the elevated child).</param>
/// <param name="Takeover">Wait for the port: an older instance is stepping down for this one.</param>
/// <param name="NoWindow">No window (<c>--no-window</c>, or <c>--no-browser</c> from earlier versions).</param>
/// <param name="NoElevate">Never ask for admin rights (limited sensors).</param>
/// <param name="KeepAlive">The server keeps running when no window is connected.</param>
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
}

internal enum ServerPlan
{
    /// <summary>A server already answers on the port: use it, leave it running afterwards.</summary>
    Reuse,

    /// <summary>Run the server inside this process (already admin, limited mode, or the elevated child itself).</summary>
    InProcess,

    /// <summary>Start an elevated copy (UAC) as the server; this process only shows the window.</summary>
    Elevated,
}

/// <summary>What a launch does. Pure decision, so it can be tested without UAC prompts or windows.</summary>
/// <param name="StopServerOnClose">Ask the server to quit when the window closes (a server this launch started elsewhere).</param>
internal sealed record LaunchPlan(ServerPlan Server, bool Window, bool StopServerOnClose)
{
    public static LaunchPlan Decide(LaunchOptions o, bool isAdmin, bool serverRunning)
    {
        if (serverRunning && !o.Takeover)
            return new LaunchPlan(ServerPlan.Reuse, Window: !o.Server && !o.NoWindow, StopServerOnClose: false);
        if (o.Server || o.NoWindow)
            return new LaunchPlan(ServerPlan.InProcess, Window: false, StopServerOnClose: false);
        // Sensors need admin rights, the window must not have them: split unless already admin or told not to.
        if (isAdmin || o.NoElevate)
            return new LaunchPlan(ServerPlan.InProcess, Window: true, StopServerOnClose: false);
        return new LaunchPlan(ServerPlan.Elevated, Window: true, StopServerOnClose: true);
    }
}
