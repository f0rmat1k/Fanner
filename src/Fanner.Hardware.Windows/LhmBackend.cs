using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Fanner.Core.Abstractions;
using Fanner.Core.Model;
using LibreHardwareMonitor.Hardware;
using LhmSensorType = LibreHardwareMonitor.Hardware.SensorType;
using SensorKind = Fanner.Core.Model.SensorKind;

namespace Fanner.Hardware.Windows;

/// <summary>
/// Windows backend built on LibreHardwareMonitor, which reaches the motherboard's
/// Super I/O chip through the PawnIO kernel driver.
/// </summary>
/// <remarks>
/// Not thread-safe by design — see <see cref="IHardwareBackend"/>. The monitor owns
/// the instance and calls it from one thread.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LhmBackend : IHardwareBackend
{
    private readonly Computer _computer = new()
    {
        IsMotherboardEnabled = true,   // the Super I/O chip: the fans we care about
        IsControllerEnabled = true,    // AIO pumps and USB fan hubs
        IsCpuEnabled = true,           // Tctl/Tdie, the usual curve input
        IsGpuEnabled = true,           // GPU temperature, and GPU fans on top
        IsStorageEnabled = true,       // NVMe temperature, worth curving on
        IsMemoryEnabled = true,
        IsPsuEnabled = true,
    };

    private readonly UpdateVisitor _updateVisitor = new();

    /// <summary>Control channel per fan header id, for writes.</summary>
    private readonly Dictionary<string, IControl> _controls = [];

    /// <summary>
    /// Headers we have taken over and the duty we last wrote to each, so shutdown
    /// knows what to hand back and <see cref="ReassertControl"/> knows what to write.
    /// </summary>
    private readonly Dictionary<string, double> _engaged = [];

    private bool _opened;
    private bool _disposed;

    public string Name => "LibreHardwareMonitor / PawnIO";

    public bool SupportsControl => true;

    public BackendStatus Initialize()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return BackendStatus.Failed(
                BackendFailure.UnsupportedPlatform,
                "The LibreHardwareMonitor backend is Windows-only.");
        }

        // Order matters. Without elevation, and without the driver, LHM does not
        // fail — it returns zeros. Both look like unsupported hardware downstream,
        // so rule them out before blaming the motherboard.
        if (!PawnIoDriver.IsProcessElevated())
        {
            return BackendStatus.Failed(
                BackendFailure.NeedsElevation,
                "Reading the motherboard sensor chip requires ring-0 port access.");
        }

        if (!PawnIoDriver.IsInstalled)
        {
            return BackendStatus.Failed(
                BackendFailure.DriverUnavailable,
                $"The PawnIO kernel driver is not installed. Get it from {PawnIoDriver.DownloadUrl}. "
                + "It is signed and works with Memory Integrity enabled, so no security "
                + "feature needs turning off.");
        }

        try
        {
            _computer.Open();
            _opened = true;
        }
        catch (Exception ex)
        {
            return BackendStatus.Failed(
                BackendFailure.DriverUnavailable,
                $"Could not open hardware access: {ex.Message}");
        }

        _computer.Accept(_updateVisitor);

        var snapshot = Poll();
        if (snapshot.Fans.Count == 0)
        {
            return BackendStatus.Failed(
                BackendFailure.NoSupportedHardware,
                $"PawnIO {PawnIoDriver.Version} is installed and running, but no chip with fan "
                + "headers was recognised. The motherboard's Super I/O may not be supported yet.");
        }

        // Only the chips worth naming in the header. Listing everything with a
        // sensor drags in every DIMM and SSD and buries the one line that matters:
        // which Super I/O chip we are actually talking to.
        var detected = Flatten(_computer.Hardware)
            .Where(h => h.Sensors.Length > 0)
            .Where(h => MapCategory(h.HardwareType)
                is HardwareCategory.Motherboard
                or HardwareCategory.Cpu
                or HardwareCategory.Gpu
                or HardwareCategory.Cooler)
            .Select(h => h.Name)
            .Distinct()
            .ToList();

        return BackendStatus.Ok(detected);
    }

    public HardwareSnapshot Poll()
    {
        if (!_opened)
        {
            return HardwareSnapshot.Empty;
        }

        _computer.Accept(_updateVisitor);

        var fans = new List<FanSnapshot>();
        var temperatures = new List<SensorReading>();
        var others = new List<SensorReading>();

        foreach (var hardware in Flatten(_computer.Hardware))
        {
            CollectFans(hardware, fans);

            foreach (var sensor in hardware.Sensors)
            {
                // Fan and Control channels are surfaced as headers above, not as
                // loose sensors — listing them twice would just clutter the UI.
                if (sensor.SensorType is LhmSensorType.Fan or LhmSensorType.Control)
                {
                    continue;
                }

                var kind = MapKind(sensor.SensorType);
                if (kind == SensorKind.Unknown)
                {
                    continue;
                }

                if (kind == SensorKind.Temperature)
                {
                    if (IsThresholdSensor(sensor.Name))
                    {
                        continue;
                    }

                    temperatures.Add(ToReading(sensor, hardware, kind));
                }
                else
                {
                    others.Add(ToReading(sensor, hardware, kind));
                }
            }
        }

        return new HardwareSnapshot(DateTimeOffset.Now, fans, temperatures, others);
    }

    public void SetDuty(string fanId, double percent)
    {
        if (!_controls.TryGetValue(fanId, out var control))
        {
            throw new NotSupportedException($"Fan header '{fanId}' has no control channel.");
        }

        // The accepted range is not always 0–100: NVIDIA refuses anything below 30.
        // Clamping here as well as in the UI keeps a stale snapshot from writing a
        // value the driver would reject.
        var clamped = Math.Clamp(percent, control.MinSoftwareValue, control.MaxSoftwareValue);

        control.SetSoftware((float)clamped);
        _engaged[fanId] = clamped;
    }

    public void ReassertControl()
    {
        foreach (var (fanId, duty) in _engaged.ToArray())
        {
            if (!_controls.TryGetValue(fanId, out var control))
            {
                continue;
            }

            try
            {
                // LibreHardwareMonitor drops a write that repeats the value it
                // already holds — it compares against its own field and never
                // reaches the chip. That is exactly the case here, since the duty we
                // want is the one we asked for before the machine slept, so the
                // value has to be moved before it can be written back.
                control.SetSoftware((float)Nudge(duty, control));
                control.SetSoftware((float)duty);
            }
            catch
            {
                // One header failing must not leave the others on the BIOS curve.
            }
        }
    }

    /// <summary>
    /// A duty one step away from <paramref name="duty"/>, staying inside what the
    /// header accepts.
    /// </summary>
    /// <remarks>
    /// Half a percent, which is a different byte once scaled to the chip's 0–255
    /// range but far too small a change to hear. Direction depends on the ends of
    /// the range: nudging a fan pinned at 100 has to go down.
    /// </remarks>
    private static double Nudge(double duty, IControl control) =>
        duty + 0.5 <= control.MaxSoftwareValue
            ? duty + 0.5
            : Math.Max(duty - 0.5, control.MinSoftwareValue);

    public void ReleaseToFirmware(string fanId)
    {
        if (_controls.TryGetValue(fanId, out var control))
        {
            control.SetDefault();
        }

        _engaged.Remove(fanId);
    }

    public void ReleaseAll()
    {
        // Snapshot the keys: SetDefault mutates _engaged through ReleaseToFirmware.
        foreach (var fanId in _engaged.Keys.ToArray())
        {
            try
            {
                ReleaseToFirmware(fanId);
            }
            catch
            {
                // Best effort. This runs on shutdown and on the panic path, where
                // giving up on one header must not strand the rest.
            }
        }

        _engaged.Clear();
    }

    private void CollectFans(IHardware hardware, List<FanSnapshot> fans)
    {
        // A header's tachometer and its control output are separate sensors that
        // share an Index. Indices are not contiguous — on an NCT6687D the system
        // fans start at 10 — so pair on the value, never on position.
        Dictionary<int, ISensor>? tachs = null;
        Dictionary<int, ISensor>? controls = null;

        foreach (var sensor in hardware.Sensors)
        {
            switch (sensor.SensorType)
            {
                case LhmSensorType.Fan:
                    (tachs ??= [])[sensor.Index] = sensor;
                    break;
                case LhmSensorType.Control:
                    (controls ??= [])[sensor.Index] = sensor;
                    break;
            }
        }

        if (tachs is null && controls is null)
        {
            return;
        }

        var indices = new SortedSet<int>();
        if (tachs is not null) indices.UnionWith(tachs.Keys);
        if (controls is not null) indices.UnionWith(controls.Keys);

        foreach (var index in indices)
        {
            ISensor? tach = null;
            ISensor? control = null;
            tachs?.TryGetValue(index, out tach);
            controls?.TryGetValue(index, out control);

            var id = HeaderId(hardware, index);
            var writable = control?.Control;

            if (writable is not null)
            {
                _controls[id] = writable;
            }

            fans.Add(new FanSnapshot(
                Id: id,
                // The tach name is the more specific of the two — "Pump Fan #1"
                // against a control channel merely called "Pump Fan".
                Name: tach?.Name ?? control?.Name ?? $"Fan #{index}",
                HardwareName: hardware.Name,
                Rpm: tach?.Value is { } rpm ? (int)Math.Round(rpm) : null,
                DutyPercent: control?.Value,
                CanControl: writable is not null,
                Mode: MapMode(writable),
                MinDuty: writable?.MinSoftwareValue ?? 0,
                MaxDuty: writable?.MaxSoftwareValue ?? 100,
                Category: MapCategory(hardware.HardwareType)));
        }
    }

    /// <summary>
    /// Stable id for a header. Built from the chip's identifier and the shared
    /// index rather than from either sensor's own identifier, so it stays the same
    /// whether or not a fan is plugged in — profiles reference this.
    /// </summary>
    private static string HeaderId(IHardware hardware, int index) =>
        $"{hardware.Identifier}/header/{index}";

    private static SensorReading ToReading(ISensor sensor, IHardware hardware, SensorKind kind) =>
        new(
            Id: sensor.Identifier.ToString(),
            Name: sensor.Name,
            HardwareName: hardware.Name,
            Kind: kind,
            Value: sensor.Value,
            Category: MapCategory(hardware.HardwareType));

    private static HardwareCategory MapCategory(HardwareType type) => type switch
    {
        // Super I/O is where the board's own temperatures live; the Motherboard node
        // itself is just a container, but both belong in the same bucket.
        HardwareType.SuperIO or HardwareType.Motherboard or HardwareType.EmbeddedController
            => HardwareCategory.Motherboard,

        HardwareType.Cpu => HardwareCategory.Cpu,

        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
            => HardwareCategory.Gpu,

        HardwareType.Cooler => HardwareCategory.Cooler,
        HardwareType.Storage => HardwareCategory.Storage,
        HardwareType.Memory => HardwareCategory.Memory,
        HardwareType.Psu => HardwareCategory.Psu,

        _ => HardwareCategory.Other,
    };

    private static FanControlMode MapMode(IControl? control) => control?.ControlMode switch
    {
        ControlMode.Software => FanControlMode.Software,

        // Default means SetDefault() handed the header back; Undefined means nobody
        // has touched it since open. Either way the firmware is driving.
        ControlMode.Default or ControlMode.Undefined => FanControlMode.Firmware,

        _ => FanControlMode.Unknown,
    };

    private static SensorKind MapKind(LhmSensorType type) => type switch
    {
        LhmSensorType.Temperature => SensorKind.Temperature,
        LhmSensorType.Fan => SensorKind.FanSpeed,
        LhmSensorType.Control => SensorKind.Control,
        LhmSensorType.Voltage => SensorKind.Voltage,
        LhmSensorType.Power => SensorKind.Power,
        LhmSensorType.Load => SensorKind.Load,
        LhmSensorType.Clock => SensorKind.Clock,
        LhmSensorType.Flow => SensorKind.Flow,
        _ => SensorKind.Unknown,
    };

    /// <summary>
    /// True for channels that report a configured threshold rather than a live
    /// reading. NVMe and DIMM hardware expose their warning and critical trip
    /// points as temperature sensors; listing them as readings would put a
    /// constant "119 °C" next to real ones and make a curve editor offer nonsense.
    /// </summary>
    private static bool IsThresholdSensor(string name) =>
        name.Contains("Limit", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Resolution", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Critical", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> hardware)
    {
        foreach (var item in hardware)
        {
            yield return item;

            foreach (var sub in Flatten(item.SubHardware))
            {
                yield return sub;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        ReleaseAll();

        if (_opened)
        {
            try
            {
                _computer.Close();
            }
            catch
            {
                // Closing twice, or after the driver has gone, is not worth surfacing.
            }

            _opened = false;
        }
    }

    /// <summary>
    /// LHM only refreshes a chip when something asks it to, and sub-hardware is not
    /// walked for you — hence the visitor.
    /// </summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();

            foreach (var sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
