using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Thermonitor.Sensors;

namespace Thermonitor.Tests;

internal static class TestData
{
    public static SensorInfo Sensor(string id, bool glitchy = false, string hwType = "Cpu") =>
        new(id, "Sensor " + id, "/hw", "Device", hwType, null, 0, glitchy);

    public static SensorFrame Frame(SensorInfo[] sensors, params float[] values) => new(sensors, values, ReadMs: 12.34);

    /// <summary>Splits one SSE frame into its event name and parsed JSON payload.</summary>
    public static (string Event, JsonElement Data) ParseFrame(byte[] frame) => ParseFrame(Encoding.UTF8.GetString(frame));

    public static (string Event, JsonElement Data) ParseFrame(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var evt = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
        var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return (evt, JsonDocument.Parse(data).RootElement.Clone());
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class FakeSensorSource : ISensorSource
{
    public Queue<SensorFrame> Frames { get; } = new();
    public Exception? OpenError { get; init; }

    /// <summary>When set, replaces the queue (called from the hub's polling thread).</summary>
    public Func<SensorFrame>? OnRead { get; init; }

    public volatile bool Disposed;
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);

    public string Version => "test";
    public bool? DriverInstalled => true;

    public void Open()
    {
        if (OpenError is not null) throw OpenError;
    }

    public SensorFrame Read()
    {
        Interlocked.Increment(ref _reads);
        return OnRead is not null ? OnRead() : Frames.Dequeue();
    }

    public void Dispose() => Disposed = true;
}
