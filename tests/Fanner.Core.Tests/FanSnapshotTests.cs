using Fanner.Core.Model;

namespace Fanner.Core.Tests;

/// <summary>
/// Covers the empty-header heuristic, which decides what the dashboard hides.
/// Getting it wrong is not cosmetic in either direction: too loose and a fan the
/// user just stopped disappears, too strict and ten headers bury the six real ones.
/// </summary>
public class FanSnapshotTests
{
    private static FanSnapshot Header(int? rpm, double? duty) => new(
        Id: "/lpc/nct6687dr/0/header/1",
        Name: "Pump Fan #1",
        HardwareName: "Nuvoton NCT6687D-R",
        Rpm: rpm,
        DutyPercent: duty,
        CanControl: true,
        Mode: FanControlMode.Firmware);

    [Theory]
    [InlineData(0, 100.0)]  // driven hard, nothing spins
    [InlineData(0, 60.0)]
    [InlineData(null, 60.0)]
    [InlineData(0, null)]   // no control channel either
    public void Reports_unpopulated_when_driven_but_no_tach(int? rpm, double? duty) =>
        Assert.True(Header(rpm, duty).LooksUnpopulated);

    [Fact]
    public void Does_not_report_unpopulated_when_duty_is_zero()
    {
        // A fan commanded off also reads 0 RPM. Hiding it would make the fan the
        // user just stopped vanish from the dashboard.
        Assert.False(Header(0, 0).LooksUnpopulated);
    }

    [Fact]
    public void Does_not_report_unpopulated_when_the_tach_is_alive() =>
        Assert.False(Header(1194, 60).LooksUnpopulated);

    [Fact]
    public void Treats_a_rounding_level_duty_as_off()
    {
        // Some chips report a hair above zero rather than a clean 0.
        Assert.False(Header(0, 0.3).LooksUnpopulated);
    }
}
