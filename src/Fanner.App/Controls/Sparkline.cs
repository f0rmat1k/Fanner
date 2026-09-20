using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fanner.App.Controls;

/// <summary>
/// Draws a rolling series as a filled line. Small enough to sit inside a fan card,
/// so it carries no axes, labels or legend — the numbers beside it do that job.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<float>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(StrokeThickness), 1.5);

    /// <summary>
    /// Lower bound of the drawn range. Leave unset to scale to the data.
    /// Pin it for percentages, where 0–100 is the meaningful scale and autoscaling
    /// would make a flat 60% look like turbulence.
    /// </summary>
    public static readonly StyledProperty<double?> MinimumProperty =
        AvaloniaProperty.Register<Sparkline, double?>(nameof(Minimum));

    public static readonly StyledProperty<double?> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double?>(nameof(Maximum));

    static Sparkline()
    {
        AffectsRender<Sparkline>(
            ValuesProperty,
            StrokeProperty,
            StrokeThicknessProperty,
            MinimumProperty,
            MaximumProperty);
    }

    public IReadOnlyList<float>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double? Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double? Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values is null || values.Count < 2 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        if (!TryGetRange(values, out var min, out var max))
        {
            return;
        }

        var stroke = Stroke ?? Brushes.DodgerBlue;
        var pen = new Pen(stroke, StrokeThickness, lineJoin: PenLineJoin.Round);

        var width = Bounds.Width;
        var height = Bounds.Height;
        var span = max - min;
        var stepX = width / (values.Count - 1);

        // Inset by half the stroke so the line is not clipped at the top and bottom.
        var inset = StrokeThickness / 2;
        var usableHeight = Math.Max(height - StrokeThickness, 0.0);

        var geometry = new StreamGeometry();
        using (var draw = geometry.Open())
        {
            var open = false;

            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];

                // NaN marks a poll where the channel had no reading. Break the line
                // rather than drawing straight through the gap.
                if (float.IsNaN(value))
                {
                    open = false;
                    continue;
                }

                var point = new Point(
                    i * stepX,
                    inset + usableHeight - ((value - min) / span * usableHeight));

                if (open)
                {
                    draw.LineTo(point);
                }
                else
                {
                    draw.BeginFigure(point, isFilled: false);
                    open = true;
                }
            }

            if (open)
            {
                draw.EndFigure(false);
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    /// Works out the vertical range, honouring any pinned bound. Returns false when
    /// the series is entirely gaps, or flat enough that a range would be degenerate.
    /// </summary>
    private bool TryGetRange(IReadOnlyList<float> values, out double min, out double max)
    {
        min = Minimum ?? double.MaxValue;
        max = Maximum ?? double.MinValue;

        if (Minimum is null || Maximum is null)
        {
            var any = false;

            foreach (var value in values)
            {
                if (float.IsNaN(value))
                {
                    continue;
                }

                any = true;

                if (Minimum is null) min = Math.Min(min, value);
                if (Maximum is null) max = Math.Max(max, value);
            }

            if (!any)
            {
                return false;
            }
        }

        // A dead-flat series has no range to scale to. Open it out slightly so the
        // line lands mid-height instead of collapsing onto an edge.
        if (max - min < 1e-6)
        {
            var centre = (max + min) / 2;
            min = centre - 0.5;
            max = centre + 0.5;
        }

        return true;
    }
}
