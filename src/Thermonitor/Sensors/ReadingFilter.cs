namespace Thermonitor.Sensors;

/// <summary>
/// Turns raw readings into plottable ones: implausible values become NaN, and on glitchy
/// (board) sensors a sudden jump is held back until it repeats.
/// </summary>
/// <remarks>Not thread-safe: one instance per polling loop.</remarks>
internal sealed class ReadingFilter
{
    /// <summary>Nothing inside a running PC is colder; Super I/O chips report 0, 12... on open inputs.</summary>
    internal const float MinPlausible = 15f;

    /// <summary>Super I/O chips report 127 / 128 on open inputs; no component survives this anyway.</summary>
    internal const float MaxPlausible = 125f;

    /// <summary>A board sensor moving more than this in one read is suspect (e.g. 17 -> 115 -> 17 °C).</summary>
    internal const float MaxJump = 25f;

    /// <summary>A suspect jump is accepted once it has been seen this many times in a row, plus one.</summary>
    internal const int MaxHeld = 3;

    private bool[] _glitchy = [];
    private float[] _last = [];
    private int[] _held = [];

    /// <summary>Call whenever the sensor list changes.</summary>
    public void Reset(IReadOnlyList<SensorInfo> sensors)
    {
        _glitchy = sensors.Select(s => s.Glitchy).ToArray();
        _last = Enumerable.Repeat(float.NaN, sensors.Count).ToArray();
        _held = new int[sensors.Count];
    }

    public float[] Apply(float[] raw)
    {
        if (raw.Length != _glitchy.Length)
            throw new ArgumentException($"{raw.Length} values for {_glitchy.Length} sensors: call Reset() first", nameof(raw));

        var row = new float[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var v = Plausible(raw[i]);
            if (_glitchy[i] && !float.IsNaN(v))
            {
                var last = _last[i];
                if (!float.IsNaN(last) && Math.Abs(v - last) > MaxJump && _held[i] < MaxHeld)
                {
                    _held[i]++;
                    v = last;
                }
                else
                {
                    _last[i] = v;
                    _held[i] = 0;
                }
            }
            row[i] = v;
        }
        return row;
    }

    public static float Plausible(float v) =>
        float.IsFinite(v) && v >= MinPlausible && v < MaxPlausible ? v : float.NaN;

    /// <summary>False for thresholds and derived values that LibreHardwareMonitor lists as temperatures.</summary>
    public static bool IsTemperatureReading(string name) =>
        !name.Contains("TjMax", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Warning", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Critical", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Limit", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Resolution", StringComparison.OrdinalIgnoreCase);
}
