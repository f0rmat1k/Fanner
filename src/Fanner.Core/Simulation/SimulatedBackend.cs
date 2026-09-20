using Fanner.Core.Abstractions;
using Fanner.Core.Model;

namespace Fanner.Core.Simulation;

/// <summary>
/// A fake backend that models a real board, so the UI can be built and demoed
/// without elevation, without a driver, and without spinning anything down.
/// </summary>
/// <remarks>
/// Deliberately mirrors the MSI X870 / NCT6687D-R this project targets, including
/// the awkward parts: non-contiguous header indices, empty headers that report
/// 0 RPM, and a pump header pinned at 100. Code that only ever meets tidy data
/// tends to break the first time it meets the real chip.
/// </remarks>
public sealed class SimulatedBackend : IHardwareBackend
{
    private const string ChipName = "Nuvoton NCT6687D-R (simulated)";

    private readonly List<FakeFan> _fans =
    [
        new(0, "CPU Fan", maxRpm: 2200, populated: true, duty: 40),
        new(1, "Pump Fan #1", maxRpm: 4800, populated: false, duty: 100),
        new(2, "Chipset Fan", maxRpm: 5000, populated: false, duty: 50),
        new(3, "EZ-Connect Fan", maxRpm: 1800, populated: false, duty: 60),
        new(10, "System Fan #1", maxRpm: 1900, populated: true, duty: 60),
        new(11, "System Fan #2", maxRpm: 1900, populated: true, duty: 60),
        new(12, "System Fan #3", maxRpm: 1900, populated: true, duty: 60),
        new(13, "System Fan #4", maxRpm: 1900, populated: true, duty: 60),
        new(14, "System Fan #5", maxRpm: 1900, populated: false, duty: 60),
        new(15, "System Fan #6", maxRpm: 1900, populated: true, duty: 60),
    ];

    private readonly Random _random = new(20260920);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    public string Name => "Simulated hardware";

    public bool SupportsControl => true;

    public BackendStatus Initialize() => BackendStatus.Ok([ChipName, "AMD Ryzen 7 9800X3D (simulated)"]);

    public HardwareSnapshot Poll()
    {
        var elapsed = (DateTimeOffset.Now - _startedAt).TotalSeconds;

        // A slow sine plus jitter: enough movement to prove the charts animate and
        // that a curve reacts, without pretending to be a thermal model.
        var cpuTemp = 48 + 14 * Math.Sin(elapsed / 37.0) + _random.NextDouble() * 1.5;
        var systemTemp = 38 + 5 * Math.Sin(elapsed / 61.0) + _random.NextDouble();

        var fans = _fans.Select(f => f.Snapshot(ChipName, _random)).ToList();

        const string CpuName = "AMD Ryzen 7 9800X3D (simulated)";
        const string GpuName = "NVIDIA GeForce RTX 4090 (simulated)";

        var temperatures = new List<SensorReading>
        {
            new("/sim/cpu/temperature/0", "Core (Tctl/Tdie)", CpuName, SensorKind.Temperature, cpuTemp, HardwareCategory.Cpu),
            new("/sim/cpu/temperature/1", "CCD1 (Tdie)", CpuName, SensorKind.Temperature, cpuTemp - 3, HardwareCategory.Cpu),
            new("/sim/gpu/temperature/0", "GPU Core", GpuName, SensorKind.Temperature, 45 + 18 * Math.Sin(elapsed / 23.0), HardwareCategory.Gpu),
            new("/sim/lpc/temperature/0", "CPU Core", ChipName, SensorKind.Temperature, cpuTemp - 2, HardwareCategory.Motherboard),
            new("/sim/lpc/temperature/1", "System", ChipName, SensorKind.Temperature, systemTemp, HardwareCategory.Motherboard),
            new("/sim/lpc/temperature/2", "VRM MOS", ChipName, SensorKind.Temperature, systemTemp + 6, HardwareCategory.Motherboard),
            new("/sim/nvme/temperature/0", "Composite Temperature", "Netac NVMe SSD 1TB (simulated)", SensorKind.Temperature, 52 + _random.NextDouble() * 2, HardwareCategory.Storage),
        };

        var others = new List<SensorReading>
        {
            new("/sim/cpu/power/0", "Package", CpuName, SensorKind.Power, 38 + 40 * Math.Abs(Math.Sin(elapsed / 29.0)), HardwareCategory.Cpu),
            new("/sim/lpc/voltage/0", "+12V", ChipName, SensorKind.Voltage, 12.1 + _random.NextDouble() * 0.05, HardwareCategory.Motherboard),
            new("/sim/lpc/voltage/4", "Vcore", ChipName, SensorKind.Voltage, 1.09 + _random.NextDouble() * 0.02, HardwareCategory.Motherboard),
        };

        return new HardwareSnapshot(DateTimeOffset.Now, fans, temperatures, others);
    }

