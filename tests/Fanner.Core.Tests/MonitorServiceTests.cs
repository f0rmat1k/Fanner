using Fanner.Core.Abstractions;
using Fanner.Core.Curves;
using Fanner.Core.Model;
using Fanner.Core.Monitoring;

namespace Fanner.Core.Tests;

public class MonitorServiceTests
{
    [Fact]
    public async Task Polls_after_a_successful_start()
    {
        var backend = new FakeBackend();
        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        var status = await monitor.StartAsync();

        Assert.True(status.IsOperational);
        Assert.True(await backend.WaitForPolls(3), "expected the monitor to keep polling");
        Assert.NotEmpty(monitor.Latest.Fans);
    }

    [Fact]
    public async Task Does_not_poll_when_the_backend_fails_to_start()
    {
        var backend = new FakeBackend
        {
            InitializeResult = BackendStatus.Failed(BackendFailure.NeedsElevation, "no rights"),
        };

        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        var status = await monitor.StartAsync();

        Assert.False(status.IsOperational);
        Assert.Equal(BackendFailure.NeedsElevation, status.Failure);

        // Polling a backend that never came up would read garbage and, worse, make
        // the UI look alive while reporting nothing real.
        await Task.Delay(120);
        Assert.Equal(0, backend.PollCount);
    }

    [Fact]
    public async Task Hands_every_fan_back_to_firmware_on_dispose()
    {
        var backend = new FakeBackend();
        var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        await monitor.StartAsync();
        monitor.SetDuty("fan-1", 80);

        Assert.True(await backend.WaitForDuty(80), "expected the queued write to reach the backend");

        monitor.Dispose();

        // The safety guarantee: exiting must never leave a fan pinned to a software
        // duty cycle, or a crash turns into a thermal problem.
        Assert.True(backend.ReleaseAllCalled);
    }

    [Fact]
    public async Task Clamps_a_duty_written_out_of_range()
    {
        var backend = new FakeBackend();
        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        await monitor.StartAsync();
        monitor.SetDuty("fan-1", 250);

        Assert.True(await backend.WaitForDuty(100), "expected the duty to be clamped to 100");
    }

    [Fact]
    public async Task Keeps_polling_after_a_failed_read()
    {
        var backend = new FakeBackend { ThrowOnPoll = true };
        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        var failures = 0;
        monitor.PollFailed += (_, _) => Interlocked.Increment(ref failures);

        await monitor.StartAsync();
        await Task.Delay(200);

        // One bad read is normal under contention for the driver; giving up on the
        // first one would strand the fans mid-curve.
        Assert.True(Volatile.Read(ref failures) >= 2, $"expected repeated polls, saw {failures}");
    }

    [Fact]
    public async Task Collapses_a_burst_of_duty_writes_into_one()
    {
        // Held inside Poll so the burst is issued entirely before the polling thread
        // gets a chance to drain it — the same situation a slider drag creates, but
        // without racing the scheduler.
        var backend = new FakeBackend { BlockFirstPoll = true };
        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(10));

        await monitor.StartAsync();
        Assert.True(backend.EnteredPoll.Wait(TimeSpan.FromSeconds(2)), "poll never started");

        for (var i = 0; i <= 500; i++)
        {
            monitor.SetDuty("fan-1", i % 101);
        }

        backend.ReleasePoll();

        Assert.True(await backend.WaitForDuty(500 % 101), "the newest value should win");
        await Task.Delay(120);

