using Fanner.Core.Model;

namespace Fanner.Core.Curves;

/// <summary>Why a duty was chosen, for the UI and for diagnosing odd behaviour.</summary>
public enum DutyReason
{
    Curve,

    /// <summary>A short burst to get a stopped fan turning.</summary>
    Kickstart,
}

public readonly record struct DutyCommand(string FanId, double Duty, DutyReason Reason);

/// <summary>What one pass of the engine decided.</summary>
public sealed record CurveOutcome(
    IReadOnlyList<DutyCommand> Duties,
    IReadOnlyList<string> Release)
{
    public static CurveOutcome Empty { get; } = new([], []);
}

/// <summary>
/// Turns bindings plus a snapshot into duty cycles.
/// </summary>
/// <remarks>
/// Holds all the state a curve needs but should not own: the smoothed temperature,
/// the last duty written, and whether a stalled fan is mid-kick. Evaluation is
/// otherwise a pure function of the snapshot.
/// <para>
/// Bindings are edited from the UI thread while <see cref="Evaluate"/> runs on the
/// monitor thread, so every member takes the lock.
/// </para>
/// </remarks>
public sealed class CurveEngine
{
    /// <summary>
    /// Polls a source may be missing before the fan is handed back.
    /// </summary>
    /// <remarks>
    /// A reading drops out for a poll now and then. But a source that is gone for
    /// good leaves the fan frozen at whatever duty it last had, which at idle is low
    /// and silent right up until something starts heating. Better to give the header
    /// back to firmware, which always has a working curve.
    /// </remarks>
    private const int MissingSensorTolerancePolls = 10;

    private readonly Dictionary<string, CurveBinding> _bindings = [];
    private readonly Dictionary<string, BindingState> _state = [];
    private readonly Lock _gate = new();

    private DateTimeOffset _lastEvaluation = DateTimeOffset.MinValue;

    /// <summary>
    /// True while the engine is standing down — set when the watchdog fires, because
    /// releasing the fans is pointless if the engine re-applies its duty a second later.
    /// </summary>
    public bool IsSuspended { get; private set; }

    public string? SuspendedReason { get; private set; }

    public IReadOnlyCollection<CurveBinding> Bindings
    {
        get
        {
            lock (_gate)
            {
                return _bindings.Values.ToArray();
            }
        }
    }

    public bool HasBindings
    {
        get
        {
            lock (_gate)
            {
                return _bindings.Count > 0;
            }
        }
    }

    public CurveBinding? For(string fanId)
    {
        lock (_gate)
        {
            return _bindings.GetValueOrDefault(fanId);
        }
    }

    /// <summary>
    /// The duty most recently written for a fan, or null if the curve has not
    /// commanded one yet.
    /// </summary>
    /// <remarks>
    /// The UI needs this to say where a curve-driven fan is heading. It cannot use
    /// the slider position, which stops tracking the moment a curve takes over and
    /// would otherwise display a number from before the binding existed.
    /// </remarks>
    public double? LastDutyFor(string fanId)
    {
        lock (_gate)
        {
            return _state.GetValueOrDefault(fanId)?.LastApplied;
        }
    }

    /// <summary>Adds or replaces the binding for a fan, resetting its state.</summary>
    public void Set(CurveBinding binding)
    {
        lock (_gate)
        {
            _bindings[binding.FanId] = binding;

            // Fresh state: smoothing carried over from a different sensor or curve
            // would drive the first few seconds from a reading that no longer applies.
            _state.Remove(binding.FanId);
        }
    }

