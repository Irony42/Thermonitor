namespace Thermonitor.Feed;

/// <summary>Time-ordered rows of readings, bounded in age and in count.</summary>
/// <remarks>Not thread-safe: <see cref="SensorHub"/> guards it with its lock.</remarks>
internal sealed class History(int maxEntries = 7200, long maxAgeMs = 3_600_000)
{
    private readonly List<long> _times = [];
    private readonly List<float[]> _rows = [];

    public int Count => _times.Count;

    public IReadOnlyList<long> Times => _times;

    public IReadOnlyList<float[]> Rows => _rows;

    public long? LastTime => _times.Count == 0 ? null : _times[^1];

    public void Clear()
    {
        _times.Clear();
        _rows.Clear();
    }

    public void Add(long timeMs, float[] row)
    {
        _times.Add(timeMs);
        _rows.Add(row);

        var drop = 0;
        while (drop < _times.Count && (timeMs - _times[drop] > maxAgeMs || _times.Count - drop > maxEntries)) drop++;
        if (drop == 0) return;
        _times.RemoveRange(0, drop);
        _rows.RemoveRange(0, drop);
    }

    /// <summary>
    /// Re-lays every row out for a new sensor list: <c>fromOld[i]</c> is the old column of new
    /// sensor <c>i</c>, or -1 for a sensor that did not exist (its past becomes NaN).
    /// </summary>
    public void Remap(int[] fromOld)
    {
        for (var r = 0; r < _rows.Count; r++)
        {
            var old = _rows[r];
            var row = new float[fromOld.Length];
            for (var i = 0; i < row.Length; i++)
                row[i] = fromOld[i] >= 0 && fromOld[i] < old.Length ? old[fromOld[i]] : float.NaN;
            _rows[r] = row;
        }
    }
}
