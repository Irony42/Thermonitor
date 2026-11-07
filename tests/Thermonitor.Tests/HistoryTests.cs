using Thermonitor.Feed;

namespace Thermonitor.Tests;

public class HistoryTests
{
    [Fact]
    public void Keeps_rows_in_order()
    {
        var history = new History();
        history.Add(1000, [1f]);
        history.Add(2000, [2f]);

        Assert.Equal([1000L, 2000L], history.Times);
        Assert.Equal(2f, history.Rows[1][0]);
    }

    [Fact]
    public void Drops_rows_older_than_max_age()
    {
        var history = new History(maxEntries: 100, maxAgeMs: 10_000);
        history.Add(0, [0f]);
        history.Add(5_000, [1f]);
        history.Add(10_000, [2f]);   // exactly at the limit: kept
        history.Add(12_000, [3f]);

        Assert.Equal([5_000L, 10_000L, 12_000L], history.Times);
    }

    [Fact]
    public void Caps_the_number_of_rows()
    {
        var history = new History(maxEntries: 3, maxAgeMs: long.MaxValue);
        for (var t = 0; t < 10; t++) history.Add(t, [t]);

        Assert.Equal([7L, 8L, 9L], history.Times);
        Assert.Equal(3, history.Rows.Count);
    }

    [Fact]
    public void Remap_moves_columns_and_fills_new_sensors_with_NaN()
    {
        var history = new History();
        history.Add(1, [10f, 20f, 30f]);
        history.Add(2, [11f, 21f, 31f]);

        history.Remap([2, -1, 0]);   // new layout: old #2, a brand new sensor, old #0

        Assert.Equal([30f, float.NaN, 10f], history.Rows[0]);
        Assert.Equal([31f, float.NaN, 11f], history.Rows[1]);
        Assert.Equal([1L, 2L], history.Times);
    }

    [Fact]
    public void LastTime_is_null_when_empty()
    {
        var history = new History();
        Assert.Null(history.LastTime);
        history.Add(42, [1f]);
        Assert.Equal(42, history.LastTime);
    }

    [Fact]
    public void Clear_empties_both_lists()
    {
        var history = new History();
        history.Add(1, [1f]);

        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Empty(history.Rows);
    }
}
