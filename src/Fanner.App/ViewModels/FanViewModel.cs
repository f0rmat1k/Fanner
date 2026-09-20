using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanner.Core.Model;
using Fanner.Core.Monitoring;

namespace Fanner.App.ViewModels;

/// <summary>
/// One fan header on the dashboard: its readings, and the slider that drives it.
/// </summary>
/// <remarks>
/// Long-lived: created once per header and updated in place on every poll, so the
/// list does not churn a second and the sparklines keep their identity.
/// </remarks>
public sealed partial class FanViewModel : ViewModelBase
{
    private readonly Action<string, double> _setDuty;
    private readonly Action<string> _release;

    /// <summary>
    /// Set while the incoming snapshot is writing <see cref="TargetDuty"/>, so that
    /// following the hardware does not look like the user moving the slider and get
    /// echoed straight back as a write.
    /// </summary>
    private bool _applyingSnapshot;

    /// <summary>
    /// Whether the user has actually touched this slider.
    /// </summary>
    /// <remarks>
    /// A property change alone is not evidence of intent. A Slider coerces its Value
    /// into [Minimum, Maximum] and pushes the corrected number back through the
    /// two-way binding, arriving here identically to a drag — and an NVIDIA header
    /// reports 0 % while refusing anything under 30, so that echo by itself took
    /// software control of a stopped GPU fan and spun it to 877 RPM with nobody
    /// touching anything. Taking over a fan is a physical act, so it requires a
    /// physical gesture, which only the view can witness.
    /// </remarks>
    private bool _userIsAdjusting;

    private readonly Action<string> _openCurve;

    public FanViewModel(
        string id,
        Action<string, double> setDuty,
        Action<string> release,
        Action<string> openCurve)
    {
        Id = id;
        _setDuty = setDuty;
        _release = release;
        _openCurve = openCurve;
    }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HardwareName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial HardwareCategory Category { get; set; }

    [ObservableProperty]
    public partial int? Rpm { get; set; }

    /// <summary>What the hardware currently reports. Display only.</summary>
    [ObservableProperty]
    public partial double? DutyPercent { get; set; }

    /// <summary>
    /// What the slider is asking for. Diverges from <see cref="DutyPercent"/> between
    /// a drag and the poll that confirms it.
    /// </summary>
    [ObservableProperty]
    public partial double TargetDuty { get; set; }

    [ObservableProperty]
    public partial bool CanControl { get; set; }

    [ObservableProperty]
    public partial FanControlMode Mode { get; set; }

    /// <summary>
    /// Whether this header looks empty. Decided by the monitor, not by a single
    /// reading: a fan between speeds is driven yet still, which on its own is
    /// indistinguishable from nothing being plugged in.
    /// </summary>
    [ObservableProperty]
    public partial bool IsEmptyHeader { get; set; }

    [ObservableProperty]
    public partial double MinDuty { get; set; }

    [ObservableProperty]
    public partial double MaxDuty { get; set; } = 100;

