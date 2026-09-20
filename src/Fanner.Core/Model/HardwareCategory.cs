namespace Fanner.Core.Model;

/// <summary>
/// What kind of device a reading came from.
/// </summary>
/// <remarks>
/// Kept separate from the chip's name because names are not something to branch on:
/// "Nuvoton NCT6687D-R" and "CT1000BX500SSD1" tell the UI nothing about how to rank
/// them. Ordering the sensor list alphabetically by hardware name puts DIMM
/// temperatures above the CPU, which is exactly backwards for fan control.
/// </remarks>
public enum HardwareCategory
{
    Other = 0,

    /// <summary>The Super I/O chip or embedded controller: board, VRM, chipset temps.</summary>
    Motherboard,

    Cpu,

    Gpu,

    /// <summary>AIO pump or fan hub.</summary>
    Cooler,

    Storage,

    Memory,

    Psu,
}

public static class HardwareCategoryExtensions
{
    /// <summary>
    /// Display rank, most relevant to fan control first. CPU and GPU lead because
    /// they are what people actually curve against; storage and memory trail because
    /// they are numerous and rarely drive a fan.
    /// </summary>
    public static int SortOrder(this HardwareCategory category) => category switch
    {
        HardwareCategory.Cpu => 0,
        HardwareCategory.Gpu => 1,
        HardwareCategory.Motherboard => 2,
        HardwareCategory.Cooler => 3,
        HardwareCategory.Storage => 4,
        HardwareCategory.Memory => 5,
        HardwareCategory.Psu => 6,
        _ => 7,
    };
}