    public void Remove(string fanId)
    {
        lock (_gate)
        {
            _bindings.Remove(fanId);
            _state.Remove(fanId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _bindings.Clear();
            _state.Clear();
        }
    }

    public void Suspend(string reason)
    {
        lock (_gate)
        {
            IsSuspended = true;
            SuspendedReason = reason;
            _state.Clear();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            IsSuspended = false;
            SuspendedReason = null;

            // Start smoothing from whatever the next poll reports rather than from a
            // reading taken before the machine got hot.
            _state.Clear();
        }
    }

    public CurveOutcome Evaluate(HardwareSnapshot snapshot)
    {
        lock (_gate)
        {
            if (IsSuspended || _bindings.Count == 0)
            {
                return CurveOutcome.Empty;
            }

            var elapsed = _lastEvaluation == DateTimeOffset.MinValue
                ? TimeSpan.Zero
                : snapshot.Timestamp - _lastEvaluation;

            _lastEvaluation = snapshot.Timestamp;

            var duties = new List<DutyCommand>();
            var release = new List<string>();

            foreach (var binding in _bindings.Values)
            {
                var fan = snapshot.Fans.FirstOrDefault(f => f.Id == binding.FanId);
                if (fan is null || !fan.CanControl)
                {
                    continue;
                }

                var state = _state.TryGetValue(binding.FanId, out var existing)
                    ? existing
                    : _state[binding.FanId] = new BindingState();

                var reading = snapshot.Temperatures
                    .FirstOrDefault(t => t.Id == binding.SensorId)?.Value;

                if (reading is null)
                {
                    if (++state.MissedPolls >= MissingSensorTolerancePolls)
                    {
                        release.Add(binding.FanId);
                        _state.Remove(binding.FanId);
                    }

                    continue;
                }

                state.MissedPolls = 0;

                var temperature = state.Smooth(reading.Value, elapsed, binding.Response);
                var duty = binding.Curve.Evaluate(temperature);

                if (TryKickstart(binding, fan, state, ref duty))
                {
                    duties.Add(new DutyCommand(binding.FanId, duty, DutyReason.Kickstart));
                    state.LastApplied = duty;
                    continue;
                }

                // Deadband: skip writes too small to be worth a driver round trip or
                // an audible change.
                if (state.LastApplied is { } last
                    && Math.Abs(duty - last) < binding.HysteresisPercent)
                {
                    continue;
                }

                duties.Add(new DutyCommand(binding.FanId, duty, DutyReason.Curve));
                state.LastApplied = duty;
            }

            return new CurveOutcome(duties, release);
        }
    }

    /// <summary>
    /// Raises <paramref name="duty"/> to the kickstart level when the fan is stopped
    /// and the curve alone would not restart it.
    /// </summary>
    private static bool TryKickstart(CurveBinding binding, FanSnapshot fan, BindingState state, ref double duty)
    {
        var stopped = fan.Rpm is null or 0;

        if (!stopped)
        {
            state.KickPolls = 0;
            return false;
        }

        if (binding.KickstartDuty <= 0
            || duty <= 0
            || duty >= binding.KickstartDuty
            || state.KickPolls >= binding.MaxKickstartPolls)
        {
            return false;
        }

        state.KickPolls++;
        duty = binding.KickstartDuty;
        return true;
    }

    private sealed class BindingState
    {
        private double? _smoothed;

        public double? LastApplied { get; set; }

        public int KickPolls { get; set; }

        public int MissedPolls { get; set; }

        /// <summary>
        /// Exponential moving average with a time constant, so the result depends on
        /// elapsed seconds rather than on how often we happen to poll.
        /// </summary>
        public double Smooth(double reading, TimeSpan elapsed, TimeSpan response)
        {
            if (_smoothed is null || response <= TimeSpan.Zero || elapsed <= TimeSpan.Zero)
            {
                // First sample, or smoothing disabled: take the reading as it is
                // rather than easing up from zero, which would start every fan at
                // the bottom of its curve.
                _smoothed = reading;
                return reading;
            }

            var alpha = 1 - Math.Exp(-elapsed.TotalSeconds / response.TotalSeconds);
            _smoothed += (reading - _smoothed.Value) * alpha;

            return _smoothed.Value;
        }
    }
}
