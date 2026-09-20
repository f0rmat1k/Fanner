using Fanner.Core.Model;
using Fanner.Core.Monitoring;

namespace Fanner.Core.Tests;

public class SensorHistoryTests
{
    private static HardwareSnapshot Snapshot(double? temperature, int? rpm, double? duty) => new(
        DateTimeOffset.UnixEpoch,
        [
            new FanSnapshot("fan-1", "CPU Fan", "chip", rpm, duty, CanControl: true, FanControlMode.Firmware),
        ],
        [
            new SensorReading("temp-1", "Core", "cpu", SensorKind.Temperature, temperature),
        ],
        []);

    [Fact]
    public void Keeps_samples_in_order()
    {
        var history = new SensorHistory(capacity: 10);

        for (var i = 1; i <= 3; i++)
        {
            history.Record(Snapshot(i, rpm: i * 100, duty: i));
        }

        Assert.Equal([1f, 2f, 3f], history.Read("temp-1"));
        Assert.Equal([100f, 200f, 300f], history.Read(SensorHistory.RpmSeriesId("fan-1")));
        Assert.Equal([1f, 2f, 3f], history.Read(SensorHistory.DutySeriesId("fan-1")));
    }

    [Fact]
    public void Drops_the_oldest_sample_once_full()
    {
        var history = new SensorHistory(capacity: 3);

        for (var i = 1; i <= 5; i++)
        {
            history.Record(Snapshot(i, rpm: null, duty: null));
        }

        // Capacity is a hard cap, and the window slides rather than resetting.
        Assert.Equal([3f, 4f, 5f], history.Read("temp-1"));
    }

    [Fact]
    public void Records_a_missing_reading_as_a_gap()
    {
        var history = new SensorHistory(capacity: 5);

        history.Record(Snapshot(40, rpm: 900, duty: 50));
        history.Record(Snapshot(temperature: null, rpm: null, duty: 50));
        history.Record(Snapshot(42, rpm: 950, duty: 50));

        var series = history.Read("temp-1");

        // NaN rather than 0: the sparkline breaks the line instead of drawing a
        // spike down to the axis that never happened.
        Assert.Equal(3, series.Length);
        Assert.Equal(40f, series[0]);
        Assert.True(float.IsNaN(series[1]));
        Assert.Equal(42f, series[2]);
    }

    [Fact]
    public void Returns_empty_for_an_unknown_series() =>
        Assert.Empty(new SensorHistory().Read("nothing-recorded-this"));

    [Fact]
    public void Rejects_a_non_positive_capacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SensorHistory(0));
}
