using System.Collections.ObjectModel;
using Fanner.Core.Model;

namespace Fanner.App.ViewModels;

/// <summary>
/// The fans belonging to one piece of hardware.
/// </summary>
/// <remarks>
/// Grouping is by hardware rather than by anything finer, because that is the real
/// division: the graphics card runs its own fans through the vendor API, while
/// every case and CPU header hangs off the motherboard's Super I/O chip. There is
/// no separate "CPU fan controller" to group under — the CPU header is one of the
/// board's, and it already sorts first because the chip numbers it first.
/// </remarks>
public sealed class FanGroupViewModel(string key, HardwareCategory category, string hardwareName)
{
    /// <summary>Identity of the group, used to tell whether the grouping changed.</summary>
    public string Key { get; } = key;

    public HardwareCategory Category { get; } = category;

    public string HardwareName { get; } = hardwareName;

    /// <summary>What kind of device these fans belong to.</summary>
    public string Title { get; } = category switch
    {
        HardwareCategory.Motherboard => "Motherboard",
        HardwareCategory.Gpu => "Graphics card",
        HardwareCategory.Cooler => "Cooler",
        HardwareCategory.Cpu => "Processor",
        HardwareCategory.Psu => "Power supply",
        _ => hardwareName,
    };

    public ObservableCollection<FanViewModel> Fans { get; } = [];

    /// <summary>
    /// Sort rank, so the board own headers lead and the graphics card follows.
    /// </summary>
    /// <remarks>
    /// Not <see cref="HardwareCategoryExtensions.SortOrder"/>: that ranking exists
    /// for the temperature list, where the CPU and GPU readings matter most and the
    /// board comes third. Fans invert it — nearly every header is on the board,
    /// including the CPU fan, so that group belongs at the top.
    /// </remarks>
    public int SortOrder => Category switch
    {
        HardwareCategory.Motherboard => 0,
        HardwareCategory.Cooler => 1,
        HardwareCategory.Cpu => 2,
        HardwareCategory.Gpu => 3,
        HardwareCategory.Psu => 4,
        _ => 5,
    };
}
