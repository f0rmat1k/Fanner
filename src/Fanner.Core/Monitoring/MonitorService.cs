using System.Collections.Concurrent;
using Fanner.Core.Abstractions;
using Fanner.Core.Curves;
using Fanner.Core.Model;
using Fanner.Core.Safety;

namespace Fanner.Core.Monitoring;

/// <summary>
/// Owns a backend and polls it on a dedicated thread.
/// </summary>
/// <remarks>
/// Everything that touches the hardware runs on that one thread. Ring-0 port I/O
/// through a shared driver is serialised globally anyway, and keeping open, poll
/// and write on a single thread avoids re-entering the driver from the UI thread
/// while a poll is in flight. Callers queue work with <see cref="Post"/> instead
/// of calling the backend themselves.
/// </remarks>
public sealed class MonitorService : IDisposable
{
    /// <summary>Sentinel for <see cref="_reassertTicks"/>: nothing to re-assert.</summary>
    private const long NotPending = -1;

    private readonly IHardwareBackend _backend;
    private readonly BlockingCollection<Action<IHardwareBackend>> _commands = new();

    /// <summary>Newest requested duty per header; see <see cref="SetDuty"/>.</summary>
    private readonly ConcurrentDictionary<string, double> _pendingDuty = new();

    /// <summary>Wakes the polling thread when a write is queued.</summary>
    private readonly AutoResetEvent _work = new(false);

    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<BackendStatus> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _worker;
    private int _disposed;

    /// <summary>
    /// How long the machine was away, in ticks, while a re-assert is outstanding;
    /// <see cref="NotPending"/> otherwise. Written from the polling thread and from
    /// callers of <see cref="RequestReassert"/>, hence the interlocked access.
    /// </summary>
    private long _reassertTicks = NotPending;

    public MonitorService(IHardwareBackend backend, TimeSpan? pollInterval = null)
    {
        _backend = backend;
        PollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>How often the hardware is read. One second matches what fan control needs.</summary>
    public TimeSpan PollInterval { get; set; }

    /// <summary>
    /// A gap between polls longer than this is read as the machine having been
    /// asleep, and every controlled header is written again.
    /// </summary>
    /// <remarks>
    /// The polling thread is frozen along with everything else while the machine
    /// sleeps, so the wall clock jumping forward is the one signal that needs no
    /// platform support. It is generous — a poll takes a good fraction of a second
    /// on a board with a lot of sensors, and a busy machine can delay the next one
    /// further — because a missed resume leaves the fans on the BIOS curve while the
    /// display claims otherwise, whereas a spurious one costs a write of the duty
    /// the header is already meant to have.
    /// </remarks>
    public TimeSpan ResumeGap { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Rolling per-channel history feeding the charts.</summary>
    public SensorHistory History { get; } = new();

    /// <summary>
    /// Hands the fans back if a temperature crosses its limit while we are driving.
    /// </summary>
    public SafetyWatchdog Watchdog { get; } = new();

    /// <summary>Drives bound fans from temperature curves.</summary>
    public CurveEngine Curves { get; } = new();

    /// <summary>
    /// Remembers which headers have ever turned, so a fan changing speed is not
    /// mistaken for an empty header while it is between speeds.
    /// </summary>
    public FanPresenceTracker Presence { get; } = new();

    /// <summary>The most recent poll, or <see cref="HardwareSnapshot.Empty"/> before the first one.</summary>
    public HardwareSnapshot Latest { get; private set; } = HardwareSnapshot.Empty;

    /// <summary>Outcome of bringing the backend up. Null until <see cref="Start"/> completes.</summary>
    public BackendStatus? Status { get; private set; }

    public bool SupportsControl => _backend.SupportsControl;

    /// <summary>
    /// Raised after every poll, on the polling thread. Handlers must marshal to the
    /// UI thread themselves and must not block — a slow handler stalls polling.
    /// </summary>
    public event EventHandler<HardwareSnapshot>? SnapshotUpdated;

    /// <summary>Raised when a poll throws. Polling continues; the UI can surface a warning.</summary>
    public event EventHandler<Exception>? PollFailed;

    /// <summary>
    /// Raised on the polling thread after every controlled header has been written
    /// again, carrying how long the machine appeared to be away.
    /// </summary>
    public event EventHandler<TimeSpan>? ControlReasserted;

    /// <summary>
    /// Raised after the watchdog has already released the fans. Reporting only — the
    /// release happens on this thread before anyone is told, so a slow or dead UI
    /// cannot delay it.
    /// </summary>
    public event EventHandler<WatchdogTrip>? SafetyTripped;

    /// <summary>
    /// Starts the polling thread and completes once the backend has reported whether
    /// it came up. Driver installation can take a second or two, so this is awaited
    /// rather than blocking application startup.
    /// </summary>
    public Task<BackendStatus> StartAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_worker is not null)
        {
            return _started.Task;
        }

        _worker = new Thread(Run)
        {
            Name = "Fanner.Monitor",
            IsBackground = true,
        };

        _worker.Start();
        return _started.Task;
    }

