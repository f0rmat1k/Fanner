using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fanner.Core.Curves;

namespace Fanner.App.Controls;

public sealed class CurvePointMovedEventArgs(int index, CurvePoint point) : EventArgs
{
    public int Index { get; } = index;

    public CurvePoint Point { get; } = point;
}

/// <summary>
/// An editable temperature-to-duty curve: drag the points, watch the marker.
/// </summary>
/// <remarks>
/// The live marker is the reason this is a custom control rather than a static
/// plot. Seeing where the current temperature lands on the curve is what makes the
/// shape mean something — without it, a curve editor is a drawing exercise.
/// </remarks>
public sealed class CurveEditor : Control
{
    /// <summary>Gutters for the axis labels.</summary>
    private const double LeftGutter = 30;
    private const double BottomGutter = 20;
    private const double Inset = 8;

    /// <summary>How close the pointer must be, in pixels, to grab a point.</summary>
    private const double GrabRadius = 14;

    public static readonly StyledProperty<IReadOnlyList<CurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<CurveEditor, IReadOnlyList<CurvePoint>?>(nameof(Points));

    /// <summary>Where the bound sensor currently reads, or null when unavailable.</summary>
    public static readonly StyledProperty<double?> MarkerTemperatureProperty =
        AvaloniaProperty.Register<CurveEditor, double?>(nameof(MarkerTemperature));

    public static readonly StyledProperty<double> MinTemperatureProperty =
        AvaloniaProperty.Register<CurveEditor, double>(nameof(MinTemperature), 20);

    public static readonly StyledProperty<double> MaxTemperatureProperty =
        AvaloniaProperty.Register<CurveEditor, double>(nameof(MaxTemperature), 100);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(MarkerBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(LabelBrush));

