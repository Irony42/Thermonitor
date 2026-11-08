using Thermonitor.App;
using Thermonitor.Platform;

namespace Thermonitor;

internal static class Program
{
    private const string Url = "http://localhost:8787/";
    private const string SingleWindowMutex = @"Local\Thermonitor.Window";

    // Flags:
    //   --server      run only the sensor server (the elevated child)
    //   --takeover    wait for the port to free up (an older instance is stepping down)
    //   --no-window   no window (alias: --no-browser)
    //   --no-elevate  never ask for admin rights (limited sensors)
    //   --keep-alive  the server does not quit when the last window closes
    [STAThread]
    private static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        Log.Init();
        Log.Write($"start args=[{string.Join(' ', args)}] admin={Elevation.IsAdmin}");

        // One window at a time: a second launch (Start menu, pinned icon) brings the first one back.
        var wantsWindow = !options.Server && !options.NoWindow;
        var firstWindow = true;
        using var single = wantsWindow ? new Mutex(initiallyOwned: true, SingleWindowMutex, out firstWindow) : null;
        if (wantsWindow && !firstWindow && Win32.FocusOtherInstance()) return 0;

        var running = !options.Takeover && Sync(() => LocalInstance.PingAsync(Url));
        var plan = LaunchPlan.Decide(options, Elevation.IsAdmin, running);
        Log.Write($"plan: {plan}");

        HostedServer? local = null;
        var stopRemote = false;
        switch (plan.Server)
        {
            case ServerPlan.Elevated:
                var childArgs = "--server --no-window" + (options.KeepAlive ? " --keep-alive" : "");
                if (Elevation.TryRelaunch(childArgs) && Sync(() => LocalInstance.WaitForAsync(Url, TimeSpan.FromSeconds(30))))
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

        if (!plan.Window)
        {
            local?.Completion.GetAwaiter().GetResult();
        }
        else if (WebViewLoader.TryPrepare())
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainWindow(Url));
            // Closed: stop the elevated server now instead of waiting for its idle timeout. Also when the
            // in-process server already stepped down for an elevated copy ("Relancer en administrateur").
            if (stopRemote || local?.Completion.IsCompleted == true) Sync(() => LocalInstance.RequestQuitAsync(Url));
        }
        else
        {
            // No usable WebView2: fall back to a Chrome / Edge app window, the server quits on its own.
            Log.Write("opening the browser instead of the app window");
            Browser.Open(Url, Elevation.IsAdmin);
            local?.Completion.GetAwaiter().GetResult();
        }

        local?.Dispose();
        Log.Write("exit");
        return 0;
    }

    // Main is a plain STA thread for WinForms: run async work on the thread pool, never on a UI context.
    private static T Sync<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
}
