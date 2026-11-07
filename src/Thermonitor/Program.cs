using Thermonitor.Feed;
using Thermonitor.Platform;
using Thermonitor.Sensors;
using Thermonitor.Web;

namespace Thermonitor;

internal static class Program
{
    private const string Url = "http://localhost:8787/";

    // Flags:
    //   --server      run the sensor server (set on the elevated child)
    //   --takeover    wait for the port to free up (an older instance is stepping down)
    //   --no-browser  do not open a window
    //   --no-elevate  never ask for admin rights (limited sensors)
    //   --keep-alive  do not quit when the last window closes
    private static async Task<int> Main(string[] args)
    {
        var flags = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        var asServer = flags.Contains("--server");
        var takeover = flags.Contains("--takeover");
        var openBrowser = !flags.Contains("--no-browser");
        var keepAlive = flags.Contains("--keep-alive");

        Log.Init();
        Log.Write($"start args=[{string.Join(' ', args)}] admin={Elevation.IsAdmin}");

        // Already running: just show it.
        if (!takeover && await LocalInstance.PingAsync(Url))
        {
            if (openBrowser) Browser.Open(Url, Elevation.IsAdmin);
            return 0;
        }

        // Sensors need admin rights: start an elevated server, keep the window un-elevated.
        if (!asServer && !Elevation.IsAdmin && !flags.Contains("--no-elevate"))
        {
            if (Elevation.TryRelaunch("--server --no-browser" + (keepAlive ? " --keep-alive" : "")))
            {
                if (await LocalInstance.WaitForAsync(Url, TimeSpan.FromSeconds(30)))
                {
                    if (openBrowser) Browser.Open(Url, elevated: false);
                    return 0;
                }
                Log.Write("elevated server never answered, falling back to limited mode");
            }
        }

        using var hub = new SensorHub(new LhmSensorSource(), Elevation.IsAdmin);
        var server = new WebServer(hub, new WebServerOptions(
            Url, keepAlive, DevRoot: Environment.GetEnvironmentVariable("THERMONITOR_WWW")));
        if (!await server.StartAsync(takeover ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(2)))
        {
            Dialog.Error("Thermonitor", $"Le port {new Uri(Url).Port} est déjà utilisé par une autre application.\n\nFerme-la puis relance Thermonitor.");
            return 1;
        }

        hub.Start();
        if (openBrowser) Browser.Open(Url, Elevation.IsAdmin);
        await server.RunAsync();
        Log.Write("exit");
        return 0;
    }
}
