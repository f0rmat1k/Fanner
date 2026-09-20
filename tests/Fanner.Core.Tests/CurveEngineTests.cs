using Fanner.Core.Curves;
using Fanner.Core.Model;

namespace Fanner.Core.Tests;

public class CurveEngineTests
{
    private const string FanId = "fan-1";
    private const string SensorId = "temp-1";

    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static CurveBinding Binding(
        TimeSpan? response = null,
        double hysteresis = 2,
        double kickstart = 60,
        int maxKickPolls = 3) => new()
    {
        FanId = FanId,
        SensorId = SensorId,
        Curve = new FanCurve([new CurvePoint(40, 20), new CurvePoint(80, 100)]),
        Response = response ?? TimeSpan.Zero,
        HysteresisPercent = hysteresis,
        KickstartDuty = kickstart,
        MaxKickstartPolls = maxKickPolls,
    };

    private static HardwareSnapshot Snap(
        double seconds,
        double? temperature,
        int? rpm = 900,
        bool canControl = true) => new(
        T0.AddSeconds(seconds),
        [new FanSnapshot(FanId, "CPU Fan", "chip", rpm, 50, canControl, FanControlMode.Software)],
        [new SensorReading(SensorId, "Core", "cpu", SensorKind.Temperature, temperature, HardwareCategory.Cpu)],
        []);

    [Fact]
    public void Drives_the_fan_from_the_curve()
    {
        var engine = new CurveEngine();
        engine.Set(Binding());

        var outcome = engine.Evaluate(Snap(0, 60));

        var command = Assert.Single(outcome.Duties);
        Assert.Equal(FanId, command.FanId);
        Assert.Equal(60, command.Duty, precision: 6);
        Assert.Equal(DutyReason.Curve, command.Reason);
    }

    [Fact]
    public void Produces_nothing_without_bindings() =>
        Assert.Empty(new CurveEngine().Evaluate(Snap(0, 60)).Duties);

    [Fact]
    public void Suppresses_a_change_below_the_deadband()
    {
        var engine = new CurveEngine();
        engine.Set(Binding(hysteresis: 5));

        Assert.Single(engine.Evaluate(Snap(0, 60)).Duties);

        // 61 °C is 2 % more duty on this curve, inside the deadband. Writing it
        // would cost a driver round trip for a change nobody can hear.
        Assert.Empty(engine.Evaluate(Snap(1, 61)).Duties);

        // 65 °C is 10 % more, worth acting on.
        Assert.Single(engine.Evaluate(Snap(2, 65)).Duties);
    }

    [Fact]
    public void Smoothing_holds_back_a_sudden_spike()
    {
        var engine = new CurveEngine();
        engine.Set(Binding(response: TimeSpan.FromSeconds(10)));

        engine.Evaluate(Snap(0, 40));

        // One second of a 40 degree jump, with a ten second time constant, should
        // move the average a few degrees rather than all of it. A curve that chased
        // the raw reading would make the fans surge on every background task.
        var outcome = engine.Evaluate(Snap(1, 80));
        var duty = Assert.Single(outcome.Duties).Duty;

        Assert.InRange(duty, 21, 35);
    }

    [Fact]
    public void Smoothing_converges_on_a_sustained_temperature()
    {
        var engine = new CurveEngine();
        engine.Set(Binding(response: TimeSpan.FromSeconds(5), hysteresis: 0));

        engine.Evaluate(Snap(0, 40));

        double last = 0;
        for (var t = 1; t <= 60; t++)
        {
            var duties = engine.Evaluate(Snap(t, 80)).Duties;
            if (duties.Count > 0)
            {
                last = duties[0].Duty;
            }
        }

        Assert.Equal(100, last, tolerance: 0.5);
    }

    [Fact]
    public void Kickstarts_a_stopped_fan()
    {
        var engine = new CurveEngine();
        engine.Set(Binding(kickstart: 60));

        // 45 °C asks for 30 %, which will not start a fan from a standstill.
        var command = Assert.Single(engine.Evaluate(Snap(0, 45, rpm: 0)).Duties);

        Assert.Equal(60, command.Duty, precision: 6);
        Assert.Equal(DutyReason.Kickstart, command.Reason);
    }

