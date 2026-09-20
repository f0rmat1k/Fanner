using Fanner.Core.Curves;

namespace Fanner.Core.Tests;

public class FanCurveTests
{
    private static FanCurve Ramp() => new(
    [
        new CurvePoint(40, 20),
        new CurvePoint(60, 60),
        new CurvePoint(80, 100),
    ]);

    [Theory]
    [InlineData(40, 20)]
    [InlineData(50, 40)]
    [InlineData(60, 60)]
    [InlineData(70, 80)]
    [InlineData(80, 100)]
    public void Interpolates_between_points(double temperature, double expected) =>
        Assert.Equal(expected, Ramp().Evaluate(temperature), precision: 6);

    [Theory]
    [InlineData(-20, 20)]
    [InlineData(39.9, 20)]
    [InlineData(95, 100)]
    public void Holds_flat_outside_the_defined_range(double temperature, double expected)
    {
        // Extrapolating instead would let this curve demand 130 % at 95 °C and a
        // negative duty on a cold morning.
        Assert.Equal(expected, Ramp().Evaluate(temperature), precision: 6);
    }

    [Fact]
    public void Sorts_points_given_out_of_order()
    {
        var curve = new FanCurve(
        [
            new CurvePoint(80, 100),
            new CurvePoint(40, 20),
            new CurvePoint(60, 60),
        ]);

        Assert.Equal([40, 60, 80], curve.Points.Select(p => p.TemperatureC));
        Assert.Equal(40, curve.Evaluate(50), precision: 6);
    }

    [Fact]
    public void Handles_two_points_at_the_same_temperature()
    {
        // A vertical step is a legitimate shape — "silent until 70, then loud" — and
        // must not divide by zero.
        var curve = new FanCurve(
        [
            new CurvePoint(40, 0),
            new CurvePoint(70, 0),
            new CurvePoint(70, 80),
            new CurvePoint(90, 100),
        ]);

        Assert.Equal(0, curve.Evaluate(69), precision: 6);
        Assert.Equal(80, curve.Evaluate(70), precision: 6);
    }

    [Fact]
    public void Clamps_points_into_a_usable_range()
    {
        var curve = new FanCurve([new CurvePoint(40, -30), new CurvePoint(80, 400)]);

        Assert.Equal(0, curve.Points[0].DutyPercent);
        Assert.Equal(100, curve.Points[1].DutyPercent);
    }

    [Fact]
    public void Rejects_a_curve_with_too_few_points() =>
        Assert.Throws<ArgumentException>(() => new FanCurve([new CurvePoint(40, 20)]));

    [Fact]
    public void Presets_rise_monotonically()
    {
        foreach (var curve in new[] { FanCurve.Silent(), FanCurve.Balanced(), FanCurve.Performance() })
        {
            var duties = curve.Points.Select(p => p.DutyPercent).ToList();

            // A preset that dips would speed a fan up and then slow it down as things
            // get hotter, which no user would ever intend.
            Assert.Equal(duties.OrderBy(d => d), duties);
        }
    }
}
