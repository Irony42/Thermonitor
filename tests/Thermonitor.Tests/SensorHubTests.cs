using System.Text;
using System.Threading.Channels;
using Thermonitor.Feed;
using Thermonitor.Sensors;
using static Thermonitor.Tests.TestData;

namespace Thermonitor.Tests;

public class SensorHubTests
{
    private static readonly SensorInfo[] TwoSensors = [Sensor("/a"), Sensor("/b")];

    private static List<(string Event, System.Text.Json.JsonElement Data)> Drain(Channel<byte[]> channel)
    {
        var frames = new List<(string, System.Text.Json.JsonElement)>();
        while (channel.Reader.TryRead(out var frame)) frames.Add(ParseFrame(frame));
        return frames;
    }

    [Fact]
    public void New_subscriber_gets_the_reconnect_delay_then_a_snapshot()
    {
        using var hub = new SensorHub(new FakeSensorSource(), admin: false);

        var first = Encoding.UTF8.GetString(hub.Subscribe(Channel.CreateUnbounded<byte[]>().Writer));

        Assert.StartsWith("retry: 1200\n\n", first);
        var (evt, data) = ParseFrame(first["retry: 1200\n\n".Length..]);
        Assert.Equal("snapshot", evt);
        Assert.Equal("init", data.GetProperty("state").GetString());
        Assert.False(data.GetProperty("admin").GetBoolean());
        Assert.Equal(0, data.GetProperty("sensors").GetArrayLength());
    }

    [Fact]
    public void First_read_publishes_a_snapshot_then_reads_publish_ticks()
    {
        using var hub = new SensorHub(new FakeSensorSource(), admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);
        Assert.True(hub.Open());

        hub.Accept(Frame(TwoSensors, 40f, 50f), 1000);
        hub.Accept(Frame(TwoSensors, 41f, float.NaN), 2000);

        var frames = Drain(channel);
        Assert.Equal(["snapshot", "tick"], frames.Select(f => f.Event));
        Assert.Equal("ready", frames[0].Data.GetProperty("state").GetString());
        Assert.Equal(2, frames[0].Data.GetProperty("sensors").GetArrayLength());
        Assert.Equal(2000, frames[1].Data.GetProperty("t").GetInt64());
        Assert.Equal(41.0, frames[1].Data.GetProperty("v")[0].GetDouble());
    }

