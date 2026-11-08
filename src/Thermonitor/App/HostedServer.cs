using Thermonitor.Feed;
using Thermonitor.Platform;
using Thermonitor.Sensors;
using Thermonitor.Web;

namespace Thermonitor.App;

/// <summary>The sensor server running inside this process.</summary>
internal sealed class HostedServer : IDisposable
{
    private readonly SensorHub _hub;
    private readonly WebServer _server;
    private int _disposed;

    private HostedServer(SensorHub hub, WebServer server)
    {
        _hub = hub;
        _server = server;
        // As soon as the server stops (quit, idle, elevated copy taking over), release the hardware:
        // the window may stay open on another server for a long time.
        Completion = Task.Run(server.RunAsync).ContinueWith(_ => ReleaseHardware(), TaskScheduler.Default);
    }

    /// <summary>Completes when the server has stopped and released the sensors.</summary>
    public Task Completion { get; }

    /// <summary>The server stopped because an elevated copy took over ("Relancer en administrateur").</summary>
    public bool SteppedDown => _server.SteppedDownForElevation;

    /// <summary>Null when the port cannot be taken.</summary>
    public static async Task<HostedServer?> StartAsync(string url, LaunchOptions options)
    {
        var hub = new SensorHub(new LhmSensorSource(), Elevation.IsAdmin);
        var server = new WebServer(hub, new WebServerOptions(
            url, options.KeepAlive, DevRoot: Environment.GetEnvironmentVariable("THERMONITOR_WWW")));
        if (!await server.StartAsync(options.Takeover ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(2)))
        {
            hub.Dispose();
            return null;
        }
        hub.Start();
        return new HostedServer(hub, server);
    }

    private void ReleaseHardware()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _hub.Dispose();
    }

    public void Dispose()
    {
        _server.Shutdown("app closing");
        Completion.Wait(TimeSpan.FromSeconds(5));
        ReleaseHardware();
    }
}
