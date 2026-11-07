using System.Text;
using System.Text.Json;
using Thermonitor.Feed;
using Thermonitor.Sensors;
using static Thermonitor.Tests.TestData;

namespace Thermonitor.Tests;

public class FeedFramesTests
{
    [Fact]
    public void Tick_rounds_values_and_sends_null_for_missing_readings()
    {
        var (evt, data) = ParseFrame(FeedFrames.Tick(1234, [42.26f, float.NaN], 9.87));

        Assert.Equal("tick", evt);
        Assert.Equal(1234, data.GetProperty("t").GetInt64());
        Assert.Equal(9.9, data.GetProperty("u").GetDouble());
        var v = data.GetProperty("v");
        Assert.Equal(42.3, v[0].GetDouble());
        Assert.Equal(JsonValueKind.Null, v[1].ValueKind);
    }

    [Fact]
    public void Frames_end_with_a_blank_line()
    {
        var text = Encoding.UTF8.GetString(FeedFrames.Config(500));

        Assert.StartsWith("event: config\ndata: {", text);
        Assert.EndsWith("}\n\n", text);
    }

    [Fact]
    public void Snapshot_sends_history_as_one_column_per_sensor()
    {
        SensorInfo[] sensors =
        [
            Sensor("/cpu/0"),
            new("/lpc/1", "Temperature #1", "/mb", "Board", "Motherboard", "Nuvoton NCT6798D", 1, true),
        ];
        var history = new History();
        history.Add(1000, [40f, 30f]);
        history.Add(2000, [41f, float.NaN]);
        history.Add(3000, [42f, 32f]);
        var status = new FeedStatus("ready", null, Admin: true, Driver: null, IntervalMs: 1000, ReadMs: 3.14159, Lib: "0.9.6");

        var (evt, data) = ParseFrame(FeedFrames.Snapshot(status, sensors, history));

        Assert.Equal("snapshot", evt);
        Assert.Equal("ready", data.GetProperty("state").GetString());
        Assert.False(data.TryGetProperty("error", out _));
        Assert.True(data.GetProperty("admin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("pawnio").ValueKind);
        Assert.Equal(3.1, data.GetProperty("u").GetDouble());
        Assert.Equal(3, data.GetProperty("t").GetArrayLength());

        var columns = data.GetProperty("v");
        Assert.Equal(2, columns.GetArrayLength());
        Assert.Equal([40.0, 41.0, 42.0], columns[0].EnumerateArray().Select(e => e.GetDouble()));
        Assert.Equal(JsonValueKind.Null, columns[1][1].ValueKind);

        var board = data.GetProperty("sensors")[1];
        Assert.Equal("Nuvoton NCT6798D", board.GetProperty("sub").GetString());
        Assert.Equal("Motherboard", board.GetProperty("hwType").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("sensors")[0].GetProperty("sub").ValueKind);
    }

    [Fact]
    public void Snapshot_carries_the_error_when_there_is_one()
    {
        var status = new FeedStatus("error", "driver missing", false, false, 1000, 0, "x");

        var (_, data) = ParseFrame(FeedFrames.Snapshot(status, [], new History()));

        Assert.Equal("driver missing", data.GetProperty("error").GetString());
        Assert.False(data.GetProperty("pawnio").GetBoolean());
    }
}