    [Fact]
    public void A_new_sensor_keeps_the_history_of_the_others_and_resyncs_clients()
    {
        using var hub = new SensorHub(new FakeSensorSource(), admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);
        hub.Open();
        hub.Accept(Frame(TwoSensors, 40f, 50f), 1000);
        hub.Accept(Frame(TwoSensors, 41f, 51f), 2000);
        Drain(channel);

        // "/c" appears in front, "/a" disappears.
        hub.Accept(Frame([Sensor("/c"), Sensor("/b")], 30f, 52f), 3000);

        var (evt, data) = Assert.Single(Drain(channel));
        Assert.Equal("snapshot", evt);
        Assert.Equal(["/c", "/b"], data.GetProperty("sensors").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal([1000L, 2000L, 3000L], data.GetProperty("t").EnumerateArray().Select(e => e.GetInt64()));
        var c = data.GetProperty("v")[0].EnumerateArray().Select(e => e.ValueKind == System.Text.Json.JsonValueKind.Null ? (double?)null : e.GetDouble());
        var b = data.GetProperty("v")[1].EnumerateArray().Select(e => e.GetDouble());
        Assert.Equal([null, null, 30.0], c);
        Assert.Equal([50.0, 51.0, 52.0], b);
    }

    [Fact]
    public void Times_keep_increasing_when_the_clock_steps_back()
    {
        using var hub = new SensorHub(new FakeSensorSource(), admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);
        hub.Open();
        hub.Accept(Frame(TwoSensors, 40f, 50f), 5000);

        hub.Accept(Frame(TwoSensors, 40f, 50f), 1000);

        Assert.Equal(5001, Drain(channel)[^1].Data.GetProperty("t").GetInt64());
    }

    [Fact]
    public void A_client_that_cannot_keep_up_is_dropped()
    {
        using var hub = new SensorHub(new FakeSensorSource(), admin: true);
        var slow = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        hub.Subscribe(slow.Writer);
        hub.Open();

        hub.Accept(Frame(TwoSensors, 40f, 50f), 1000);   // fills the queue
        hub.Accept(Frame(TwoSensors, 40f, 50f), 2000);   // does not fit: the client is dropped

        Assert.True(slow.Reader.TryRead(out _));
        Assert.True(slow.Reader.Completion.IsCompleted, "the stream ends, so the page reconnects and gets a fresh snapshot");
    }

    [Fact]
    public async Task Polling_thread_publishes_and_survives_failing_reads()
    {
        var reads = 0;
        var source = new FakeSensorSource
        {
            OnRead = () => ++reads % 2 == 0 ? throw new IOException("device unplugged") : Frame(TwoSensors, 40f, 50f),
        };
        var hub = new SensorHub(source, admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);
        hub.SetInterval(SensorHub.MinIntervalMs);

        hub.Start();
        var events = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (events.Count(e => e == "tick") < 2)
            events.Add(ParseFrame(await channel.Reader.ReadAsync(cts.Token)).Event);
        hub.Dispose();

        Assert.Equal("snapshot", events.SkipWhile(e => e == "config").First());
        Assert.True(source.Reads >= 4, "kept polling after a failed read");
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task SetInterval_wakes_the_polling_thread()
    {
        var source = new FakeSensorSource { OnRead = () => Frame(TwoSensors, 40f, 50f) };
        using var hub = new SensorHub(source, admin: true);
        hub.SetInterval(SensorHub.MaxIntervalMs);
        hub.Start();
        await WaitUntil(() => source.Reads >= 1);

        hub.SetInterval(SensorHub.MinIntervalMs);   // without the wake-up, the next read would be 5 s away

        await WaitUntil(() => source.Reads >= 3, TimeSpan.FromSeconds(3));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Readings_go_through_the_filter()
    {
        using var hub = new SensorHub(new FakeSensorSource(), admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);
        hub.Open();

        hub.Accept(Frame([Sensor("/a")], 5f), 1000);   // implausible

        var (_, data) = Assert.Single(Drain(channel));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, data.GetProperty("v")[0][0].ValueKind);
    }

    [Theory]
    [InlineData(10, SensorHub.MinIntervalMs)]
    [InlineData(2000, 2000)]
    [InlineData(60_000, SensorHub.MaxIntervalMs)]
    public void SetInterval_clamps_and_notifies_clients(int requested, int expected)
    {
        using var hub = new SensorHub(new FakeSensorSource(), admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);

        hub.SetInterval(requested);

        Assert.Equal(expected, hub.IntervalMs);
        var (evt, data) = Assert.Single(Drain(channel));
        Assert.Equal("config", evt);
        Assert.Equal(expected, data.GetProperty("interval").GetInt32());
    }

    [Fact]
    public void Open_failure_is_reported_to_clients()
    {
        using var hub = new SensorHub(new FakeSensorSource { OpenError = new InvalidOperationException("no driver") }, admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);

        Assert.False(hub.Open());

        var (evt, data) = Assert.Single(Drain(channel));
        Assert.Equal("snapshot", evt);
        Assert.Equal("error", data.GetProperty("state").GetString());
        Assert.Equal("no driver", data.GetProperty("error").GetString());
    }

    [Fact]
    public void Unsubscribed_clients_get_nothing_more()
    {
        using var hub = new SensorHub(new FakeSensorSource(), admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);
        hub.Open();

        hub.Unsubscribe(channel.Writer);
        hub.Accept(Frame(TwoSensors, 40f, 50f), 1000);

        Assert.Empty(Drain(channel));
        Assert.True(channel.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void Poll_reads_the_source()
    {
        var source = new FakeSensorSource();
        source.Frames.Enqueue(Frame(TwoSensors, 40f, 50f));
        using var hub = new SensorHub(source, admin: true);
        var channel = Channel.CreateUnbounded<byte[]>();
        hub.Subscribe(channel.Writer);
        hub.Open();

        hub.Poll();

        Assert.Equal("snapshot", Assert.Single(Drain(channel)).Event);
        Assert.Empty(source.Frames);
    }

    [Fact]
    public void Dispose_releases_the_source()
    {
        var source = new FakeSensorSource();
        var hub = new SensorHub(source, admin: true);

        hub.Dispose();

        Assert.True(source.Disposed);
    }
}
