using Fanner.Core.Model;

namespace Fanner.Core.Abstractions;

/// <summary>
/// One way of talking to fan hardware: a Super I/O chip over LPC, a GPU's own fan
/// controller, a USB fan hub, and so on.
/// </summary>
/// <remarks>
/// Implementations are not thread-safe. <see cref="Monitoring.MonitorService"/>
/// owns the instance and serialises every call onto its own polling thread;
/// nothing else should touch a backend directly.
/// </remarks>
public interface IHardwareBackend : IDisposable
{
    /// <summary>Short name shown in diagnostics, e.g. "LibreHardwareMonitor".</summary>
    string Name { get; }

    /// <summary>
    /// Whether this backend can write duty cycles at all. False for a read-only
    /// backend; individual headers may still refuse via <see cref="FanSnapshot.CanControl"/>.
    /// </summary>
    bool SupportsControl { get; }

    /// <summary>
    /// Brings the backend up: loads the driver, enumerates chips. Called once.
    /// Must not throw — failures come back as a <see cref="BackendStatus"/>.
    /// </summary>
    BackendStatus Initialize();

    /// <summary>
    /// Reads every channel. Called on a timer, so it must be cheap and must never
    /// block indefinitely. Returns <see cref="HardwareSnapshot.Empty"/> rather than
    /// throwing if a single read fails.
    /// </summary>
    HardwareSnapshot Poll();

    /// <summary>
    /// Takes the header over and drives it at <paramref name="percent"/> (0–100).
    /// </summary>
    /// <exception cref="NotSupportedException">The header cannot be controlled.</exception>
    void SetDuty(string fanId, double percent);

    /// <summary>
    /// Hands the header back to the motherboard firmware. Must be safe to call on a
    /// fan we never took over.
    /// </summary>
    void ReleaseToFirmware(string fanId);

    /// <summary>
    /// Hands every header back to firmware. Called on shutdown, on the panic path,
    /// and by the "restore BIOS control" button — so it must not throw, even when
    /// the driver has already gone away.
    /// </summary>
    void ReleaseAll();
}
