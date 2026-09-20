using Fanner.Core.Model;

namespace Fanner.Core.Monitoring;

/// <summary>
/// Remembers which headers have ever had something turning on them.
/// </summary>
/// <remarks>
/// Whether a header is empty is a lasting fact about the machine, but
/// <see cref="FanSnapshot.LooksUnpopulated"/> can only see one instant. Those two
/// disagree exactly when a fan is between speeds: on the way down it stops turning
/// while the duty is still falling, and on the way up the duty rises before the
/// blades move. In both cases a single poll shows "driven, but no tachometer" —
/// which is also what an empty header looks like.
/// <para>
/// One reading of real RPM settles it forever: something is plugged in. Fans are
/// not hot-plugged, so this never needs to expire.
/// </para>
/// </remarks>
public sealed class FanPresenceTracker
{
    private readonly HashSet<string> _hasSpun = [];
    private readonly Lock _gate = new();

    /// <summary>Records any header reporting rotation in this snapshot.</summary>
    public void Observe(HardwareSnapshot snapshot)
    {
        lock (_gate)
        {
            foreach (var fan in snapshot.Fans)
            {
                if (fan.Rpm is > 0)
                {
                    _hasSpun.Add(fan.Id);
                }
            }
        }
    }

    /// <summary>True once this header has reported rotation at any point.</summary>
    public bool HasEverSpun(string fanId)
    {
        lock (_gate)
        {
            return _hasSpun.Contains(fanId);
        }
    }

    /// <summary>
    /// Whether the header should be treated as empty. False for anything that has
    /// ever turned, however still it looks right now.
    /// </summary>
    public bool IsEmpty(FanSnapshot fan) => !HasEverSpun(fan.Id) && fan.LooksUnpopulated;

    /// <summary>Forgets everything. Used when switching to a different backend.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _hasSpun.Clear();
        }
    }
}
