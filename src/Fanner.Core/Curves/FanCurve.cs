namespace Fanner.Core.Curves;

/// <summary>One vertex of a fan curve: at this temperature, run this duty.</summary>
public sealed record CurvePoint(double TemperatureC, double DutyPercent)
{
    public CurvePoint Clamped() => new(
        Math.Clamp(TemperatureC, -50, 150),
        Math.Clamp(DutyPercent, 0, 100));
}

/// <summary>
/// A temperature-to-duty mapping, interpolated linearly between its points.
/// </summary>
/// <remarks>
/// Immutable. The engine holds all the moving parts — smoothing, hysteresis, the
/// state of a stalled fan — so the curve itself stays a pure function and can be
/// compared, serialised and reasoned about on its own.
/// </remarks>
public sealed class FanCurve
{
    private readonly CurvePoint[] _points;

    /// <param name="points">
    /// At least two, in any order; they are sorted and clamped on construction.
    /// </param>
    public FanCurve(IEnumerable<CurvePoint> points)
    {
        _points = points
            .Select(p => p.Clamped())
            .OrderBy(p => p.TemperatureC)
            .ToArray();

        if (_points.Length < 2)
        {
            throw new ArgumentException("A curve needs at least two points.", nameof(points));
        }
    }

    public IReadOnlyList<CurvePoint> Points => _points;

    /// <summary>The lowest duty this curve can ask for, which is its first point.</summary>
    public double MinimumDuty => _points[0].DutyPercent;

    /// <summary>
    /// Duty for a given temperature. Flat outside the defined range rather than
    /// extrapolated — continuing the last segment past the final point would have a
    /// steep curve demanding 140 % at 90 °C, and past the first would let a cold
    /// reading drive the duty negative.
    /// </summary>
    public double Evaluate(double temperatureC)
    {
        if (temperatureC < _points[0].TemperatureC)
        {
            return _points[0].DutyPercent;
        }

        if (temperatureC > _points[^1].TemperatureC)
        {
            return _points[^1].DutyPercent;
        }

        // The last point at or below the reading. Scanning for the *last* rather
        // than the first matters where two points share a temperature: that pair is
        // a vertical step, and landing exactly on it means the step has happened, so
        // the upper duty applies. Picking the first would leave a "silent until 70,
        // then 80 %" curve reading 0 % at exactly 70.
        var index = 0;
        for (var i = 1; i < _points.Length; i++)
        {
            if (_points[i].TemperatureC <= temperatureC)
            {
                index = i;
            }
        }

        var lower = _points[index];

        if (index == _points.Length - 1 || lower.TemperatureC == temperatureC)
        {
            return lower.DutyPercent;
        }

        var upper = _points[index + 1];
        var span = upper.TemperatureC - lower.TemperatureC;
        var t = (temperatureC - lower.TemperatureC) / span;

        return lower.DutyPercent + (upper.DutyPercent - lower.DutyPercent) * t;
    }

    /// <summary>Quiet until it has to be otherwise: flat and low, then a late ramp.</summary>
    public static FanCurve Silent() => new(
    [
        new CurvePoint(30, 20),
        new CurvePoint(55, 25),
        new CurvePoint(70, 45),
        new CurvePoint(85, 100),
    ]);

    /// <summary>A gentle slope across the usable range.</summary>
    public static FanCurve Balanced() => new(
    [
        new CurvePoint(30, 30),
        new CurvePoint(50, 45),
        new CurvePoint(65, 70),
        new CurvePoint(80, 100),
    ]);

    /// <summary>Airflow first: high floor, reaches maximum early.</summary>
    public static FanCurve Performance() => new(
    [
        new CurvePoint(30, 50),
        new CurvePoint(45, 70),
        new CurvePoint(60, 90),
        new CurvePoint(70, 100),
    ]);
}
