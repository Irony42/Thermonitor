using Thermonitor.Sensors;
using static Thermonitor.Tests.TestData;

namespace Thermonitor.Tests;

public class ReadingFilterTests
{
    [Theory]
    [InlineData(float.NaN, float.NaN)]
    [InlineData(float.PositiveInfinity, float.NaN)]
    [InlineData(-128f, float.NaN)]
    [InlineData(0f, float.NaN)]
    [InlineData(12f, float.NaN)]      // open Super I/O input seen on a real board
    [InlineData(14.9f, float.NaN)]
    [InlineData(15f, 15f)]
    [InlineData(45.5f, 45.5f)]
    [InlineData(124.9f, 124.9f)]
    [InlineData(125f, float.NaN)]
    [InlineData(127f, float.NaN)]
    public void Plausible_keeps_only_real_temperatures(float raw, float expected) =>
        Assert.Equal(expected, ReadingFilter.Plausible(raw));

    [Theory]
    [InlineData("CPU Package", true)]
    [InlineData("P-Core #3", true)]
    [InlineData("Composite Temperature", true)]
    [InlineData("Core #1 Distance to TjMax", false)]
    [InlineData("Warning Temperature", false)]
    [InlineData("Critical Temperature", false)]
    [InlineData("Thermal Limit", false)]
    [InlineData("Temperature Resolution", false)]
    public void IsTemperatureReading_drops_thresholds(string name, bool expected) =>
        Assert.Equal(expected, ReadingFilter.IsTemperatureReading(name));

    [Fact]
    public void One_off_spike_on_a_glitchy_sensor_is_flattened()
    {
        var filter = new ReadingFilter();
        filter.Reset([Sensor("board", glitchy: true)]);

        var seen = new[] { 17f, 115f, 17f }.Select(v => filter.Apply([v])[0]);

        Assert.Equal([17f, 17f, 17f], seen);
    }

    [Fact]
    public void Lasting_jump_is_accepted_after_being_held_three_times()
    {
        var filter = new ReadingFilter();
        filter.Reset([Sensor("board", glitchy: true)]);
        filter.Apply([40f]);

        var seen = Enumerable.Range(0, 5).Select(_ => filter.Apply([80f])[0]).ToArray();

        Assert.Equal([40f, 40f, 40f, 80f, 80f], seen);
    }

    [Fact]
    public void Non_glitchy_sensors_follow_fast_changes()
    {
        var filter = new ReadingFilter();
        filter.Reset([Sensor("cpu")]);
        filter.Apply([35f]);

        Assert.Equal(90f, filter.Apply([90f])[0]);   // a CPU really does jump under load
    }

    [Fact]
    public void Missing_readings_do_not_reset_the_reference_value()
    {
        var filter = new ReadingFilter();
        filter.Reset([Sensor("board", glitchy: true)]);
        filter.Apply([30f]);
        Assert.True(float.IsNaN(filter.Apply([float.NaN])[0]));

        Assert.Equal(30f, filter.Apply([110f])[0]);
    }

    [Fact]
    public void Apply_rejects_a_row_that_does_not_match_the_sensor_list()
    {
        var filter = new ReadingFilter();
        filter.Reset([Sensor("a")]);

        Assert.Throws<ArgumentException>(() => filter.Apply([1f, 2f]));
    }

    [Fact]
    public void Apply_does_not_modify_the_raw_row()
    {
        var filter = new ReadingFilter();
        filter.Reset([Sensor("a")]);
        var raw = new[] { 5f };

        filter.Apply(raw);

        Assert.Equal(5f, raw[0]);
    }
}
