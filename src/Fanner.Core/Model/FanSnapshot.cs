namespace Fanner.Core.Model;

/// <summary>
/// Who is currently deciding this fan's duty cycle.
/// </summary>
public enum FanControlMode
{
    /// <summary>The backend cannot tell. Treated as <see cref="Firmware"/> for safety.</summary>
    Unknown = 0,

    /// <summary>The motherboard firmware or embedded controller owns the fan.</summary>
    Firmware,

    /// <summary>Fanner has taken the fan over and is writing its duty cycle.</summary>
    Software,
}

/// <summary>
/// A fan header: its tachometer, its control output, and whether we may write to it.
/// Tach and control are separate channels in the hardware but always belong to one
/// physical header, so they are paired here rather than surfaced as loose sensors.
/// </summary>
/// <param name="Id">Stable identifier of the header, used to persist per-fan settings.</param>
/// <param name="Rpm">
/// Tachometer reading. <c>null</c> when nothing is plugged in, or when the fan is
/// stopped — a zero-RPM fan and an empty header look the same from here.
/// </param>
/// <param name="DutyPercent">Control output, 0–100. <c>null</c> if the header reports no control channel.</param>
/// <param name="CanControl">Whether the backend can write a duty cycle to this header.</param>
/// <param name="MinDuty">
/// Lowest duty the hardware accepts. Not always 0 — an NVIDIA GPU refuses anything
/// under 30, so the UI must clamp its slider to this rather than to zero.
/// </param>
/// <param name="MaxDuty">Highest duty the hardware accepts, usually 100.</param>
public sealed record FanSnapshot(
    string Id,
    string Name,
    string HardwareName,
    int? Rpm,
    double? DutyPercent,
    bool CanControl,
    FanControlMode Mode,
    double MinDuty = 0,
    double MaxDuty = 100,
    HardwareCategory Category = HardwareCategory.Other)
{
    /// <summary>
    /// True when the header is being driven but nothing is spinning — almost always
    /// an empty header, so the UI hides these behind a toggle.
    /// </summary>
    /// <remarks>
    /// The absence of a duty reading is not the test. Firmware reports a duty for
    /// every header it owns, wired or not, so an empty one still shows "60 %".
    /// What distinguishes it is the mismatch: power is going out and no tach is
    /// coming back.
    /// <para>
    /// A header at zero duty is excluded deliberately. A fan commanded off also
    /// reads 0 RPM, and hiding it would make the fan the user just stopped vanish
    /// from the dashboard.
    /// </para>
    /// </remarks>
    public bool LooksUnpopulated => Rpm is null or 0 && (DutyPercent ?? 100) > 0.5;
}