    [Fact]
    public void Stops_kickstarting_once_the_fan_turns()
    {
        var engine = new CurveEngine();
        engine.Set(Binding(kickstart: 60, hysteresis: 0));

        Assert.Equal(DutyReason.Kickstart, engine.Evaluate(Snap(0, 45, rpm: 0)).Duties[0].Reason);

        var running = Assert.Single(engine.Evaluate(Snap(1, 45, rpm: 400)).Duties);
        Assert.Equal(DutyReason.Curve, running.Reason);
        Assert.Equal(30, running.Duty, precision: 6);
    }

    [Fact]
    public void Gives_up_kickstarting_a_header_that_never_responds()
    {
        var engine = new CurveEngine();
        engine.Set(Binding(kickstart: 60, hysteresis: 0, maxKickPolls: 3));

        for (var t = 0; t < 3; t++)
        {
            Assert.Equal(DutyReason.Kickstart, engine.Evaluate(Snap(t, 45, rpm: 0)).Duties[0].Reason);
        }

        // An empty header reports 0 RPM no matter what. Kicking it forever would
        // leave it pinned at 60 % for the rest of the session.
        var settled = Assert.Single(engine.Evaluate(Snap(4, 45, rpm: 0)).Duties);
        Assert.Equal(DutyReason.Curve, settled.Reason);
        Assert.Equal(30, settled.Duty, precision: 6);
    }

    [Fact]
    public void Does_not_kickstart_when_the_curve_already_asks_for_enough()
    {
        var engine = new CurveEngine();
        engine.Set(Binding(kickstart: 60));

        // 75 °C asks for 90 %, well above the kickstart level.
        var command = Assert.Single(engine.Evaluate(Snap(0, 75, rpm: 0)).Duties);

        Assert.Equal(DutyReason.Curve, command.Reason);
        Assert.Equal(90, command.Duty, precision: 6);
    }

    [Fact]
    public void Tolerates_a_brief_sensor_dropout_then_releases()
    {
        var engine = new CurveEngine();
        engine.Set(Binding());

        engine.Evaluate(Snap(0, 60));

        for (var t = 1; t < 10; t++)
        {
            var outcome = engine.Evaluate(Snap(t, null));

            // A reading missing for a poll or two is normal; holding the last duty
            // is the right response.
            Assert.Empty(outcome.Duties);
            Assert.Empty(outcome.Release);
        }

        // Gone for good, though, would leave the fan frozen at an idle duty while
        // the machine heats up. Hand it back to firmware instead.
        Assert.Contains(FanId, engine.Evaluate(Snap(10, null)).Release);
    }

    [Fact]
    public void Produces_nothing_while_suspended()
    {
        var engine = new CurveEngine();
        engine.Set(Binding());
        engine.Suspend("watchdog");

        Assert.True(engine.IsSuspended);
        Assert.Empty(engine.Evaluate(Snap(0, 90)).Duties);

        engine.Resume();
        Assert.False(engine.IsSuspended);
        Assert.Single(engine.Evaluate(Snap(1, 90)).Duties);
    }

    [Fact]
    public void Skips_a_header_that_cannot_be_controlled()
    {
        var engine = new CurveEngine();
        engine.Set(Binding());

        Assert.Empty(engine.Evaluate(Snap(0, 60, canControl: false)).Duties);
    }

    [Fact]
    public void Replacing_a_binding_resets_its_smoothing()
    {
        var engine = new CurveEngine();
        engine.Set(Binding(response: TimeSpan.FromSeconds(30)));

        engine.Evaluate(Snap(0, 40));

        // Re-binding to a different sensor or curve must not carry over an average
        // built from readings that no longer apply.
        engine.Set(Binding(response: TimeSpan.FromSeconds(30)));

        var command = Assert.Single(engine.Evaluate(Snap(1, 80)).Duties);
        Assert.Equal(100, command.Duty, precision: 6);
    }

    [Fact]
    public void Removing_a_binding_stops_the_commands()
    {
        var engine = new CurveEngine();
        engine.Set(Binding());

        Assert.Single(engine.Evaluate(Snap(0, 60)).Duties);

        engine.Remove(FanId);

        Assert.False(engine.HasBindings);
        Assert.Empty(engine.Evaluate(Snap(1, 60)).Duties);
    }
}
