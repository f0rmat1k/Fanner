using CommunityToolkit.Mvvm.ComponentModel;
using Fanner.Core.Model;
using Fanner.Core.Monitoring;

namespace Fanner.App.ViewModels;

/// <summary>One temperature or other reading in the sensor panel.</summary>
public sealed partial class SensorViewModel(string id, SensorKind kind) : ViewModelBase
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HardwareName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double? Value { get; set; }

    [ObservableProperty]
    public partial string Display { get; set; } = "—";

    [ObservableProperty]
    public partial IReadOnlyList<float>? History { get; set; }

    [ObservableProperty]
    public partial HardwareCategory Category { get; set; }

    public string Id { get; } = id;

    public SensorKind Kind { get; } = kind;

    public void Update(SensorReading reading, SensorHistory history)
    {
        Name = reading.Name;
        HardwareName = reading.HardwareName;
        Category = reading.Category;
        Value = reading.Value;
        Display = reading.Display;
        History = history.Read(reading.Id);
    }
}
