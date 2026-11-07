using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Thermonitor.Feed;
using Thermonitor.Platform;

namespace Thermonitor.Web;

/// <param name="Url">Listening prefix, e.g. <c>http://localhost:8787/</c> (localhost only).</param>
/// <param name="KeepAlive">Keep running when no window is connected.</param>
/// <param name="DevRoot">Serve wwwroot from this folder instead of the embedded copy.</param>
internal sealed record WebServerOptions(string Url, bool KeepAlive, string? DevRoot = null)
{
    /// <summary>Quit this long after the last request once a window has been seen.</summary>
    public TimeSpan IdleAfterLastClient { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Quit if no window ever connects within this delay.</summary>
    public TimeSpan IdleAtStartup { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan WatchdogPeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A comment line sent on quiet streams, so a closed window is noticed even when no data flows.</summary>
    public TimeSpan HeartbeatEvery { get; init; } = TimeSpan.FromSeconds(10);

    public bool IsAdmin { get; init; } = Elevation.IsAdmin;

    /// <summary>Starts an elevated copy with these arguments; false when the user refuses.</summary>
    public Func<string, bool> RelaunchElevated { get; init; } = Elevation.TryRelaunch;
}

/// <summary>Tiny localhost-only HTTP server: the page, the SSE feed and two POST actions.</summary>
internal sealed class WebServer(SensorHub hub, WebServerOptions options)
{
    private const string ActionHeader = "X-Thermonitor";
    private const int ClientQueueFrames = 512;
    private static readonly byte[] Heartbeat = ": ping\n\n"u8.ToArray();

    // The page only loads its own files; nobody may frame it (clickjacking on "Relancer en administrateur").
    private static readonly (string Name, string Value)[] SecurityHeaders =
    [
        ("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"),
        ("X-Frame-Options", "DENY"),
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "no-referrer"),
    ];

    private readonly CancellationTokenSource _cts = new();
    private readonly StaticFiles _files = new(typeof(WebServer).Assembly, options.DevRoot);
    private readonly string _origin = options.Url.TrimEnd('/');
    private readonly string _authority = new Uri(options.Url).Authority;
    private HttpListener? _listener;
    private int _clients;
    private long _lastActivity = Environment.TickCount64;
    private volatile bool _everConnected;

    public async Task<bool> StartAsync(TimeSpan retryFor)
    {
        var deadline = Environment.TickCount64 + (long)retryFor.TotalMilliseconds;
        while (true)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(options.Url);
            try
            {
                listener.Start();
                _listener = listener;
                Touch();
                Log.Write("listening on " + options.Url);
                return true;
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                if (Environment.TickCount64 > deadline)
                {
                    Log.Write($"cannot listen on {options.Url}: {ex.Message}");
                    return false;
                }
                await Task.Delay(250);
            }
        }
    }

    /// <summary>Serves until <see cref="Shutdown"/> or until no window has been open for a while.</summary>
    public async Task RunAsync()
    {
        var listener = _listener ?? throw new InvalidOperationException("StartAsync() first");
        _ = WatchdogAsync();
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                if (_cts.IsCancellationRequested || !listener.IsListening) break;
                Log.Write("accept: " + ex.Message);   // one bad connection, keep serving
                continue;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
        listener.Stop();
        listener.Close();
    }

