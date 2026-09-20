namespace Fanner.Core.Curves;

/// <summary>
/// Ties one fan header to one temperature source through a curve, with the
/// behaviour that turns a bare mapping into something usable on real hardware.
/// </summary>
public sealed record CurveBinding
{
    public required string FanId { get; init; }

    /// <summary>Sensor whose reading drives the curve.</summary>
    public required string SensorId { get; init; }

    public required FanCurve Curve { get; init; }

    /// <summary>
    /// Time constant for smoothing the source temperature.
    /// </summary>
    /// <remarks>
    /// A CPU temperature moves ten degrees in a second when a core wakes up. Feeding
    /// that straight into a curve makes the fans surge and sink constantly, which is
    /// far more irritating than a steady speed slightly too high. Smoothing trades a
    /// few seconds of response for quiet.
    /// </remarks>
    public TimeSpan Response { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Smallest duty change worth writing, in percent.
    /// </summary>
    /// <remarks>
    /// Without a deadband the duty shuffles by a fraction of a percent every poll,
    /// which on an NCT6687D means a write per second forever and a fan that never
    /// quite settles.
    /// </remarks>
    public double HysteresisPercent { get; init; } = 2;

    /// <summary>
    /// Duty used briefly to get a stopped fan turning, or 0 to disable.
    /// </summary>
    /// <remarks>
    /// Most fans cannot start from a standstill at the duty that keeps them
    /// spinning — a curve asking for 15 % on a stopped fan leaves it stopped, and
    /// the user sees a dead fan and rising temperatures. A short kick gets it
    /// moving, after which the curve's own value holds fine.
    /// </remarks>
    public double KickstartDuty { get; init; } = 60;

    /// <summary>
    /// How many consecutive polls may kick before giving up.
    /// </summary>
    /// <remarks>
    /// An empty header never reports RPM however hard it is driven. Without a cap,
    /// such a header would sit at the kickstart duty forever.
    /// </remarks>
    public int MaxKickstartPolls { get; init; } = 3;
}
