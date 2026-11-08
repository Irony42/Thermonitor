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

    private HostedServer(SensorHub hub, WebServer server, Task run)
    {
        _hub = hub;
        _server = server;
        Completion = run;
    }

    /// <summary>Completes when the server stops (window gone, quit request, elevated copy taking over).</summary>
    public Task Completion { get; }

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
        return new HostedServer(hub, server, Task.Run(server.RunAsync));
    }

    public void Dispose()
    {
        _server.Shutdown("app closing");
        Completion.Wait(TimeSpan.FromSeconds(5));
        _hub.Dispose();
    }
}