        // 501 slider positions, one trip into the driver.
        Assert.Equal(1, backend.SetDutyCallCount);
    }

    [Fact]
    public async Task Releasing_a_fan_cancels_a_duty_still_waiting()
    {
        var backend = new FakeBackend { BlockFirstPoll = true };
        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(10));

        await monitor.StartAsync();
        Assert.True(backend.EnteredPoll.Wait(TimeSpan.FromSeconds(2)), "poll never started");

        monitor.SetDuty("fan-1", 80);
        monitor.ReleaseToFirmware("fan-1");

        backend.ReleasePoll();
        await Task.Delay(200);

        // A duty applied after the release would silently take the header back over.
        Assert.Contains("fan-1", backend.Released);
        Assert.Equal(0, backend.SetDutyCallCount);
    }

    [Fact]
    public async Task Watchdog_releases_the_fans_and_reports()
    {
        var backend = new FakeBackend
        {
            FanMode = FanControlMode.Software,
            CpuTemperature = 99,
        };

        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        var tripped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SafetyTripped += (_, trip) => tripped.TrySetResult(trip.Message);

        await monitor.StartAsync();

        var completed = await Task.WhenAny(tripped.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(tripped.Task, completed);

        var message = await tripped.Task;

        // Released before anyone was told: a dead or slow UI must not be able to
        // delay the part that actually protects the hardware.
        Assert.True(backend.ReleaseAllCalled);
        Assert.Contains("99", message);
    }

    [Fact]
    public async Task Watchdog_suspends_the_curves_it_interrupts()
    {
        var backend = new FakeBackend
        {
            FanMode = FanControlMode.Software,
            CpuTemperature = 99,
        };

        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        monitor.Curves.Set(new CurveBinding
        {
            FanId = "fan-1",
            SensorId = "t",
            Curve = new FanCurve([new CurvePoint(40, 30), new CurvePoint(80, 100)]),
        });

        var tripped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SafetyTripped += (_, _) => tripped.TrySetResult();

        await monitor.StartAsync();
        Assert.Same(tripped.Task, await Task.WhenAny(tripped.Task, Task.Delay(TimeSpan.FromSeconds(3))));

        // Releasing the fans without stopping the engine would be theatre: the very
        // next poll would evaluate the curve and take them straight back.
        Assert.True(monitor.Curves.IsSuspended);
    }

    [Fact]
    public async Task Restoring_everything_suspends_curves_without_deleting_them()
    {
        var backend = new FakeBackend();
        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        monitor.Curves.Set(new CurveBinding
        {
            FanId = "fan-1",
            SensorId = "t",
            Curve = new FanCurve([new CurvePoint(40, 30), new CurvePoint(80, 100)]),
        });

        await monitor.StartAsync();
        monitor.ReleaseAll();
        await Task.Delay(120);

        // The panic button has to be reversible: a user who hits it must not find
        // their curves gone.
        Assert.True(monitor.Curves.IsSuspended);
        Assert.True(monitor.Curves.HasBindings);
    }

    [Fact]
    public async Task Releasing_one_fan_drops_its_binding()
    {
        var backend = new FakeBackend();
        using var monitor = new MonitorService(backend, TimeSpan.FromMilliseconds(20));

        monitor.Curves.Set(new CurveBinding
        {
            FanId = "fan-1",
            SensorId = "t",
            Curve = new FanCurve([new CurvePoint(40, 30), new CurvePoint(80, 100)]),
        });

        await monitor.StartAsync();
        monitor.ReleaseToFirmware("fan-1");
        await Task.Delay(120);

        // Keeping the binding here would undo the release on the next poll.
        Assert.False(monitor.Curves.HasBindings);
        Assert.Contains("fan-1", backend.Released);
    }

    private sealed class FakeBackend : IHardwareBackend
    {
        private readonly SemaphoreSlim _polled = new(0);
        private readonly ManualResetEventSlim _pollGate = new(false);

        private int _pollCount;
        private int _setDutyCallCount;
        private double _lastDuty = double.NaN;

        public string Name => "Fake";

        public bool SupportsControl => true;

        public BackendStatus? InitializeResult { get; set; }

        public bool ThrowOnPoll { get; set; }

        /// <summary>Holds the polling thread inside <see cref="Poll"/> until released.</summary>
        public bool BlockFirstPoll { get; set; }

        public FanControlMode FanMode { get; set; } = FanControlMode.Firmware;

        public double CpuTemperature { get; set; } = 45;

        public bool ReleaseAllCalled { get; private set; }

        public List<string> Released { get; } = [];

        public ManualResetEventSlim EnteredPoll { get; } = new(false);

        public int PollCount => Volatile.Read(ref _pollCount);

        public int SetDutyCallCount => Volatile.Read(ref _setDutyCallCount);

        public BackendStatus Initialize() => InitializeResult ?? BackendStatus.Ok(["Fake chip"]);

        public HardwareSnapshot Poll()
        {
            Interlocked.Increment(ref _pollCount);
            EnteredPoll.Set();

            if (BlockFirstPoll)
            {
                _pollGate.Wait(TimeSpan.FromSeconds(5));
            }

            _polled.Release();

            if (ThrowOnPoll)
            {
                throw new InvalidOperationException("simulated read failure");
            }

            return new HardwareSnapshot(
                DateTimeOffset.UnixEpoch,
                [new FanSnapshot("fan-1", "CPU Fan", "Fake chip", 900, 50, true, FanMode)],
                [new SensorReading("t", "Core", "Fake CPU", SensorKind.Temperature, CpuTemperature, HardwareCategory.Cpu)],
                []);
        }

        public void ReleasePoll()
        {
            BlockFirstPoll = false;
            _pollGate.Set();
        }

        public void SetDuty(string fanId, double percent)
        {
            Interlocked.Increment(ref _setDutyCallCount);
            Volatile.Write(ref _lastDuty, percent);
        }

        public void ReleaseToFirmware(string fanId)
        {
            lock (Released)
            {
                Released.Add(fanId);
            }
        }

        public void ReleaseAll() => ReleaseAllCalled = true;

        public void Dispose() => _pollGate.Set();

        public async Task<bool> WaitForPolls(int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!await _polled.WaitAsync(TimeSpan.FromSeconds(2)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes are applied on the polling thread, so a test cannot assert straight
        /// after calling one — it has to wait for the drain.
        /// </summary>
        public async Task<bool> WaitForDuty(double expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);

            while (DateTime.UtcNow < deadline)
            {
                if (Math.Abs(Volatile.Read(ref _lastDuty) - expected) < 0.001)
                {
                    return true;
                }

                await Task.Delay(10);
            }

            return false;
        }
    }
}
