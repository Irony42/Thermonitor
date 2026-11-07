using System.Buffers;
using System.Text;
using System.Text.Json;
using Thermonitor.Sensors;

namespace Thermonitor.Feed;

/// <summary>State of the feed sent along with every snapshot.</summary>
internal sealed record FeedStatus(string State, string? Error, bool Admin, bool? Driver, int IntervalMs, double ReadMs, string Lib);

/// <summary>
/// Wire format of the live feed (Server-Sent Events, JSON payloads).
/// <list type="bullet">
/// <item><c>snapshot</c>: status, sensor list, full history as one column per sensor.</item>
/// <item><c>tick</c>: one new row, values in sensor order.</item>
/// <item><c>config</c>: the polling interval changed.</item>
/// </list>
/// Missing readings are <c>null</c>; values are rounded to 0.1 °C.
/// </summary>
internal static class FeedFrames
{
    /// <summary>Tells EventSource to reconnect after 1.2 s instead of its default 3 s.</summary>
    public static readonly byte[] Retry = "retry: 1200\n\n"u8.ToArray();

    public static byte[] Snapshot(FeedStatus status, IReadOnlyList<SensorInfo> sensors, History history) => Frame("snapshot", w =>
    {
        w.WriteString("state", status.State);
        if (status.Error is not null) w.WriteString("error", status.Error);
        w.WriteBoolean("admin", status.Admin);
        if (status.Driver is { } driver) w.WriteBoolean("pawnio", driver);
        else w.WriteNull("pawnio");
        w.WriteNumber("interval", status.IntervalMs);
        w.WriteNumber("u", Math.Round(status.ReadMs, 1));
        w.WriteString("lib", status.Lib);

        w.WriteStartArray("sensors");
        foreach (var s in sensors)
        {
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteString("name", s.Name);
            w.WriteString("hw", s.Hw);
            w.WriteString("hwName", s.HwName);
            w.WriteString("hwType", s.HwType);
            if (s.Sub is not null) w.WriteString("sub", s.Sub);
            else w.WriteNull("sub");
            w.WriteNumber("index", s.Index);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("t");
        foreach (var t in history.Times) w.WriteNumberValue(t);
        w.WriteEndArray();

        w.WriteStartArray("v");
        for (var i = 0; i < sensors.Count; i++)
        {
            w.WriteStartArray();
            foreach (var row in history.Rows) WriteValue(w, i < row.Length ? row[i] : float.NaN);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    });

    public static byte[] Tick(long timeMs, float[] row, double readMs) => Frame("tick", w =>
    {
        w.WriteNumber("t", timeMs);
        w.WriteNumber("u", Math.Round(readMs, 1));
        w.WriteStartArray("v");
        foreach (var v in row) WriteValue(w, v);
        w.WriteEndArray();
    });

    public static byte[] Config(int intervalMs) => Frame("config", w => w.WriteNumber("interval", intervalMs));

    private static void WriteValue(Utf8JsonWriter w, float v)
    {
        if (float.IsNaN(v)) w.WriteNullValue();
        else w.WriteNumberValue(Math.Round((double)v, 1));
    }

    private static byte[] Frame(string eventName, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        buffer.Write(Encoding.UTF8.GetBytes($"event: {eventName}\ndata: "));
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        buffer.Write("\n\n"u8);
        return buffer.WrittenSpan.ToArray();
    }
}