    public void SetDuty(string fanId, double percent) => Find(fanId).Duty = Math.Clamp(percent, 0, 100);

    public void ReleaseToFirmware(string fanId) => Find(fanId).ReleaseToFirmware();

    public void ReleaseAll()
    {
        foreach (var fan in _fans)
        {
            fan.ReleaseToFirmware();
        }
    }

    private FakeFan Find(string fanId) =>
        _fans.FirstOrDefault(f => f.Id == fanId)
        ?? throw new NotSupportedException($"Unknown fan header '{fanId}'.");

    public void Dispose()
    {
    }

    private sealed class FakeFan(int index, string name, int maxRpm, bool populated, double duty)
    {
        /// <summary>
        /// How fast the duty cycle travels towards its target, in percent per second.
        /// </summary>
        /// <remarks>
        /// Measured on a real NCT6687D-R: a jump from 60 % to 85 % took about
        /// fourteen seconds. The simulator slews too, because an instant response
        /// would hide the one behaviour most likely to be mistaken for a bug — the
        /// reading lagging well behind the slider.
        /// </remarks>
        private const double SlewPercentPerSecond = 2;

        private double _duty = duty;
        private double _target = duty;
        private DateTime _lastStep = DateTime.UtcNow;

        public string Id { get; } = $"/sim/lpc/nct6687dr/0/header/{index}";

        public double FirmwareDuty { get; } = duty;

        public bool IsSoftwareControlled { get; set; }

        /// <summary>Where the duty is headed. Reads back the value actually reached.</summary>
        public double Duty
        {
            get => _duty;
            set
            {
                _target = value;
                IsSoftwareControlled = true;
            }
        }

        /// <summary>
        /// Aims the header back at its firmware duty. The slew still applies, so the
        /// fan coasts back rather than snapping, as the real board does.
        /// </summary>
        public void ReleaseToFirmware()
        {
            IsSoftwareControlled = false;
            _target = FirmwareDuty;
        }

        /// <summary>Moves the duty towards its target by however long has elapsed.</summary>
        private void Step()
        {
            var now = DateTime.UtcNow;
            var seconds = (now - _lastStep).TotalSeconds;
            _lastStep = now;

            var remaining = _target - _duty;
            var travel = SlewPercentPerSecond * seconds;

            _duty = Math.Abs(remaining) <= travel
                ? _target
                : _duty + Math.Sign(remaining) * travel;
        }

        public FanSnapshot Snapshot(string chipName, Random random)
        {
            Step();

            // An empty header reads 0 RPM whatever the duty says — the same trap the
            // real board sets, where 0 RPM means "nothing plugged in" far more often
            // than "fan stopped".
            int? rpm = populated
                ? (int)Math.Round(maxRpm * (_duty / 100.0) * (0.97 + random.NextDouble() * 0.06))
                : 0;

            return new FanSnapshot(
                Id: Id,
                Name: name,
                HardwareName: chipName,
                Rpm: rpm,
                DutyPercent: Math.Round(_duty),
                CanControl: true,
                Mode: IsSoftwareControlled ? FanControlMode.Software : FanControlMode.Firmware);
        }
    }
}
