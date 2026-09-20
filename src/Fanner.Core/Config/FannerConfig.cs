using Fanner.Core.Curves;

namespace Fanner.Core.Config;

/// <summary>Everything Fanner remembers between runs.</summary>
public sealed record FannerConfig
{
    /// <summary>Format this build writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Schema version, so a format change can migrate rather than silently
    /// mis-reading a file and driving fans from a misparsed curve.
    /// </summary>
    public int Version { get; init; } = CurrentVersion;

    public List<Profile> Profiles { get; init; } = [];

    /// <summary>Name of the profile to apply on launch.</summary>
    public string? ActiveProfile { get; init; }

    /// <summary>Whether a scheduled task starts Fanner at logon.</summary>
    public bool RunAtStartup { get; init; }

    /// <summary>
    /// Whether closing the window hides it to the tray instead of quitting.
    /// </summary>
    /// <remarks>
    /// Default on: a fan controller that quits when the window is closed stops
    /// controlling fans, which is rarely what closing a window is meant to mean.
    /// </remarks>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Start hidden, for the logon task.</summary>
    public bool StartMinimised { get; init; }

    /// <summary>
    /// Version 1 stored one flat list of bindings and no profiles. Kept so an
    /// existing file can be read; <see cref="Migrated"/> folds it into a profile.
    /// </summary>
    public List<CurveBindingConfig> Bindings { get; init; } = [];

    /// <summary>
    /// Brings an older file up to the current shape, and guarantees at least one
    /// profile exists so the rest of the app never has to handle "no profiles".
    /// </summary>
    public FannerConfig Migrated()
    {
        if (Profiles.Count > 0)
        {
            return this with { Version = CurrentVersion, Bindings = [] };
        }

        // A v1 file's bindings become the Default profile rather than being
        // discarded — the user configured those curves and would not expect an
        // upgrade to silently forget them.
        var profile = new Profile
        {
            Name = Profile.DefaultName,
            Bindings = Bindings.Count > 0 ? [.. Bindings] : [],
        };

        return this with
        {
            Version = CurrentVersion,
            Profiles = [profile],
            ActiveProfile = ActiveProfile ?? Profile.DefaultName,
            Bindings = [],
        };
    }

    public Profile? Find(string? name) =>
        name is null ? null : Profiles.FirstOrDefault(p => p.Name == name);

    /// <summary>The profile to apply on launch, falling back to the first one.</summary>
    public Profile Active => Find(ActiveProfile) ?? Profiles.FirstOrDefault() ?? new Profile();
}

/// <summary>
/// Serialisable form of a <see cref="CurveBinding"/>.
/// </summary>
/// <remarks>
/// Deliberately a separate type. <see cref="FanCurve"/> validates and sorts in its
/// constructor, which is exactly what a deserialiser must not bypass, and keeping
/// the file format apart from the runtime model means one can change without
/// dragging the other with it.
/// </remarks>
public sealed record CurveBindingConfig
{
    public string FanId { get; init; } = string.Empty;

    public string SensorId { get; init; } = string.Empty;

    public List<CurvePoint> Points { get; init; } = [];

    public double ResponseSeconds { get; init; } = 5;

    public double HysteresisPercent { get; init; } = 2;

    public double KickstartDuty { get; init; } = 60;

    public static CurveBindingConfig From(CurveBinding binding) => new()
    {
        FanId = binding.FanId,
        SensorId = binding.SensorId,
        Points = binding.Curve.Points.ToList(),
        ResponseSeconds = binding.Response.TotalSeconds,
        HysteresisPercent = binding.HysteresisPercent,
        KickstartDuty = binding.KickstartDuty,
    };

    /// <summary>
    /// Rebuilds the runtime binding, or null when the entry cannot make a valid
    /// curve. A malformed entry is skipped rather than thrown on: one bad line in a
    /// hand-edited file should not stop the other fans from working.
    /// </summary>
    public CurveBinding? ToBinding()
    {
        if (string.IsNullOrWhiteSpace(FanId) || string.IsNullOrWhiteSpace(SensorId))
        {
            return null;
        }

        try
        {
            return new CurveBinding
            {
                FanId = FanId,
                SensorId = SensorId,
                Curve = new FanCurve(Points),
                Response = TimeSpan.FromSeconds(Math.Clamp(ResponseSeconds, 0, 120)),
                HysteresisPercent = Math.Clamp(HysteresisPercent, 0, 50),
                KickstartDuty = Math.Clamp(KickstartDuty, 0, 100),
            };
        }
        catch (ArgumentException)
        {
            // Fewer than two points, or otherwise unusable.
            return null;
        }
    }
}
