using System.Globalization;

namespace Fanner.Core.Model;

/// <summary>
/// One sensor value at one moment in time.
/// </summary>
/// <param name="Id">
/// Stable identifier, unique across the whole backend. Survives restarts, so it
/// is safe to persist in a profile: curves reference sensors by this.
/// </param>
/// <param name="Name">Human-readable channel name, e.g. "CPU Fan" or "Core (Tctl/Tdie)".</param>
/// <param name="HardwareName">The chip or device the channel belongs to, e.g. "Nuvoton NCT6687D".</param>
/// <param name="Value">
/// Current value, or <c>null</c> when the channel exists but has no reading — an
/// unpopulated header, or a tachometer on a fan that is stopped and not reporting.
/// </param>
public sealed record SensorReading(
    string Id,
    string Name,
    string HardwareName,
    SensorKind Kind,
    double? Value,
    HardwareCategory Category = HardwareCategory.Other)
{
    public string Unit => Kind.Unit();

    /// <summary>Value and unit ready for display, or an em dash when unavailable.</summary>
    public string Display => Value is null
        ? "—"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Round(Value.Value, Kind.Precision())} {Unit}");
}
