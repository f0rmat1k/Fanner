namespace Fanner.Core.Abstractions;

/// <summary>
/// Why a backend is not working. The overwhelmingly common support question for
/// fan software is "it shows nothing" — so initialisation reports a specific
/// cause the UI can render as an actionable message, never a bare false.
/// </summary>
public enum BackendFailure
{
    None = 0,

    /// <summary>Running as a normal user; ring-0 port I/O needs elevation.</summary>
    NeedsElevation,

    /// <summary>The kernel driver could not be installed or started.</summary>
    DriverUnavailable,

    /// <summary>Driver is up but no chip we understand answered on the LPC bus.</summary>
    NoSupportedHardware,

    /// <summary>This backend does not run on the current OS.</summary>
    UnsupportedPlatform,

    /// <summary>Something else; see <see cref="BackendStatus.Detail"/>.</summary>
    Unknown,
}

/// <summary>
/// Result of bringing a backend up, including enough detail to tell the user what
/// to do about a failure.
/// </summary>
public sealed record BackendStatus(
    bool IsOperational,
    BackendFailure Failure,
    string Detail,
    IReadOnlyList<string> DetectedHardware)
{
    public static BackendStatus Ok(IReadOnlyList<string> detectedHardware) =>
        new(true, BackendFailure.None, string.Empty, detectedHardware);

    public static BackendStatus Failed(BackendFailure failure, string detail) =>
        new(false, failure, detail, []);

    /// <summary>A short sentence telling the user what went wrong and what fixes it.</summary>
    public string UserMessage => Failure switch
    {
        BackendFailure.None => "Hardware access is working.",
        BackendFailure.NeedsElevation =>
            "Fanner needs administrator rights to read the motherboard's sensor chip. "
            + "Restart it as administrator.",
        BackendFailure.DriverUnavailable =>
            "The hardware access driver could not start. Another monitoring tool may be "
            + "holding it, or an anti-virus blocked it. Close other fan or sensor "
            + "software and try again.",
        BackendFailure.NoSupportedHardware =>
            "No supported sensor chip responded. Your motherboard's Super I/O chip is not "
            + "recognised yet.",
        BackendFailure.UnsupportedPlatform =>
            "This hardware backend does not run on the current operating system.",
        _ => string.IsNullOrWhiteSpace(Detail) ? "Hardware access failed." : Detail,
    };
}
