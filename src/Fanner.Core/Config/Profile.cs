namespace Fanner.Core.Config;

/// <summary>A fan held at a fixed duty, with no curve.</summary>
public sealed record ManualDutyConfig
{
    public string FanId { get; init; } = string.Empty;

    public double DutyPercent { get; init; }
}

/// <summary>
/// A named set of fan settings the user can switch between.
/// </summary>
/// <remarks>
/// Holds both curves and fixed duties, because a real setup mixes them: a curve on
/// the CPU fan, the pump pinned at 100 %, the rest left to the firmware. A fan
/// absent from both lists is deliberately not mentioned — switching to this profile
/// hands it back to the motherboard.
/// </remarks>
public sealed record Profile
{
    public const string DefaultName = "Default";

    public string Name { get; init; } = DefaultName;

    public List<CurveBindingConfig> Bindings { get; init; } = [];

    public List<ManualDutyConfig> ManualDuties { get; init; } = [];

    /// <summary>True when this profile leaves every fan to the firmware.</summary>
    public bool IsEmpty => Bindings.Count == 0 && ManualDuties.Count == 0;
}