    /// <summary>
    /// Queues work against the backend on the polling thread. Used for every write:
    /// setting a duty cycle, releasing a fan, restoring all.
    /// </summary>
    public void Post(Action<IHardwareBackend> command)
    {
        if (Volatile.Read(ref _disposed) != 0 || _commands.IsAddingCompleted)
        {
            return;
        }

        try
        {
            _commands.Add(command);
            _work.Set();
        }
        catch (InvalidOperationException)
        {
            // Raced with shutdown; the backend is being released anyway.
        }
    }

    /// <summary>
    /// Requests a duty cycle. Repeated calls for the same header collapse into one
    /// write.
    /// </summary>
    /// <remarks>
    /// A slider emits a value for every pixel of a drag — well over a hundred per
    /// second — and each write is a round trip into the kernel driver. Queueing them
    /// individually would either flood the driver or pile up a burst to replay at the
    /// next poll. Only the newest value for a header is worth anything, so the latest
    /// one overwrites its predecessor and the polling thread performs a single write.
    /// </remarks>
    public void SetDuty(string fanId, double percent)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _pendingDuty[fanId] = Math.Clamp(percent, 0, 100);
        _work.Set();
    }

    /// <summary>
    /// Gives one header back to the firmware and stops curving it. Dropping the
    /// binding is the point: a release that leaves the curve running just gets
    /// undone on the next poll.
    /// </summary>
    public void ReleaseToFirmware(string fanId)
    {
        // Drop any duty still waiting to be written, or it would land after the
        // release and silently take the header back over.
        _pendingDuty.TryRemove(fanId, out _);
        Curves.Remove(fanId);

        Post(backend => backend.ReleaseToFirmware(fanId));
    }

    /// <summary>
    /// Gives every header back. Curves are suspended rather than deleted — this is
    /// the panic button, and a panic button that quietly destroys the user's setup
    /// is one they will not press when they need to.
    /// </summary>
    public void ReleaseAll()
    {
        _pendingDuty.Clear();
        Curves.Suspend("Released by request.");

        Post(backend => backend.ReleaseAll());
    }

    /// <summary>
    /// Asks for every controlled header to be written again at the next poll.
    /// </summary>
    /// <remarks>
    /// The polling thread notices sleep on its own, so this is for anything that
    /// knows sooner or knows better — a platform power event, or a user who can see
    /// that the board has stopped listening.
    /// </remarks>
    public void RequestReassert()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.CompareExchange(ref _reassertTicks, 0, NotPending);
        _work.Set();
    }

    private void Run()
    {
        BackendStatus status;
        try
        {
            status = _backend.Initialize();
        }
        catch (Exception ex)
        {
            status = BackendStatus.Failed(BackendFailure.Unknown, ex.Message);
        }

        Status = status;
        _started.TrySetResult(status);

        if (!status.IsOperational)
        {
            return;
        }

        var token = _stopping.Token;
        var nextPoll = DateTime.UtcNow;
        var lastPoll = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            DrainCommands();
            ApplyPendingDuties();

            // Writes wake this thread immediately, but readings stay on their own
            // cadence — a drag should move the fan now, not re-read every sensor on
            // the board a hundred times on the way.
            if (DateTime.UtcNow < nextPoll)
            {
                WaitForWork(token, nextPoll - DateTime.UtcNow);
                continue;
            }

            var startedPoll = DateTime.UtcNow;
            var away = lastPoll == DateTime.MinValue ? TimeSpan.Zero : startedPoll - lastPoll;
            lastPoll = startedPoll;
            nextPoll = startedPoll + PollInterval;

            if (away > ResumeGap)
            {
                Interlocked.Exchange(ref _reassertTicks, away.Ticks);
            }

            try
            {
                var snapshot = _backend.Poll();
                Latest = snapshot;
                History.Record(snapshot);
                Presence.Observe(snapshot);

                // Act on the watchdog before telling anyone. Releasing the fans is
                // the urgent part; the notification can wait a few microseconds.
                if (Watchdog.Evaluate(snapshot) is { } trip)
                {
                    // Suspend first. Releasing the fans while the curve engine is
                    // still running would hand them back and take them again on the
                    // very next poll, which is no protection at all.
                    Curves.Suspend(trip.Message);

                    // A slider moved a moment ago is still queued, and applying it
                    // after the release would take a header straight back — the same
                    // trap ReleaseToFirmware avoids.
                    _pendingDuty.Clear();

                    SafeReleaseAll();
                    SafetyTripped?.Invoke(this, trip);

                    // Nothing is held any more, so there is nothing to write again.
                    Interlocked.Exchange(ref _reassertTicks, NotPending);
                }
                else
                {
                    Reassert();
                    ApplyCurves(snapshot);
                }

                SnapshotUpdated?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                PollFailed?.Invoke(this, ex);
            }

            WaitForWork(token, nextPoll - DateTime.UtcNow);
        }

        // Last duty of the thread: never leave fans pinned to a software duty
        // cycle after we exit, or a crash becomes a thermal problem.
        SafeReleaseAll();
    }

    /// <summary>
    /// Sleeps until the next poll is due, a write arrives, or shutdown is requested.
    /// </summary>
    private void WaitForWork(CancellationToken token, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            WaitHandle.WaitAny([token.WaitHandle, _work], timeout);
        }
        catch (ObjectDisposedException)
        {
            // Disposed mid-wait; the loop condition picks it up.
        }
    }

    /// <summary>
    /// Writes every controlled header again when a re-assert is outstanding.
    /// </summary>
    /// <remarks>
    /// Deliberately after the poll rather than the moment the gap is noticed: a poll
    /// that has just come back is proof the chip is answering again. If the write
    /// throws, the request stays outstanding and the next poll tries again.
    /// </remarks>
    private void Reassert()
    {
        var ticks = Interlocked.Read(ref _reassertTicks);
        if (ticks == NotPending)
        {
            return;
        }

        try
        {
            _backend.ReassertControl();
        }
        catch (Exception ex)
        {
            PollFailed?.Invoke(this, ex);
            return;
        }

        Interlocked.CompareExchange(ref _reassertTicks, NotPending, ticks);
        ControlReasserted?.Invoke(this, TimeSpan.FromTicks(ticks));
    }

    /// <summary>
    /// Writes whatever the curves decided for this poll.
    /// </summary>
    /// <remarks>
    /// Runs before <see cref="SnapshotUpdated"/> so the UI and the hardware are not a
    /// poll out of step, and a failure on one header does not stop the others.
    /// </remarks>
    private void ApplyCurves(HardwareSnapshot snapshot)
    {
        var outcome = Curves.Evaluate(snapshot);

        foreach (var command in outcome.Duties)
        {
            try
            {
                _backend.SetDuty(command.FanId, command.Duty);
            }
            catch (Exception ex)
            {
                PollFailed?.Invoke(this, ex);
            }
        }

        foreach (var fanId in outcome.Release)
        {
            try
            {
                _backend.ReleaseToFirmware(fanId);
            }
            catch (Exception ex)
            {
                PollFailed?.Invoke(this, ex);
            }
        }
    }

    /// <summary>
    /// Writes the newest requested duty for each header, one write apiece however
    /// many times the slider moved since the last pass.
    /// </summary>
    private void ApplyPendingDuties()
    {
        foreach (var fanId in _pendingDuty.Keys)
        {
            if (!_pendingDuty.TryRemove(fanId, out var percent))
            {
                continue;
            }

            try
            {
                _backend.SetDuty(fanId, percent);
            }
            catch (Exception ex)
            {
                PollFailed?.Invoke(this, ex);
            }
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryTake(out var command))
        {
            try
            {
                command(_backend);
            }
            catch (Exception ex)
            {
                PollFailed?.Invoke(this, ex);
            }
        }
    }

    private void SafeReleaseAll()
    {
        try
        {
            _backend.ReleaseAll();
        }
        catch
        {
            // Shutdown path. The driver may already be gone; nothing useful to do.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pendingDuty.Clear();
        _commands.CompleteAdding();
        _stopping.Cancel();
        _work.Set();

        // Give the worker a moment to hand the fans back before we tear the
        // backend down underneath it.
        _worker?.Join(TimeSpan.FromSeconds(5));

        _started.TrySetResult(
            Status ?? BackendStatus.Failed(BackendFailure.Unknown, "Stopped before start completed."));

        try
        {
            _backend.Dispose();
        }
        catch
        {
            // Nothing to recover to.
        }

        _stopping.Dispose();
        _commands.Dispose();
        _work.Dispose();
    }
}