    public void Shutdown(string why)
    {
        if (_cts.IsCancellationRequested) return;
        Log.Write("shutdown: " + why);
        _cts.Cancel();
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    private async Task WatchdogAsync()
    {
        if (options.KeepAlive) return;
        try
        {
            while (true)
            {
                await Task.Delay(options.WatchdogPeriod, _cts.Token);
                if (Volatile.Read(ref _clients) > 0) continue;
                var idle = Environment.TickCount64 - Interlocked.Read(ref _lastActivity);
                var limit = _everConnected ? options.IdleAfterLastClient : options.IdleAtStartup;
                if (idle <= limit.TotalMilliseconds) continue;
                Shutdown("no window open");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // server stopping
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        Touch();   // a window about to connect (ping, page load) keeps the server up
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            foreach (var (name, value) in SecurityHeaders) res.Headers[name] = value;
            var path = req.Url?.AbsolutePath ?? "/";

            if (!string.Equals(req.Url?.Authority, _authority, StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 421;   // only ever answer to http://localhost:<port>
                return;
            }
            if (path.StartsWith("/api/", StringComparison.Ordinal) && IsCrossSite(req))
            {
                res.StatusCode = 403;
                return;
            }

            switch (req.HttpMethod)
            {
                case "GET" when path == "/api/stream":
                    await StreamAsync(res);
                    break;
                case "GET" when path == "/api/ping":
                    await SendAsync(res, 200, "application/json", """{"app":"Thermonitor"}"""u8.ToArray());
                    break;
                case "GET" or "HEAD" when _files.TryGet(path, out var body, out var type):
                    await SendAsync(res, 200, type, req.HttpMethod == "HEAD" ? null : body);
                    break;
                case "POST" when req.Headers[ActionHeader] != "1":
                    res.StatusCode = 403;
                    break;
                case "POST" when path == "/api/interval":
                    await SetIntervalAsync(req, res);
                    break;
                case "POST" when path == "/api/elevate":
                    await ElevateAsync(res);
                    break;
                default:
                    res.StatusCode = 404;   // includes OPTIONS: no CORS preflight is ever approved
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // client went away or server stopping
        }
        catch (Exception ex)
        {
            Log.Write("request failed: " + ex);
            TrySetStatus(res, 500);
        }
        finally
        {
            try { res.Close(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { /* client gone */ }
        }
    }

    // Another site may not use the API (CSRF, or making the elevated server build megabyte snapshots).
    // Browsers send Sec-Fetch-Site; Origin covers older ones. Non-browser clients send neither.
    private bool IsCrossSite(HttpListenerRequest req)
    {
        if (req.Headers["Sec-Fetch-Site"] is "cross-site" or "same-site") return true;
        var origin = req.Headers["Origin"];
        return origin is not null && !origin.Equals(_origin, StringComparison.OrdinalIgnoreCase);
    }

    private async Task StreamAsync(HttpListenerResponse res)
    {
        res.StatusCode = 200;
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-store";
        res.SendChunked = true;

        // When this queue fills up (a client that stopped reading), the hub drops the client.
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ClientQueueFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        var first = hub.Subscribe(channel.Writer);
        Interlocked.Increment(ref _clients);
        _everConnected = true;
        try
        {
            var output = res.OutputStream;
            await output.WriteAsync(first, _cts.Token);
            await output.FlushAsync(_cts.Token);
            while (await WaitOrHeartbeatAsync(channel.Reader, output))
            {
                while (channel.Reader.TryRead(out var frame)) await output.WriteAsync(frame, _cts.Token);
                await output.FlushAsync(_cts.Token);
            }
        }
        finally
        {
            hub.Unsubscribe(channel.Writer);
            Touch();   // before the count drops, so the watchdog never sees 0 clients with an old timestamp
            Interlocked.Decrement(ref _clients);
        }
    }

    // True when frames are ready, false when the hub completed the channel. Writes heartbeats meanwhile.
    private async Task<bool> WaitOrHeartbeatAsync(ChannelReader<byte[]> reader, Stream output)
    {
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(options.HeartbeatEvery);
            try
            {
                return await reader.WaitToReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
            {
                await output.WriteAsync(Heartbeat, _cts.Token);   // throws if the window is gone
                await output.FlushAsync(_cts.Token);
            }
        }
    }

    private async Task SetIntervalAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        int ms;
        try
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
            ms = doc.RootElement.GetProperty("ms").GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            await SendAsync(res, 400, "application/json", """{"ok":false,"error":"expected {\"ms\": <int>}"}"""u8.ToArray());
            return;
        }
        hub.SetInterval(ms);
        await SendAsync(res, 200, "application/json", """{"ok":true}"""u8.ToArray());
    }

    private async Task ElevateAsync(HttpListenerResponse res)
    {
        if (options.IsAdmin)
        {
            await SendAsync(res, 200, "application/json", """{"ok":true,"already":true}"""u8.ToArray());
            return;
        }

        // The elevated copy waits for the port, this one steps down, the page reconnects by itself.
        var ok = options.RelaunchElevated("--server --no-browser --takeover" + (options.KeepAlive ? " --keep-alive" : ""));
        await SendAsync(res, 200, "application/json", ok ? """{"ok":true}"""u8.ToArray() : """{"ok":false}"""u8.ToArray());
        if (ok) _ = Task.Delay(300).ContinueWith(_ => Shutdown("elevated instance takes over"), TaskScheduler.Default);
    }

    private static async Task SendAsync(HttpListenerResponse res, int status, string contentType, byte[]? body)
    {
        res.StatusCode = status;
        res.ContentType = contentType;
        res.Headers["Cache-Control"] = "no-store";
        if (body is null) return;   // HEAD: HttpListener refuses a body
        res.ContentLength64 = body.Length;
        await res.OutputStream.WriteAsync(body);
    }

    private static void TrySetStatus(HttpListenerResponse res, int status)
    {
        try { res.StatusCode = status; }
        catch (InvalidOperationException) { /* headers already sent */ }
    }
}
