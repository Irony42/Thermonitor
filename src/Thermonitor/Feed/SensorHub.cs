using System.Diagnostics;
using System.Threading.Channels;
using Thermonitor.Platform;
using Thermonitor.Sensors;

namespace Thermonitor.Feed;

/// <summary>
/// The live feed: polls a sensor source on its own thread, keeps one hour of history and pushes
/// Server-Sent-Event frames to every subscriber. Frames are encoded once and shared by all clients.
/// </summary>
internal sealed class SensorHub(ISensorSource source, bool admin) : IDisposable
{
    internal const int MinIntervalMs = 250;
    internal const int MaxIntervalMs = 5000;
    private static readonly TimeSpan FailureLogEvery = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly List<ChannelWriter<byte[]>> _subscribers = [];
    private readonly History _history = new();
    private readonly ReadingFilter _filter = new();   // polling thread only
    private readonly ManualResetEventSlim _wake = new(false);
    private string? _signature;                        // polling thread only
    private IReadOnlyList<SensorInfo> _sensors = [];
    private Thread? _thread;
    private volatile bool _stop;
    private string _state = "init";
    private string? _error;
    private double _readMs;
    private int _intervalMs = 1000;
    private int _failures;                             // polling thread only
    private long _lastFailureLog = long.MinValue;      // polling thread only

    public int IntervalMs
    {
        get { lock (_gate) return _intervalMs; }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "sensors" };
        _thread.Start();
    }

    /// <summary>Registers a client; returns the frames to send it first (reconnect delay + snapshot).</summary>
    public byte[] Subscribe(ChannelWriter<byte[]> writer)
    {
        lock (_gate)
        {
            _subscribers.Add(writer);
            return [.. FeedFrames.Retry, .. SnapshotLocked()];
        }
    }

    public void Unsubscribe(ChannelWriter<byte[]> writer)
    {
        lock (_gate) _subscribers.Remove(writer);
        writer.TryComplete();
    }

    public void SetInterval(int ms)
    {
        lock (_gate)
        {
            _intervalMs = Math.Clamp(ms, MinIntervalMs, MaxIntervalMs);
            PublishLocked(FeedFrames.Config(_intervalMs));
        }
        _wake.Set();
    }

    /// <summary>Opens the source. False (and an error snapshot to clients) when it cannot be opened.</summary>
    internal bool Open()
    {
        try
        {
            source.Open();
            lock (_gate) _state = "ready";
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("sensor source failed to open: " + ex);
            lock (_gate)
            {
                _state = "error";
                _error = ex.Message;
                PublishLocked(SnapshotLocked());
            }
            return false;
        }
    }

    /// <summary>Reads every sensor once and publishes the result.</summary>
    internal void Poll() => Accept(source.Read(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal void Accept(SensorFrame frame, long timeMs)
    {
        var changed = frame.Signature != _signature;
        if (changed) _filter.Reset(frame.Sensors);
        var row = _filter.Apply(frame.Values);

        lock (_gate)
        {
            // Times must keep increasing (the page binary-searches them), even if the clock steps back.
            if (_history.LastTime is { } last && timeMs <= last) timeMs = last + 1;
            _readMs = frame.ReadMs;

            if (changed)
            {
                // A sensor appeared or disappeared: keep the past of the others, resync clients.
                var oldColumn = _sensors.Select((s, i) => (s.Id, i)).ToDictionary(p => p.Id, p => p.i);
                _history.Remap(frame.Sensors.Select(s => oldColumn.GetValueOrDefault(s.Id, -1)).ToArray());
                _signature = frame.Signature;
                _sensors = frame.Sensors;
                _history.Add(timeMs, row);
                PublishLocked(SnapshotLocked());
                Log.Write($"{_sensors.Count} temperature sensors: " + string.Join(", ", _sensors.Select(s => $"{s.HwName}/{s.Name}")));
            }
            else
            {
                _history.Add(timeMs, row);
                PublishLocked(FeedFrames.Tick(timeMs, row, frame.ReadMs));
            }
        }
    }

    private void Run()
    {
        if (!Open()) return;

        var loop = new Stopwatch();
        while (!_stop)
        {
            loop.Restart();
            try
            {
                Poll();
                _failures = 0;
            }
            catch (Exception ex)
            {
                LogFailure(ex);
            }

            var remaining = IntervalMs - (int)loop.ElapsedMilliseconds;
            try
            {
                if (remaining > 0 && _wake.Wait(remaining)) _wake.Reset();
            }
            catch (ObjectDisposedException)
            {
                return;   // disposed while waiting
            }
        }
    }

    // A failing read repeats at every tick: full detail once, then a summary at most once a minute.
    private void LogFailure(Exception ex)
    {
        _failures++;
        var now = Environment.TickCount64;
        if (_failures == 1) Log.Write("poll failed: " + ex);
        else if (now - _lastFailureLog >= FailureLogEvery.TotalMilliseconds) Log.Write($"poll still failing ({_failures} times): {ex.Message}");
        else return;
        _lastFailureLog = now;
    }

    // A client that cannot keep up is dropped; its page reconnects and gets a fresh snapshot.
    private void PublishLocked(byte[] frame)
    {
        for (var i = _subscribers.Count - 1; i >= 0; i--)
        {
            if (_subscribers[i].TryWrite(frame)) continue;
            _subscribers[i].TryComplete();
            _subscribers.RemoveAt(i);
        }
    }

    private byte[] SnapshotLocked() => FeedFrames.Snapshot(
        new FeedStatus(_state, _error, admin, source.DriverInstalled, _intervalMs, _readMs, source.Version),
        _sensors,
        _history);

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        var stopped = _thread?.Join(TimeSpan.FromSeconds(5)) ?? true;
        lock (_gate)
            foreach (var subscriber in _subscribers) subscriber.TryComplete();
        if (!stopped)
        {
            // Still inside a driver call: closing the hardware under it could crash; the process is exiting anyway.
            Log.Write("sensor thread did not stop, leaving the source open");
            return;
        }
        source.Dispose();
        _wake.Dispose();
    }
}
