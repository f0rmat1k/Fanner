namespace Fanner.Core.Model;

/// <summary>
/// What a sensor physically measures. Determines unit, formatting and which
/// sensors are eligible to drive a fan curve.
/// </summary>
public enum SensorKind
{
    Unknown = 0,

    /// <summary>Degrees Celsius.</summary>
    Temperature,

    /// <summary>Fan tachometer reading, RPM.</summary>
    FanSpeed,

    /// <summary>Fan control output (PWM or DC), percent of full scale.</summary>
    Control,

    /// <summary>Volts.</summary>
    Voltage,

    /// <summary>Watts.</summary>
    Power,

    /// <summary>Utilisation, percent.</summary>
    Load,

    /// <summary>Megahertz.</summary>
    Clock,

    /// <summary>Litres per hour, for AIO pumps and flow meters.</summary>
    Flow,
}

public static class SensorKindExtensions
{
    public static string Unit(this SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.FanSpeed => "RPM",
        SensorKind.Control => "%",
        SensorKind.Voltage => "V",
        SensorKind.Power => "W",
        SensorKind.Load => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Flow => "L/h",
        _ => string.Empty,
    };

    /// <summary>
    /// Decimal places used when rendering a value of this kind. RPM and MHz are
    /// whole numbers; volts need three places to be meaningful.
    /// </summary>
    public static int Precision(this SensorKind kind) => kind switch
    {
        SensorKind.FanSpeed => 0,
        SensorKind.Clock => 0,
        SensorKind.Voltage => 3,
        SensorKind.Temperature => 1,
        _ => 1,
    };
}
