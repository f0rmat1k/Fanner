using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanner.Core.Curves;

namespace Fanner.App.ViewModels;

/// <summary>
/// Editing state for one fan's curve. Every change applies immediately — the fan
/// reacting is the feedback that makes the shape meaningful, and an Apply button
/// would leave the editor showing something the hardware is not doing.
/// </summary>
public sealed partial class CurveEditorViewModel : ViewModelBase
{
    private readonly Action<CurveEditorViewModel> _changed;
    private readonly Action _removed;

    public CurveEditorViewModel(
        FanViewModel fan,
        IReadOnlyList<SensorOption> sensors,
        SensorOption selectedSensor,
        FanCurve curve,
        Action<CurveEditorViewModel> changed,
        Action removed)
    {
        Fan = fan;
        Sensors = sensors;
        _changed = changed;
        _removed = removed;

        SelectedSensor = selectedSensor;
        Points = curve.Points.ToArray();
    }

    public FanViewModel Fan { get; }

    public IReadOnlyList<SensorOption> Sensors { get; }

    public string FanName => Fan.Name;

    /// <summary>
    /// Replaced wholesale on every edit rather than mutated. The editor redraws on a
    /// property change, not on a collection change, and an immutable list is also
    /// what <see cref="FanCurve"/> wants.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<CurvePoint> Points { get; set; }

    [ObservableProperty]
    public partial SensorOption SelectedSensor { get; set; }

    /// <summary>Live reading of the bound sensor, drawn as the marker.</summary>
    [ObservableProperty]
    public partial double? MarkerTemperature { get; set; }

    [ObservableProperty]
    public partial double ResponseSeconds { get; set; } = 5;

    [ObservableProperty]
    public partial double HysteresisPercent { get; set; } = 2;

    [ObservableProperty]
    public partial double KickstartDuty { get; set; } = 60;

    /// <summary>Duty this curve would ask for at the current reading.</summary>
    public string PredictedDuty => MarkerTemperature is { } temperature && Points.Count >= 2
        ? $"{new FanCurve(Points).Evaluate(temperature):0} %"
        : "—";

    public FanCurve BuildCurve() => new(Points);

    /// <summary>
    /// Moves one vertex. Points are kept in temperature order but are not prevented
    /// from crossing: dragging one past its neighbour simply re-sorts, which is what
    /// the gesture looks like it should do.
    /// </summary>
    public void MovePoint(int index, CurvePoint point)
    {
        if (index < 0 || index >= Points.Count)
        {
            return;
        }

        var updated = Points.ToArray();
        updated[index] = point;

        Points = updated.OrderBy(p => p.TemperatureC).ToArray();
        _changed(this);
    }

    [RelayCommand]
    private void UseSilent() => Replace(FanCurve.Silent());

    [RelayCommand]
    private void UseBalanced() => Replace(FanCurve.Balanced());

    [RelayCommand]
    private void UsePerformance() => Replace(FanCurve.Performance());

    /// <summary>Splits the widest gap, so a new point lands where there is room for it.</summary>
    [RelayCommand]
    private void AddPoint()
    {
        if (Points.Count < 2)
        {
            return;
        }

        var widest = 0;
        var widestSpan = double.MinValue;

        for (var i = 1; i < Points.Count; i++)
        {
            var span = Points[i].TemperatureC - Points[i - 1].TemperatureC;
            if (span > widestSpan)
            {
                widestSpan = span;
                widest = i;
            }
        }

        var lower = Points[widest - 1];
        var upper = Points[widest];

        var inserted = new CurvePoint(
            (lower.TemperatureC + upper.TemperatureC) / 2,
            (lower.DutyPercent + upper.DutyPercent) / 2);

        Points = Points.Take(widest).Append(inserted).Concat(Points.Skip(widest)).ToArray();
        _changed(this);
    }

    /// <summary>Drops the point nearest the middle, keeping the endpoints intact.</summary>
    [RelayCommand]
    private void RemovePoint()
    {
        // Two points is the minimum a curve can be built from.
        if (Points.Count <= 2)
        {
            return;
        }

        var middle = Points.Count / 2;

        Points = Points.Where((_, i) => i != middle).ToArray();
        _changed(this);
    }

    [RelayCommand]
    private void StopCurve() => _removed();

    partial void OnSelectedSensorChanged(SensorOption value) => _changed(this);

    partial void OnResponseSecondsChanged(double value) => _changed(this);

    partial void OnHysteresisPercentChanged(double value) => _changed(this);

    partial void OnKickstartDutyChanged(double value) => _changed(this);

    partial void OnPointsChanged(IReadOnlyList<CurvePoint> value) =>
        OnPropertyChanged(nameof(PredictedDuty));

    partial void OnMarkerTemperatureChanged(double? value) =>
        OnPropertyChanged(nameof(PredictedDuty));

    private void Replace(FanCurve curve)
    {
        Points = curve.Points.ToArray();
        _changed(this);
    }
}

/// <summary>
/// A temperature source the user can pick.
/// </summary>
/// <param name="QualifiedName">
/// Name and hardware together. Several drives report a channel called simply
/// "Temperature", so the bare name is not enough to choose between them.
/// </param>
public sealed record SensorOption(string Id, string Name, string HardwareName)
{
    public string QualifiedName => $"{Name} · {HardwareName}";

    public override string ToString() => QualifiedName;
}
