using Fanner.Core.Model;

namespace Fanner.Core.Safety;

/// <summary>A temperature that crossed its limit while Fanner was driving the fans.</summary>
public sealed record WatchdogTrip(
    string SensorName,
    string HardwareName,
    HardwareCategory Category,
    double Temperature,
    double Threshold,
    DateTimeOffset At)
{
    public string Message =>
        $"{SensorName} on {HardwareName} reached {Temperature:0.#} °C "
        + $"(limit {Threshold:0} °C). Every fan was handed back to the motherboard.";
}

/// <summary>
/// Watches temperatures while Fanner is driving fans, and says when to stop.
/// </summary>
/// <remarks>
/// The job is narrow on purpose: undo our own intervention. It arms only while at
/// least one header is under software control, because if the firmware is driving
/// everything then a high temperature is the firmware's problem and releasing fans
/// we do not hold would achieve nothing.
/// <para>
/// Handing control back — rather than ramping to 100% — is the deliberate response.
/// The board's own curve is the known-good configuration, and a duty cycle we
/// calculated is exactly what is in question at the moment the limit is crossed.
/// </para>
/// </remarks>
public sealed class SafetyWatchdog
{
    /// <summary>
    /// Per-category limits, in Celsius.
    /// </summary>
    /// <remarks>
    /// These are "something is wrong" values, not "getting warm" values, and they are
    /// deliberately high. A Ryzen 9800X3D sits at its 95 °C Tjmax under sustained
    /// load by design — it is a thermally-limited part, not an overheating one — so a
    /// limit of 90 would fire during any ordinary gaming session and train the user to
    /// ignore the warning. Storage and memory are unwatched: NVMe throttles itself,
    /// and releasing a case fan does not rescue a hot DIMM quickly enough to matter.
    /// </remarks>
    private readonly Dictionary<HardwareCategory, double> _thresholds = new()
    {
        [HardwareCategory.Cpu] = 95,
        [HardwareCategory.Gpu] = 90,
    };

    /// <summary>
    /// Extra headroom for a hot spot or junction reading, in Celsius.
    /// </summary>
    /// <remarks>
    /// These are not the same measurement as the die temperature they sit beside.
    /// The hot spot is the single warmest point the card can find, and it runs ten to
    /// twenty degrees above the core by design — an RTX 4090 reports a hot spot in
    /// the nineties during any ordinary game, while its own throttle point is above a
    /// hundred. Judging it by the core's limit means reporting an emergency every
    /// time someone plays something.
    /// </remarks>
    private const double JunctionAllowanceC = 15;

    /// <summary>
    /// Set once a limit is crossed, cleared once everything drops back below
    /// limit minus hysteresis. Stops one hot excursion from reporting every second.
    /// </summary>
    private bool _reportedThisExcursion;

    /// <summary>
    /// How far a temperature must fall below its limit before a new excursion can be
    /// reported. Without it, a reading hovering on the boundary reports repeatedly.
    /// </summary>
    public double HysteresisC { get; init; } = 5;

    public IReadOnlyDictionary<HardwareCategory, double> Thresholds => _thresholds;

    /// <summary>One line describing the active limits, for the UI to show while armed.</summary>
    public string Summary => string.Join(
        " · ",
        _thresholds
            .OrderBy(t => t.Key.SortOrder())
            .Select(t => $"{Describe(t.Key)} {t.Value:0} °C"));

    /// <summary>
    /// Returns a trip when the fans should be handed back, or null to carry on.
    /// Pure: the caller performs the release.
    /// </summary>
    public WatchdogTrip? Evaluate(HardwareSnapshot snapshot)
    {
        var watched = snapshot.Temperatures
            .Where(t => t.Value is not null && _thresholds.ContainsKey(t.Category))
            .ToList();

        // Clear the latch once every watched sensor is comfortably back down, so a
        // later excursion is reported afresh.
        if (watched.All(t => t.Value < ThresholdFor(t) - HysteresisC))
        {
            _reportedThisExcursion = false;
        }

        if (_reportedThisExcursion)
        {
            return null;
        }

        foreach (var sensor in watched)
        {
            if (!WeDriveFansThatAffect(sensor.Category, snapshot))
            {
                continue;
            }

            var threshold = ThresholdFor(sensor);

            if (sensor.Value >= threshold)
            {
                _reportedThisExcursion = true;

                return new WatchdogTrip(
                    sensor.Name,
                    sensor.HardwareName,
                    sensor.Category,
                    sensor.Value.Value,
                    threshold,
                    snapshot.Timestamp);
            }
        }

        return null;
    }

    /// <summary>Overrides a category limit, or adds one that is not watched by default.</summary>
    public void SetThreshold(HardwareCategory category, double celsius) =>
        _thresholds[category] = celsius;

    /// <summary>
    /// Whether any fan we are driving could have caused this temperature — the whole
    /// question the watchdog exists to answer.
    /// </summary>
    /// <remarks>
    /// A graphics card runs its own fans off its own curve. If we are not driving
    /// them, a hot GPU is not our doing and handing the case fans back to the
    /// motherboard does nothing about it: at best it changes nothing, at worst it
    /// takes away the airflow that was helping. The card throttles itself long
    /// before anything is at risk.
    /// </remarks>
    private static bool WeDriveFansThatAffect(HardwareCategory sensor, HardwareSnapshot snapshot)
    {
        var ours = snapshot.Fans.Where(f => f.Mode == FanControlMode.Software);

        return sensor == HardwareCategory.Gpu
            ? ours.Any(f => f.Category == HardwareCategory.Gpu)
            : ours.Any(f => f.Category != HardwareCategory.Gpu);
    }

    private double ThresholdFor(SensorReading sensor) =>
        _thresholds[sensor.Category] + (IsJunction(sensor.Name) ? JunctionAllowanceC : 0);

    /// <summary>
    /// True for the warmest-point readings a chip publishes beside its die
    /// temperature: hot spot on a graphics core, junction on its memory.
    /// </summary>
    private static bool IsJunction(string name) =>
        name.Contains("hot spot", StringComparison.OrdinalIgnoreCase)
        || name.Contains("hotspot", StringComparison.OrdinalIgnoreCase)
        || name.Contains("junction", StringComparison.OrdinalIgnoreCase);

    private static string Describe(HardwareCategory category) => category switch
    {
        HardwareCategory.Cpu => "CPU",
        HardwareCategory.Gpu => "GPU",
        HardwareCategory.Motherboard => "board",
        HardwareCategory.Storage => "storage",
        HardwareCategory.Memory => "memory",
        HardwareCategory.Cooler => "cooler",
        HardwareCategory.Psu => "PSU",
        _ => category.ToString(),
    };
}
