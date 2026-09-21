using Fanner.Core.Model;
using Fanner.Core.Safety;

namespace Fanner.Core.Tests;

public class SafetyWatchdogTests
{
    private static HardwareSnapshot Snapshot(
        double cpuTemperature,
        FanControlMode mode = FanControlMode.Software,
        HardwareCategory category = HardwareCategory.Cpu) => new(
        DateTimeOffset.UnixEpoch,
        [new FanSnapshot("fan-1", "CPU Fan", "chip", 900, 40, CanControl: true, mode)],
        [new SensorReading("t", "Core (Tctl/Tdie)", "cpu", SensorKind.Temperature, cpuTemperature, category)],
        []);

    [Fact]
    public void Trips_when_a_watched_limit_is_crossed_while_we_drive()
    {
        var trip = new SafetyWatchdog().Evaluate(Snapshot(96));

        Assert.NotNull(trip);
        Assert.Equal(96, trip.Temperature);
        Assert.Equal(95, trip.Threshold);
    }

    [Fact]
    public void Stays_quiet_below_the_limit()
    {
        // The case that drove the threshold choice: a 9800X3D sits in the low
        // nineties under sustained load by design. Tripping here would fire during
        // ordinary gaming and teach the user to ignore the warning.
        Assert.Null(new SafetyWatchdog().Evaluate(Snapshot(94)));
    }

    [Fact]
    public void Stays_quiet_when_the_firmware_is_driving()
    {
        // Nothing of ours to undo: releasing fans we do not hold changes nothing,
        // and a hot chip under the board's own curve is not our emergency.
        Assert.Null(new SafetyWatchdog().Evaluate(Snapshot(120, FanControlMode.Firmware)));
    }

    [Fact]
    public void Ignores_categories_it_does_not_watch()
    {
        // An SSD past 95 °C throttles itself, and releasing a case fan would not
        // rescue it in time to matter.
        Assert.Null(new SafetyWatchdog().Evaluate(
            Snapshot(99, FanControlMode.Software, HardwareCategory.Storage)));
    }

    [Fact]
    public void Reports_one_excursion_only_once()
    {
        var watchdog = new SafetyWatchdog();

        Assert.NotNull(watchdog.Evaluate(Snapshot(97)));

        // Still hot on the next poll, but this is the same event; reporting every
        // second would bury the original message.
        Assert.Null(watchdog.Evaluate(Snapshot(98)));
        Assert.Null(watchdog.Evaluate(Snapshot(96)));
    }

    [Fact]
    public void Reports_again_after_cooling_clear_of_the_limit()
    {
        var watchdog = new SafetyWatchdog { HysteresisC = 5 };

        Assert.NotNull(watchdog.Evaluate(Snapshot(97)));

        // 91 is below the limit but inside the hysteresis band, so the excursion is
        // not over yet.
        Assert.Null(watchdog.Evaluate(Snapshot(91)));
        Assert.Null(watchdog.Evaluate(Snapshot(96)));

        Assert.Null(watchdog.Evaluate(Snapshot(70)));
        Assert.NotNull(watchdog.Evaluate(Snapshot(97)));
    }

    [Fact]
    public void Honours_an_overridden_threshold()
    {
        var watchdog = new SafetyWatchdog();
        watchdog.SetThreshold(HardwareCategory.Cpu, 60);

        Assert.NotNull(watchdog.Evaluate(Snapshot(61)));
    }

    [Fact]
    public void Ignores_a_sensor_with_no_reading()
    {
        var snapshot = new HardwareSnapshot(
            DateTimeOffset.UnixEpoch,
            [new FanSnapshot("fan-1", "CPU Fan", "chip", 0, 40, true, FanControlMode.Software)],
            [new SensorReading("t", "Core", "cpu", SensorKind.Temperature, null, HardwareCategory.Cpu)],
            []);

        Assert.Null(new SafetyWatchdog().Evaluate(snapshot));
    }

    private static HardwareSnapshot GpuSnapshot(
        string sensorName,
        double temperature,
        HardwareCategory drivenFan) => new(
        DateTimeOffset.UnixEpoch,
        [new FanSnapshot(
            "fan-1", "Fan", "chip", 900, 40, CanControl: true, FanControlMode.Software,
            MinDuty: 0, MaxDuty: 100, Category: drivenFan)],
        [new SensorReading(
            "t", sensorName, "NVIDIA GeForce RTX 4090", SensorKind.Temperature,
            temperature, HardwareCategory.Gpu)],
        []);

    [Fact]
    public void Ignores_a_hot_graphics_card_whose_fans_are_not_ours()
    {
        // Reported from a real machine: case fans on a custom curve, GPU fans never
        // touched, and a warning saying every fan had been handed back because the
        // card was warm. Nothing we did made the card hot and nothing we undo will
        // cool it, so this is not our emergency to declare.
        Assert.Null(new SafetyWatchdog().Evaluate(
            GpuSnapshot("GPU Hot Spot", 99, HardwareCategory.Motherboard)));
    }

    [Fact]
    public void Ignores_a_hot_spot_reading_that_is_ordinary_for_the_part()
    {
        // 90.8 °C on the hot spot of a 4090 is a game running, not a fault: the
        // measurement sits ten to twenty degrees above the core it belongs to.
        Assert.Null(new SafetyWatchdog().Evaluate(
            GpuSnapshot("GPU Hot Spot", 90.8, HardwareCategory.Gpu)));
    }

    [Fact]
    public void Trips_on_a_hot_spot_past_its_own_limit()
    {
        var trip = new SafetyWatchdog().Evaluate(
            GpuSnapshot("GPU Hot Spot", 106, HardwareCategory.Gpu));

        Assert.NotNull(trip);
        Assert.Equal(105, trip.Threshold);
    }

    [Fact]
    public void Trips_on_a_hot_core_when_the_card_is_running_our_duty()
    {
        var trip = new SafetyWatchdog().Evaluate(
            GpuSnapshot("GPU Core", 91, HardwareCategory.Gpu));

        Assert.NotNull(trip);
        Assert.Equal(90, trip.Threshold);
    }

    [Fact]
    public void Summary_names_the_watched_limits()
    {
        var summary = new SafetyWatchdog().Summary;

        Assert.Contains("CPU 95", summary);
        Assert.Contains("GPU 90", summary);
    }
}
