using Thermonitor.App;
using Thermonitor.Platform;

namespace Thermonitor;

internal static class Program
{
    private const string Url = "http://localhost:8787/";

    // Flags:
    //   --server      run only the sensor server (the elevated child)
    //   --takeover    wait for the port to free up (an older instance is stepping down)
    //   --no-window   no window (alias: --no-browser)
    //   --no-elevate  never ask for admin rights (limited sensors)
    //   --keep-alive  the server keeps running when the window closes
    [STAThread]
    private static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        Log.Init();
        Log.Write($"start args=[{string.Join(' ', args)}] admin={Elevation.IsAdmin}");

        // One window at a time. Taken before looking at the server: the previous window's process may
        // still be stopping it.
        var windowHere = !options.Server && !options.NoWindow && !Elevation.IsAdmin;
        using var windowLock = windowHere ? WindowLock.Acquire(TimeSpan.FromSeconds(60)) : null;
        if (windowHere && windowLock is null) return 0;

        var running = !options.Takeover && Sync(() => LocalInstance.PingAsync(Url));
        var plan = LaunchPlan.Decide(options, Elevation.IsAdmin, running);
        Log.Write($"plan: {plan}");

        HostedServer? local = null;
        var stopRemote = false;
        switch (plan.Server)
        {
            case ServerPlan.Elevated:
                if (Elevation.TryRelaunch(options.ElevatedServerArgs) && Sync(() => LocalInstance.WaitForAsync(Url, TimeSpan.FromSeconds(30))))
                {
                    stopRemote = plan.StopServerOnClose;
                    break;
                }
                Log.Write("no elevated server, falling back to limited mode");
                goto case ServerPlan.InProcess;
            case ServerPlan.InProcess:
                local = Sync(() => HostedServer.StartAsync(Url, options));
                if (local is null)
                {
                    Dialog.Error("Thermonitor", $"Le port {new Uri(Url).Port} est déjà utilisé par une autre application.\n\nFerme-la puis relance Thermonitor.");
                    return 1;
                }
                break;
        }

        switch (plan.Window)
        {
            case WindowPlan.None:
                local?.Completion.GetAwaiter().GetResult();   // a server-only process lives as long as its server
                break;

            case WindowPlan.Unelevated:
                Elevation.StartUnelevated(Environment.ProcessPath!);
                local?.Completion.GetAwaiter().GetResult();
                break;

            case WindowPlan.Here when !ShowWindow():
                // WebView2 missing or failing: a Chrome / Edge app window instead; the server quits on its own.
                Browser.Open(Url, elevated: false);
                local?.Completion.GetAwaiter().GetResult();
                break;

            case WindowPlan.Here:
                if (options.KeepAlive)
                {
                    local?.Completion.GetAwaiter().GetResult();   // the server outlives the window
                    break;
                }
                // Stop the server this launch started elsewhere, instead of waiting for its idle timeout:
                // the elevated child, or the elevated copy that took over from the in-process server.
                if (stopRemote || local?.SteppedDown == true) Sync(() => LocalInstance.RequestQuitAsync(Url));
                break;
        }

        local?.Dispose();
        Log.Write("exit");
        return 0;
    }

    /// <summary>Runs the window until it closes. False when WebView2 could not show the page.</summary>
    private static bool ShowWindow()
    {
        if (!WebViewLoader.TryPrepare()) return false;
        ApplicationConfiguration.Initialize();
        using var window = new MainWindow(Url);
        Application.Run(window);
        return !window.WebViewFailed;
    }

    // Main is a plain STA thread for WinForms: run async work on the thread pool, never on a UI context.
    private static T Sync<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
}