    private int _draggingIndex = -1;

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(
            PointsProperty,
            MarkerTemperatureProperty,
            MinTemperatureProperty,
            MaxTemperatureProperty,
            StrokeProperty,
            MarkerBrushProperty,
            GridBrushProperty,
            LabelBrushProperty);
    }

    public CurveEditor() => Focusable = true;

    /// <summary>Raised while a point is dragged. The owner validates and re-publishes.</summary>
    public event EventHandler<CurvePointMovedEventArgs>? PointMoved;

    public IReadOnlyList<CurvePoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double? MarkerTemperature
    {
        get => GetValue(MarkerTemperatureProperty);
        set => SetValue(MarkerTemperatureProperty, value);
    }

    public double MinTemperature
    {
        get => GetValue(MinTemperatureProperty);
        set => SetValue(MinTemperatureProperty, value);
    }

    public double MaxTemperature
    {
        get => GetValue(MaxTemperatureProperty);
        set => SetValue(MaxTemperatureProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var plot = PlotArea();
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        var grid = GridBrush ?? Brushes.Gray;
        var labels = LabelBrush ?? Brushes.Gray;
        var stroke = Stroke ?? Brushes.DodgerBlue;

        DrawGrid(context, plot, grid, labels);

        var points = Points;
        if (points is null || points.Count < 2)
        {
            return;
        }

        DrawCurve(context, plot, points, stroke);
        DrawMarker(context, plot, points);
    }

    private void DrawGrid(DrawingContext context, Rect plot, IBrush grid, IBrush labels)
    {
        var pen = new Pen(grid, 1);

        for (var duty = 0; duty <= 100; duty += 25)
        {
            var y = DutyToY(plot, duty);
            context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            context.DrawText(Label($"{duty}", labels, 9), new Point(2, y - 7));
        }

        var step = (MaxTemperature - MinTemperature) / 4;
        for (var i = 0; i <= 4; i++)
        {
            var temperature = MinTemperature + step * i;
            var x = TemperatureToX(plot, temperature);
            context.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawText(Label($"{temperature:0}°", labels, 9), new Point(x - 8, plot.Bottom + 3));
        }
    }

    private void DrawCurve(DrawingContext context, Rect plot, IReadOnlyList<CurvePoint> points, IBrush stroke)
    {
        var pen = new Pen(stroke, 2, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();

        using (var draw = geometry.Open())
        {
            // Flat shoulders on both sides, matching how the curve actually
            // evaluates outside its defined range.
            draw.BeginFigure(new Point(plot.Left, DutyToY(plot, points[0].DutyPercent)), isFilled: false);

            foreach (var point in points)
            {
                draw.LineTo(ToCanvas(plot, point));
            }

            draw.LineTo(new Point(plot.Right, DutyToY(plot, points[^1].DutyPercent)));
            draw.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);

        for (var i = 0; i < points.Count; i++)
        {
            var centre = ToCanvas(plot, points[i]);
            var radius = i == _draggingIndex ? 7.0 : 5.0;

            context.DrawEllipse(stroke, null, centre, radius, radius);
        }
    }

    private void DrawMarker(DrawingContext context, Rect plot, IReadOnlyList<CurvePoint> points)
    {
        if (MarkerTemperature is not { } temperature)
        {
            return;
        }

        var brush = MarkerBrush ?? Brushes.Orange;
        var x = TemperatureToX(plot, temperature);

        context.DrawLine(
            new Pen(brush, 1, DashStyle.Dash),
            new Point(x, plot.Top),
            new Point(x, plot.Bottom));

        var duty = new FanCurve(points).Evaluate(temperature);
        context.DrawEllipse(brush, null, new Point(x, DutyToY(plot, duty)), 4, 4);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var points = Points;
        if (points is null || points.Count == 0)
        {
            return;
        }

        var plot = PlotArea();
        var position = e.GetPosition(this);

        var nearest = -1;
        var best = GrabRadius;

        for (var i = 0; i < points.Count; i++)
        {
            var distance = Distance(ToCanvas(plot, points[i]), position);
            if (distance <= best)
            {
                best = distance;
                nearest = i;
            }
        }

        if (nearest < 0)
        {
            return;
        }

        _draggingIndex = nearest;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_draggingIndex < 0 || Points is not { } points || _draggingIndex >= points.Count)
        {
            return;
        }

        var plot = PlotArea();
        var position = e.GetPosition(this);

        var moved = new CurvePoint(
            Math.Clamp(XToTemperature(plot, position.X), MinTemperature, MaxTemperature),
            Math.Clamp(YToDuty(plot, position.Y), 0, 100));

        PointMoved?.Invoke(this, new CurvePointMovedEventArgs(_draggingIndex, moved));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_draggingIndex >= 0)
        {
            _draggingIndex = -1;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
    }

    private Rect PlotArea() => new(
        LeftGutter,
        Inset,
        Math.Max(Bounds.Width - LeftGutter - Inset, 0),
        Math.Max(Bounds.Height - BottomGutter - Inset, 0));

    private Point ToCanvas(Rect plot, CurvePoint point) => new(
        TemperatureToX(plot, point.TemperatureC),
        DutyToY(plot, point.DutyPercent));

    private double TemperatureToX(Rect plot, double temperature)
    {
        var span = Math.Max(MaxTemperature - MinTemperature, 1);
        return plot.Left + (temperature - MinTemperature) / span * plot.Width;
    }

    private double XToTemperature(Rect plot, double x)
    {
        var span = Math.Max(MaxTemperature - MinTemperature, 1);
        return MinTemperature + (x - plot.Left) / Math.Max(plot.Width, 1) * span;
    }

    private static double DutyToY(Rect plot, double duty) =>
        plot.Bottom - duty / 100.0 * plot.Height;

    private static double YToDuty(Rect plot, double y) =>
        (plot.Bottom - y) / Math.Max(plot.Height, 1) * 100.0;

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static FormattedText Label(string text, IBrush brush, double size) => new(
        text,
        System.Globalization.CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        Typeface.Default,
        size,
        brush);
}
