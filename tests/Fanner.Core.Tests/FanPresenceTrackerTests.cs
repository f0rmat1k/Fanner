using Fanner.Core.Model;
using Fanner.Core.Monitoring;

namespace Fanner.Core.Tests;

public class FanPresenceTrackerTests
{
    private const string FanId = "/lpc/nct6687dr/0/header/10";

    private static FanSnapshot Fan(int? rpm, double? duty, string id = FanId) => new(
        Id: id,
        Name: "System Fan #1",
        HardwareName: "chip",
        Rpm: rpm,
        DutyPercent: duty,
        CanControl: true,
        Mode: FanControlMode.Firmware);

    private static HardwareSnapshot Snapshot(params FanSnapshot[] fans) =>
        new(DateTimeOffset.UnixEpoch, fans, [], []);

    [Fact]
    public void Remembers_a_header_that_has_turned()
    {
        var tracker = new FanPresenceTracker();
        tracker.Observe(Snapshot(Fan(rpm: 1200, duty: 60)));

        Assert.True(tracker.HasEverSpun(FanId));
    }

    [Fact]
    public void A_header_that_never_turns_is_empty()
    {
        var tracker = new FanPresenceTracker();

        for (var i = 0; i < 10; i++)
        {
            tracker.Observe(Snapshot(Fan(rpm: 0, duty: 60)));
        }

        Assert.True(tracker.IsEmpty(Fan(rpm: 0, duty: 60)));
    }

    [Fact]
    public void A_fan_slowing_to_a_stop_is_not_suddenly_empty()
    {
        // The reported bug. Dragging the slider to zero makes the chip travel down,
        // and the fan stops turning at roughly 20 % — well before the duty reaches
        // zero. For those seconds the reading is "driven, no tachometer", which is
        // also what an empty header looks like, so the card vanished mid-drag.
        var tracker = new FanPresenceTracker();
        tracker.Observe(Snapshot(Fan(rpm: 1200, duty: 60)));

        var coastingDown = Fan(rpm: 0, duty: 30);

        Assert.True(coastingDown.LooksUnpopulated);
        Assert.False(tracker.IsEmpty(coastingDown));
    }

    [Fact]
    public void A_fan_spinning_up_again_is_not_suddenly_empty()
    {
        // The same thing on the way back: handing a stopped fan to the firmware
        // raises the duty before the blades move.
        var tracker = new FanPresenceTracker();
        tracker.Observe(Snapshot(Fan(rpm: 1200, duty: 60)));
        tracker.Observe(Snapshot(Fan(rpm: 0, duty: 0)));

        Assert.False(tracker.IsEmpty(Fan(rpm: 0, duty: 45)));
    }

    [Fact]
    public void A_fan_stopped_at_zero_duty_is_not_empty_either()
    {
        // Nothing is being asked of it, so its silence says nothing. This case never
        // needed the tracker — it is covered by the instantaneous test — but it must
        // not regress, or stopping a fan would hide it.
        Assert.False(new FanPresenceTracker().IsEmpty(Fan(rpm: 0, duty: 0)));
    }

    [Fact]
    public void Tracks_headers_independently()
    {
        var tracker = new FanPresenceTracker();
        tracker.Observe(Snapshot(
            Fan(rpm: 1200, duty: 60),
            Fan(rpm: 0, duty: 60, id: "empty-header")));

        Assert.True(tracker.HasEverSpun(FanId));
        Assert.False(tracker.HasEverSpun("empty-header"));
    }

    [Fact]
    public void Clearing_forgets_everything()
    {
        var tracker = new FanPresenceTracker();
        tracker.Observe(Snapshot(Fan(rpm: 1200, duty: 60)));

        tracker.Clear();

        Assert.False(tracker.HasEverSpun(FanId));
    }
}
