namespace Fanner.Core.Model;

/// <summary>
/// The complete state of every monitored channel at one instant. Produced by a
/// backend poll; immutable, so it can be handed to the UI thread without locking.
/// </summary>
public sealed record HardwareSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<FanSnapshot> Fans,
    IReadOnlyList<SensorReading> Temperatures,
    IReadOnlyList<SensorReading> Others)
{
    public static HardwareSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        [],
        [],
        []);

    /// <summary>Every reading in one sequence, for lookup by id.</summary>
    public IEnumerable<SensorReading> AllSensors => Temperatures.Concat(Others);
}