    [ObservableProperty]
    public partial IReadOnlyList<float>? RpmHistory { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<float>? DutyHistory { get; set; }

    public string Id { get; }

    /// <summary>
    /// Position the backend reported this header in, which follows the chip's own
    /// header order. Sorting by name instead would read as scrambled: it puts
    /// "Chipset Fan" ahead of "CPU Fan" and scatters the numbered system fans.
    /// </summary>
    public int Order { get; set; }

    public string RpmDisplay => Rpm is null ? "—" : $"{Rpm} RPM";

    public string DutyDisplay => DutyPercent is null ? "—" : $"{DutyPercent.Value:0} %";

    /// <summary>
    /// Whether a curve is driving this header. Distinct from the hardware's own
    /// notion of mode, which only knows firmware against software and cannot say
    /// where a software duty came from.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCurveDriven { get; set; }

    /// <summary>
    /// Who is driving the fan. Shown as a badge, because "why did my fan not
    /// respond" is nearly always "the firmware still owns it".
    /// </summary>
    public string ModeDisplay => (Mode, IsCurveDriven) switch
    {
        (FanControlMode.Software, true) => "CURVE",
        (FanControlMode.Software, false) => "MANUAL",
        (FanControlMode.Firmware, _) => "BIOS",
        _ => "—",
    };

    public bool IsSoftwareControlled => Mode == FanControlMode.Software;

    /// <summary>
    /// The curve owns the duty while one is bound, so the slider stands down rather
    /// than letting the user fight an engine that overwrites them every poll.
    /// </summary>
    public bool IsSliderAvailable => CanControl && !IsCurveDriven;

    /// <summary>
    /// Duty the curve last asked for, when a curve is driving this header.
    /// </summary>
    [ObservableProperty]
    public partial double? CurveTarget { get; set; }

    /// <summary>
    /// Where the duty is actually headed, from whichever source owns the fan.
    /// </summary>
    /// <remarks>
    /// The slider position is only the answer while the user owns the fan. A curve
    /// writes duties straight to the hardware without touching the slider, so
    /// reading <see cref="TargetDuty"/> for a curve-driven header reports whatever
    /// the firmware happened to be doing when the binding was created.
    /// </remarks>
    public double? EffectiveTarget => IsCurveDriven ? CurveTarget : TargetDuty;

    /// <summary>
    /// The fan is still travelling towards the requested duty.
    /// </summary>
    /// <remarks>
    /// An NCT6687D does not jump to a new duty cycle, it slews — measured at roughly
    /// two percent per second on an MSI X870, so 60 % to 85 % takes about fourteen
    /// seconds. Without showing the target, dragging the slider to 85 and reading
    /// "62 %" looks exactly like a write that did not take.
    /// </remarks>
    public bool IsSlewing =>
        IsSoftwareControlled
        && DutyPercent is { } duty
        && EffectiveTarget is { } target
        && Math.Abs(duty - target) > 2;

    public string TargetDisplay => EffectiveTarget is { } target ? $"→ {target:0} %" : string.Empty;

    /// <summary>
    /// We are driving this header and nothing is turning. Worth flagging loudly: it
    /// is the one outcome of dragging a slider that can cost hardware, and unlike an
    /// empty header it is a state the user just created.
    /// </summary>
    public bool IsStoppedByUs => IsSoftwareControlled && Rpm is null or 0;

    /// <summary>Whether the slider should respond at all.</summary>
    public bool IsSliderEnabled => IsSliderAvailable;

    public void Update(FanSnapshot snapshot, SensorHistory history, bool isEmptyHeader)
    {
        Name = snapshot.Name;
        HardwareName = snapshot.HardwareName;
        Rpm = snapshot.Rpm;
        DutyPercent = snapshot.DutyPercent;
        CanControl = snapshot.CanControl;
        Mode = snapshot.Mode;
        IsEmptyHeader = isEmptyHeader;
        Category = snapshot.Category;
        MinDuty = snapshot.MinDuty;
        MaxDuty = snapshot.MaxDuty;

        // Follow the hardware only while the firmware owns the header. Once we are
        // driving it the slider is the source of truth: re-reading the chip every
        // second would drag the handle back under the user's finger, and a duty the
        // chip rounds to 59 would fight a slider the user put at 60.
        if (!IsSoftwareControlled)
        {
            _applyingSnapshot = true;

            // Clamp into the range the slider will enforce anyway, so it has no
            // reason to coerce and echo a correction back at us.
            TargetDuty = Math.Clamp(
                snapshot.DutyPercent ?? snapshot.MinDuty,
                snapshot.MinDuty,
                snapshot.MaxDuty);

            _applyingSnapshot = false;

            // The firmware owns this header again, so any gesture that came before
            // is spent. Without this, releasing a fan and letting it re-sync would
            // leave the flag set and the next coerced echo would re-engage it.
            _userIsAdjusting = false;
        }

        RpmHistory = history.Read(SensorHistory.RpmSeriesId(snapshot.Id));
        DutyHistory = history.Read(SensorHistory.DutySeriesId(snapshot.Id));
    }

    /// <summary>
    /// Hands this header back to the motherboard. Also resets the slider from the
    /// next poll, since <see cref="Update"/> resumes following once the mode flips.
    /// </summary>
    [RelayCommand]
    private void ReleaseToFirmware() => _release(Id);

    /// <summary>Opens the curve editor for this fan, binding one if there is none.</summary>
    [RelayCommand]
    private void OpenCurve() => _openCurve(Id);

    /// <summary>
    /// Records that the user has physically grabbed this fan's slider. Called by the
    /// view on pointer or keyboard input; see <see cref="_userIsAdjusting"/>.
    /// </summary>
    public void BeginUserAdjust() => _userIsAdjusting = true;

    /// <summary>
    /// Moves the slider to a duty a profile has just written, without writing it
    /// again. The duty is already on its way to the hardware; echoing it back would
    /// be a redundant driver round trip.
    /// </summary>
    public void ApplyProfileDuty(double percent)
    {
        _applyingSnapshot = true;
        TargetDuty = Math.Clamp(percent, MinDuty, MaxDuty);
        _applyingSnapshot = false;

        // The profile, not a gesture, put it here — so a later coerced echo must not
        // be mistaken for the user taking over.
        _userIsAdjusting = false;
        Mode = FanControlMode.Software;
    }

    partial void OnTargetDutyChanged(double value)
    {
        if (_applyingSnapshot || !CanControl || !_userIsAdjusting)
        {
            return;
        }

        // Flip the badge immediately rather than waiting a poll for the chip to
        // confirm; otherwise the card reads "BIOS" while the user is dragging it.
        Mode = FanControlMode.Software;

        RaiseTargetChanged();

        _setDuty(Id, value);
    }

    partial void OnRpmChanged(int? value)
    {
        OnPropertyChanged(nameof(RpmDisplay));
        OnPropertyChanged(nameof(IsStoppedByUs));
    }

    partial void OnDutyPercentChanged(double? value)
    {
        OnPropertyChanged(nameof(DutyDisplay));
        OnPropertyChanged(nameof(IsSlewing));
    }

    partial void OnModeChanged(FanControlMode value)
    {
        OnPropertyChanged(nameof(ModeDisplay));
        OnPropertyChanged(nameof(IsSoftwareControlled));
        OnPropertyChanged(nameof(IsStoppedByUs));
        OnPropertyChanged(nameof(IsSlewing));
    }

    partial void OnCanControlChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSliderAvailable));
        OnPropertyChanged(nameof(IsSliderEnabled));
    }

    partial void OnIsCurveDrivenChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeDisplay));
        OnPropertyChanged(nameof(IsSliderAvailable));
        OnPropertyChanged(nameof(IsSliderEnabled));
        RaiseTargetChanged();
    }

    partial void OnCurveTargetChanged(double? value) => RaiseTargetChanged();

    private void RaiseTargetChanged()
    {
        OnPropertyChanged(nameof(EffectiveTarget));
        OnPropertyChanged(nameof(IsSlewing));
        OnPropertyChanged(nameof(TargetDisplay));
    }

}
