using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Thermonitor.Feed;
using Thermonitor.Web;
using static Thermonitor.Tests.TestData;

namespace Thermonitor.Tests;

/// <summary>Runs the real HTTP server on a free localhost port, fed by a fake sensor source.</summary>
public sealed class WebServerTests : IAsyncLifetime
{
    private readonly string _url = $"http://localhost:{FreePort()}/";
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private SensorHub _hub = null!;
    private WebServer _server = null!;
    private Task _run = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _hub = new SensorHub(new FakeSensorSource(), admin: true);
        _hub.Open();
        _hub.Accept(Frame([Sensor("/cpu/0"), Sensor("/gpu/0")], 45f, 50f), 1000);
        _server = new WebServer(_hub, new WebServerOptions(_url, KeepAlive: true)
        {
            RelaunchElevated = _ => throw new InvalidOperationException("a test must never trigger a real UAC prompt"),
        });
        Assert.True(await _server.StartAsync(TimeSpan.FromSeconds(2)));
        _run = _server.RunAsync();
        _http.BaseAddress = new Uri(_url);
    }

    public async Task DisposeAsync()
    {
        _server.Shutdown("test done");
        await _run.WaitAsync(TimeSpan.FromSeconds(5));
        _hub.Dispose();
        _http.Dispose();
    }

    private HttpRequestMessage Action(string path, string? json, bool withHeader = true, string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json ?? "", Encoding.UTF8, "application/json"),
        };
        if (withHeader) req.Headers.Add("X-Thermonitor", "1");
        if (origin is not null) req.Headers.Add("Origin", origin);
        return req;
    }

    [Fact]
    public async Task Serves_the_page_and_its_modules()
    {
        var page = await _http.GetAsync("/");
        var module = await _http.GetAsync("/js/domain.js");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Thermonitor", await page.Content.ReadAsStringAsync());
        Assert.Equal("text/javascript", module.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Head_requests_get_headers_only()
    {
        var res = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Ping_identifies_the_app()
    {
        Assert.Contains("Thermonitor", await _http.GetStringAsync("/api/ping"));
    }

    [Theory]
    [InlineData("/nope")]
    [InlineData("/api/nope")]
    public async Task Unknown_paths_are_404(string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Page_cannot_be_framed_or_load_foreign_code()
    {
        var res = await _http.GetAsync("/");

        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("default-src 'self'", csp);
    }

    [Fact]
    public async Task Cors_preflight_is_never_approved()
    {
        var req = new HttpRequestMessage(HttpMethod.Options, "/api/interval");
        req.Headers.Add("Origin", "https://evil.example");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "x-thermonitor");

        var res = await _http.SendAsync(req);

        Assert.False(res.IsSuccessStatusCode);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task Other_sites_cannot_open_the_feed(string fetchSite)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
        req.Headers.Add("Sec-Fetch-Site", fetchSite);

        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Requests_for_another_host_name_are_refused()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        req.Headers.Host = "evil.example:" + new Uri(_url).Port;   // DNS rebinding

        var res = await _http.SendAsync(req);

        Assert.False(res.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Actions_need_the_custom_header()
    {
        var res = await _http.SendAsync(Action("/api/interval", """{"ms":2000}""", withHeader: false));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(1000, _hub.IntervalMs);
    }

    [Fact]
    public async Task Actions_from_another_origin_are_refused()
    {
        var res = await _http.SendAsync(Action("/api/interval", """{"ms":2000}""", origin: "https://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(1000, _hub.IntervalMs);
    }

    [Fact]
    public async Task Interval_can_be_changed_from_the_page()
    {
        var res = await _http.SendAsync(Action("/api/interval", """{"ms":2000}""", origin: _url.TrimEnd('/')));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2000, _hub.IntervalMs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"rate":2}""")]
    [InlineData("""{"ms":"fast"}""")]
    public async Task Bad_interval_requests_are_400(string body)
    {
        var res = await _http.SendAsync(Action("/api/interval", body));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(1000, _hub.IntervalMs);
    }

    [Fact]
    public async Task Stream_starts_with_the_current_snapshot()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);

        await using var stream = await res.Content.ReadAsStreamAsync();
        var text = await ReadUntilAsync(stream, "\n\nevent: snapshot", "}\n\n");
        var (evt, data) = ParseFrame(text[text.IndexOf("event: snapshot", StringComparison.Ordinal)..]);

        Assert.StartsWith("retry: ", text);
        Assert.Equal("snapshot", evt);
        Assert.Equal(2, data.GetProperty("sensors").GetArrayLength());
    }

    [Fact]
    public async Task Stream_pushes_new_readings()
    {
        using var res = await _http.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await res.Content.ReadAsStreamAsync();
        await ReadUntilAsync(stream, "event: snapshot", "}\n\n");

        _hub.Accept(Frame([Sensor("/cpu/0"), Sensor("/gpu/0")], 46f, 51f), 2000);
        var text = await ReadUntilAsync(stream, "event: tick", "}\n\n");

        var (_, data) = ParseFrame(text[text.IndexOf("event: tick", StringComparison.Ordinal)..]);
        Assert.Equal(46.0, data.GetProperty("v")[0].GetDouble());
    }

    [Fact]
    public async Task Quiet_streams_get_heartbeats()
    {
        await using var other = await OtherServerAsync(o => o with { KeepAlive = true, HeartbeatEvery = TimeSpan.FromMilliseconds(150) });
        using var res = await other.Http.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await res.Content.ReadAsStreamAsync();

        await ReadUntilAsync(stream, "event: snapshot", ": ping\n\n");
    }

    [Fact]
    public async Task Server_quits_when_no_window_ever_connects()
    {
        await using var other = await OtherServerAsync(o => o with { IdleAtStartup = TimeSpan.FromMilliseconds(100) });

        await other.Run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // No readings flow here (the hub is not polling): only the heartbeat can notice the closed window.
    [Fact]
    public async Task Server_quits_after_the_last_window_closes_even_without_data()
    {
        await using var other = await OtherServerAsync(o => o with
        {
            IdleAfterLastClient = TimeSpan.FromMilliseconds(200),
            HeartbeatEvery = TimeSpan.FromMilliseconds(100),
        });
        using (var res = await other.Http.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead))
        await using (var stream = await res.Content.ReadAsStreamAsync())
        {
            await ReadUntilAsync(stream, "event: snapshot", "}\n\n");
            await Task.Delay(600);
            Assert.False(other.Run.IsCompleted, "a connected window keeps the server up");
        }

        await other.Run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Elevate_needs_the_custom_header()
    {
        var calls = 0;
        await using var other = await OtherServerAsync(o => o with { KeepAlive = true, IsAdmin = false, RelaunchElevated = _ => { calls++; return true; } });

        var res = await other.Http.SendAsync(Action("/api/elevate", null, withHeader: false));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Elevate_hands_over_to_the_elevated_copy()
    {
        string? args = null;
        await using var other = await OtherServerAsync(o => o with { KeepAlive = true, IsAdmin = false, RelaunchElevated = a => { args = a; return true; } });

        var res = await other.Http.SendAsync(Action("/api/elevate", null));

        Assert.Contains("\"ok\":true", await res.Content.ReadAsStringAsync());
        Assert.Contains("--takeover", args);
        Assert.Contains("--keep-alive", args);
        await other.Run.WaitAsync(TimeSpan.FromSeconds(5));   // steps down so the copy can take the port
    }

    [Fact]
    public async Task Refused_elevation_keeps_the_server_running()
    {
        await using var other = await OtherServerAsync(o => o with { KeepAlive = true, IsAdmin = false, RelaunchElevated = _ => false });

        var res = await other.Http.SendAsync(Action("/api/elevate", null));

        Assert.Contains("\"ok\":false", await res.Content.ReadAsStringAsync());
        await Task.Delay(500);
        Assert.False(other.Run.IsCompleted);
    }

    [Fact]
    public async Task Elevate_is_a_no_op_when_already_admin()
    {
        var calls = 0;
        await using var other = await OtherServerAsync(o => o with { KeepAlive = true, IsAdmin = true, RelaunchElevated = _ => { calls++; return true; } });

        var res = await other.Http.SendAsync(Action("/api/elevate", null));

        Assert.Contains("\"already\":true", await res.Content.ReadAsStringAsync());
        Assert.Equal(0, calls);
    }

    /// <summary>A second server with its own options (the shared one is keep-alive and never elevates).</summary>
    private static async Task<OtherServer> OtherServerAsync(Func<WebServerOptions, WebServerOptions> configure)
    {
        var url = $"http://localhost:{FreePort()}/";
        var hub = new SensorHub(new FakeSensorSource(), admin: false);
        var options = configure(new WebServerOptions(url, KeepAlive: false)
        {
            WatchdogPeriod = TimeSpan.FromMilliseconds(50),
            RelaunchElevated = _ => throw new InvalidOperationException("a test must never trigger a real UAC prompt"),
        });
        var server = new WebServer(hub, options);
        Assert.True(await server.StartAsync(TimeSpan.FromSeconds(2)));
        var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
        return new OtherServer(server, hub, http, server.RunAsync());
    }

    private sealed record OtherServer(WebServer Server, SensorHub Hub, HttpClient Http, Task Run) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Server.Shutdown("test done");
            await Run.WaitAsync(TimeSpan.FromSeconds(5));
            Hub.Dispose();
            Http.Dispose();
        }
    }

    // Reads the stream until `marker` has been seen and the text after it contains `end`.
    private static async Task<string> ReadUntilAsync(Stream stream, string marker, string end)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sb = new StringBuilder();
        var buffer = new byte[8192];
        while (true)
        {
            var text = sb.ToString();
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0 && text.IndexOf(end, at, StringComparison.Ordinal) >= 0) return text;
            var n = await stream.ReadAsync(buffer, cts.Token);
            if (n == 0) throw new EndOfStreamException(text);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
    }
}
