using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanner.App.Services;
using Fanner.Core.Abstractions;
using Fanner.Core.Config;
using Fanner.Core.Curves;
using Fanner.Core.Model;
using Fanner.Core.Monitoring;
using Fanner.Core.Safety;
using Fanner.Core.Simulation;

namespace Fanner.App.ViewModels;

public enum ShellState
{
    Starting,
    Running,
    Failed,
}

/// <summary>
/// The dashboard. Owns the monitor, turns each poll into view models, and — when
/// hardware access fails — explains why and offers the fix.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Dictionary<string, FanViewModel> _fansById = [];
    private readonly Dictionary<string, SensorViewModel> _sensorsById = [];
    private readonly string[] _args;
    private readonly ConfigStore _configStore = new();

    private MonitorService? _monitor;
    private FannerConfig _config = new();
    private bool _savedBindingsApplied;
    private bool _applyingProfile;
    private bool _disposed;

    public MainViewModel()
        : this([])
    {
    }

    public MainViewModel(string[] args)
    {
        _args = args;
        IsSimulated = BackendSelector.IsSimulationRequested(args);
    }

    [ObservableProperty]
    public partial ShellState State { get; set; } = ShellState.Starting;

    [ObservableProperty]
    public partial string StatusHeadline { get; set; } = "Looking for fan hardware…";

    [ObservableProperty]
    public partial string StatusDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackendName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetectedHardware { get; set; } = string.Empty;

    /// <summary>True when the failure is one that restarting with elevation would fix.</summary>
    [ObservableProperty]
    public partial bool CanFixByElevating { get; set; }

    /// <summary>True when PawnIO is missing, so the UI can link to the installer.</summary>
    [ObservableProperty]
    public partial bool NeedsDriverInstall { get; set; }

    [ObservableProperty]
    public partial bool IsSimulated { get; set; }

    /// <summary>
    /// Empty headers are hidden by default. This board exposes ten and only six are
    /// wired, so showing them all by default buries the ones that matter.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowEmptyHeaders { get; set; }

    [ObservableProperty]
    public partial string LastUpdated { get; set; } = string.Empty;

    /// <summary>Set after the watchdog has released the fans. Stays until dismissed.</summary>
    [ObservableProperty]
    public partial bool HasSafetyAlert { get; set; }

    [ObservableProperty]
    public partial string SafetyAlert { get; set; } = string.Empty;

    /// <summary>True while at least one header is under our control.</summary>
    [ObservableProperty]
    public partial bool IsDrivingAnyFan { get; set; }

    /// <summary>The active watchdog limits, shown while we are driving something.</summary>
    [ObservableProperty]
    public partial string WatchdogSummary { get; set; } = string.Empty;

    /// <summary>
    /// The curve being edited, or null when the side panel shows temperatures.
    /// </summary>
    [ObservableProperty]
    public partial CurveEditorViewModel? CurveEditor { get; set; }

    /// <summary>
    /// True when curves are configured but standing down — after the watchdog fired,
    /// or after the user pressed restore. Surfaced because a silently inert curve is
    /// indistinguishable from a broken one.
    /// </summary>
    [ObservableProperty]
    public partial bool AreCurvesSuspended { get; set; }

    [ObservableProperty]
    public partial string CurvesSuspendedReason { get; set; } = string.Empty;

    /// <summary>
    /// Name of the profile in force. The active profile always mirrors the current
    /// state — every edit writes straight into it — so there is no such thing as
    /// unsaved changes to explain or recover.
    /// </summary>
    [ObservableProperty]
    public partial string? SelectedProfileName { get; set; }

    [ObservableProperty]
    public partial bool RunAtStartup { get; set; }

    /// <summary>Version of this build, for the header.</summary>
    public string Version { get; } = AppVersion.Number;

    /// <summary>Version and commit, for a bug report.</summary>
    public string VersionDetail { get; } = $"Fanner {AppVersion.Full}";

    /// <summary>
    /// What the logon task will start, in words, or empty when there is no task.
    /// </summary>
    /// <remarks>
    /// Worth saying out loud because updating Fanner means downloading another
    /// executable, and the task keeps pointing at the one it was registered with.
    /// Without this the only way to know which of three files in a Downloads folder
    /// Windows actually starts is to reboot and watch.
    /// </remarks>
    [ObservableProperty]
    public partial string StartupTarget { get; set; } = string.Empty;

    /// <summary>True when the logon task starts some other copy than this one.</summary>
    [ObservableProperty]
    public partial bool StartupPointsElsewhere { get; set; }

    /// <summary>
    /// Set when another Fanner is running that this one could not stand aside for.
    /// </summary>
    /// <remarks>
    /// Copies from 0.4.1 and earlier do not know about each other, so the pair can
    /// only be spotted after the fact and reported. It matters: two copies both
    /// write duty cycles, and quitting either one hands every header back to the
    /// firmware while the other still believes it is driving.
    /// </remarks>
    [ObservableProperty]
    public partial string OtherCopyWarning { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CloseToTray { get; set; } = true;

    /// <summary>Set when a settings change could not be carried out.</summary>
    [ObservableProperty]
    public partial string SettingsError { get; set; } = string.Empty;

    /// <summary>Name typed into the "add profile" box.</summary>
    [ObservableProperty]
    public partial string NewProfileName { get; set; } = string.Empty;

    public ObservableCollection<string> ProfileNames { get; } = [];

    /// <summary>Raised when the set of profiles or the active one changes, for the tray menu.</summary>
    public event EventHandler? ProfilesChanged;

    /// <summary>
    /// Fans grouped by the hardware they hang off, so the graphics card does not sit
    /// in the same heap as the case headers.
    /// </summary>
    public ObservableCollection<FanGroupViewModel> FanGroups { get; } = [];

    public ObservableCollection<SensorViewModel> Temperatures { get; } = [];

    public bool IsStarting => State == ShellState.Starting;

    public bool IsRunning => State == ShellState.Running;

    public bool IsFailed => State == ShellState.Failed;

    public bool IsEditingCurve => CurveEditor is not null;

    /// <summary>
    /// Settings live in the side panel rather than a flyout.
    /// </summary>
    /// <remarks>
    /// A popup dismisses itself whenever the window loses activation, which on a
    /// busy multi-monitor desktop happens constantly and makes the panel feel
    /// broken. The side panel also gives the settings room to explain themselves.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsShowingSettings { get; set; }

    /// <summary>The side panel falls back to temperatures when nothing else claims it.</summary>
    public bool IsShowingTemperatures => CurveEditor is null && !IsShowingSettings;

    partial void OnStateChanged(ShellState value)
    {
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
    }

    partial void OnCurveEditorChanged(CurveEditorViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditingCurve));
        OnPropertyChanged(nameof(IsShowingTemperatures));

        // The panel holds one thing at a time.
        if (value is not null)
        {
            IsShowingSettings = false;
        }
    }

    partial void OnIsShowingSettingsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsShowingTemperatures));

        if (value)
        {
            CurveEditor = null;
        }
    }

    [RelayCommand]
    private void OpenSettings() => IsShowingSettings = true;

    [RelayCommand]
    private void CloseSettings() => IsShowingSettings = false;

    public async Task StartAsync()
    {
        // Read the config before the hardware comes up, but do not act on it until
        // the first poll tells us which fans and sensors this machine actually has.
        _config = _configStore.Load();
        LoadSettingsFromConfig();

        OtherCopyWarning = DescribeOtherCopies();

        var backend = BackendSelector.Create(_args);

        _monitor = new MonitorService(backend);
        _monitor.SnapshotUpdated += OnSnapshotUpdated;
        _monitor.PollFailed += OnPollFailed;
        _monitor.SafetyTripped += OnSafetyTripped;

        BackendName = backend.Name;
        WatchdogSummary = _monitor.Watchdog.Summary;

        var status = await _monitor.StartAsync();

        if (status.IsOperational)
        {
            State = ShellState.Running;
            StatusHeadline = IsSimulated ? "Running on simulated hardware" : "Monitoring";
            StatusDetail = string.Empty;
            DetectedHardware = string.Join(" · ", status.DetectedHardware);
            CanFixByElevating = false;
            NeedsDriverInstall = false;
            return;
        }

        State = ShellState.Failed;
        StatusHeadline = status.Failure switch
        {
            BackendFailure.NeedsElevation => "Administrator rights needed",
            BackendFailure.DriverUnavailable => "Hardware access driver missing",
            BackendFailure.NoSupportedHardware => "No supported sensor chip found",
            BackendFailure.UnsupportedPlatform => "Unsupported platform",
            _ => "Hardware access failed",
        };

        StatusDetail = string.IsNullOrWhiteSpace(status.Detail)
            ? status.UserMessage
            : $"{status.UserMessage}\n\n{status.Detail}";

        CanFixByElevating = status.Failure == BackendFailure.NeedsElevation;
        NeedsDriverInstall = status.Failure == BackendFailure.DriverUnavailable;
    }

    /// <summary>
    /// Points the logon task at the copy of Fanner running right now.
    /// </summary>
    /// <remarks>
    /// The alternative is unticking the box and ticking it again, which works only
    /// because registering happens to use the running executable's path — a detail
    /// nobody should have to know to update their fan controller.
    /// </remarks>
    [RelayCommand]
    private void UseThisCopyForStartup()
    {
        var error = StartupTask.Register();

        SettingsError = error is null
            ? string.Empty
            : $"Could not change the startup task: {error}";

        RunAtStartup = StartupTask.IsRegistered();
        RefreshStartupTarget();
    }

    /// <summary>Works out which copy the logon task starts, and says so.</summary>
    private void RefreshStartupTarget()
    {
        var registered = StartupTask.RegisteredExecutable();

        if (registered is null)
        {
            StartupTarget = string.Empty;
            StartupPointsElsewhere = false;
            return;
        }

        var here = Environment.ProcessPath;

        if (here is not null && string.Equals(registered, here, StringComparison.OrdinalIgnoreCase))
        {
            StartupTarget = "Windows starts this copy at logon.";
            StartupPointsElsewhere = false;
            return;
        }

        StartupPointsElsewhere = true;
        StartupTarget = File.Exists(registered)
            ? $"Windows starts a different copy at logon: {registered}"
            : $"Windows is set to start a file that is no longer there: {registered}";
    }

    /// <summary>
    /// Describes any other Fanner already running, or an empty string.
    /// </summary>
    /// <remarks>
    /// Matched on the process name rather than its path, which cannot be read when
    /// the other copy is elevated and this one is not — the usual pairing. The name
    /// is loose on purpose: released builds are called things like
    /// <c>Fanner-v0.4.2-win-x64</c>, and those are exactly the copies too old to
    /// stand aside on their own.
    /// </remarks>
    private static string DescribeOtherCopies()
    {
        try
        {
            var mine = Environment.ProcessId;
            var others = new List<int>();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id != mine
                        && process.ProcessName.Contains("Fanner", StringComparison.OrdinalIgnoreCase))
                    {
                        others.Add(process.Id);
                    }
                }
            }

            return others.Count == 0
                ? string.Empty
                : $"Another copy of Fanner is running ({string.Join(", ", others.Select(id => $"process {id}"))}). "
                  + "Two copies both write fan speeds, and quitting either one gives every fan back to the "
                  + "motherboard. Quit the other from its tray icon.";
        }
        catch (Exception)
        {
            // Enumerating processes is a courtesy, not a requirement.
            return string.Empty;
        }
    }

    [RelayCommand]
    private void RelaunchElevated()
    {
        if (Elevation.TryRelaunchElevated())
        {
            Shutdown();
            return;
        }

        StatusDetail = "Could not start an elevated copy. "
            + "The prompt may have been dismissed, or policy forbids elevation.";
    }

    [RelayCommand]
    private static void OpenDriverPage() => OpenUrl("https://pawnio.eu/");

    /// <summary>
    /// Drops to the fake backend so the dashboard is usable — and demoable — on a
    /// machine that cannot reach the real hardware.
    /// </summary>
    [RelayCommand]
    private async Task UseSimulationAsync()
    {
        DisposeMonitor();
        ClearReadings();

        IsSimulated = true;
        State = ShellState.Starting;
        StatusHeadline = "Starting simulated hardware…";

        _monitor = new MonitorService(new SimulatedBackend());
        _monitor.SnapshotUpdated += OnSnapshotUpdated;
        _monitor.PollFailed += OnPollFailed;
        _monitor.SafetyTripped += OnSafetyTripped;
        WatchdogSummary = _monitor.Watchdog.Summary;

        var status = await _monitor.StartAsync();

        BackendName = "Simulated hardware";
        State = status.IsOperational ? ShellState.Running : ShellState.Failed;
        StatusHeadline = "Running on simulated hardware";
        StatusDetail = "Nothing here touches your fans.";
        DetectedHardware = string.Join(" · ", status.DetectedHardware);
        CanFixByElevating = false;
        NeedsDriverInstall = false;
    }

    /// <summary>
    /// Hands every header back to the motherboard. The escape hatch: one click
    /// returns the machine to its known-good configuration from any state.
    /// </summary>
    [RelayCommand]
    private void RestoreAllToFirmware()
    {
        _monitor?.ReleaseAll();
        RefreshCurveState();
    }

    [RelayCommand]
    private void DismissSafetyAlert()
    {
        HasSafetyAlert = false;
        SafetyAlert = string.Empty;
    }

    /// <summary>
    /// Puts the curves back to work after a suspension. Separate from dismissing the
    /// warning on purpose: acknowledging that a chip got too hot should not silently
    /// restart the thing that let it.
    /// </summary>
    [RelayCommand]
    private void ResumeCurves()
    {
        _monitor?.Curves.Resume();
        RefreshCurveState();
    }

    [RelayCommand]
    private void CloseCurveEditor() => CurveEditor = null;

    private void SetDuty(string fanId, double percent) => _monitor?.SetDuty(fanId, percent);

    private void ReleaseFan(string fanId)
    {
        _monitor?.ReleaseToFirmware(fanId);

        // The binding is gone, so the editor showing it is stale.
        if (CurveEditor?.Fan.Id == fanId)
        {
            CurveEditor = null;
        }

        SaveConfig();
    }

    /// <summary>
    /// Opens the curve editor for a fan, creating a binding if it has none. Clicking
    /// the button is the gesture that engages control, so the fan starts being driven
    /// right away rather than waiting for a separate confirmation.
    /// </summary>
    private void OpenCurve(string fanId)
    {
        if (_monitor is null || !_fansById.TryGetValue(fanId, out var fan))
        {
            return;
        }

        var sensors = BuildSensorOptions();
        if (sensors.Count == 0)
        {
            StatusDetail = "No temperature sensor is available to drive a curve.";
            return;
        }

        var existing = _monitor.Curves.For(fanId);

        var selected = sensors.FirstOrDefault(s => s.Id == existing?.SensorId)
            ?? DefaultSensor(sensors);

        var editor = new CurveEditorViewModel(
            fan,
            sensors,
            selected,
            existing?.Curve ?? FanCurve.Balanced(),
            OnCurveEdited,
            () => ReleaseFan(fanId));

        if (existing is not null)
        {
            editor.ResponseSeconds = existing.Response.TotalSeconds;
            editor.HysteresisPercent = existing.HysteresisPercent;
            editor.KickstartDuty = existing.KickstartDuty;
        }

        CurveEditor = editor;

        // Bind straight away so the preview marker and the fan agree from the start.
        OnCurveEdited(editor);
    }

    private void OnCurveEdited(CurveEditorViewModel editor)
    {
        if (_monitor is null)
        {
            return;
        }

        try
        {
            _monitor.Curves.Set(new CurveBinding
            {
                FanId = editor.Fan.Id,
                SensorId = editor.SelectedSensor.Id,
                Curve = editor.BuildCurve(),
                Response = TimeSpan.FromSeconds(editor.ResponseSeconds),
                HysteresisPercent = editor.HysteresisPercent,
                KickstartDuty = editor.KickstartDuty,
            });
        }
        catch (ArgumentException)
        {
            // Mid-edit the points can briefly be unusable; the next change fixes it.
            return;
        }

        // Editing is an explicit act, so it lifts a suspension the user may have
        // forgotten about — otherwise the curve would silently do nothing.
        if (_monitor.Curves.IsSuspended)
        {
            _monitor.Curves.Resume();
        }

        RefreshCurveState();
        SaveConfig();
    }

    /// <summary>
    /// Picks a sensible default source: the CPU package temperature, which is what
    /// nearly every curve is built on.
    /// </summary>
    private static SensorOption DefaultSensor(IReadOnlyList<SensorOption> sensors) =>
        sensors.FirstOrDefault(s => s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
        ?? sensors[0];

    private List<SensorOption> BuildSensorOptions() =>
        Temperatures
            .Select(s => new SensorOption(s.Id, s.Name, s.HardwareName))
            .ToList();

    private void LoadSettingsFromConfig()
    {
        _applyingProfile = true;

        ProfileNames.Clear();
        foreach (var profile in _config.Profiles)
        {
            ProfileNames.Add(profile.Name);
        }

        SelectedProfileName = _config.Active.Name;
        CloseToTray = _config.CloseToTray;

        // Trust the system, not the file: the task can be removed from Task
        // Scheduler without Fanner ever knowing, and a checkbox that disagrees with
        // reality is worse than no checkbox.
        RunAtStartup = StartupTask.IsRegistered();

        // A task pointing at a file that is gone starts nothing and says nothing,
        // which is the worst way for "start with Windows" to fail. Updating means
        // downloading a new executable and deleting the old one, so this is the
        // normal path, not an edge case.
        if (RunAtStartup && StartupTask.TargetIsMissing())
        {
            StartupTask.Register();
        }

        RefreshStartupTarget();

        _applyingProfile = false;
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Folds the live state into the active profile and writes the file.
    /// </summary>
    private void SaveConfig()
    {
        if (_monitor is null || _applyingProfile)
        {
            return;
        }

        var updated = CaptureActiveProfile();

        var profiles = _config.Profiles
            .Select(p => p.Name == updated.Name ? updated : p)
            .ToList();

        if (profiles.All(p => p.Name != updated.Name))
        {
            profiles.Add(updated);
        }

        _config = _config with
        {
            Profiles = profiles,
            ActiveProfile = updated.Name,
            CloseToTray = CloseToTray,
            RunAtStartup = RunAtStartup,
        };

        _configStore.Save(_config);
    }

    /// <summary>
    /// The current state as a profile: every curve, plus every fan we are holding at
    /// a fixed duty without one.
    /// </summary>
    private Profile CaptureActiveProfile()
    {
        var bindings = _monitor?.Curves.Bindings.Select(CurveBindingConfig.From).ToList() ?? [];

        var manual = _fansById.Values
            .Where(f => f.IsSoftwareControlled && !f.IsCurveDriven)
            .Select(f => new ManualDutyConfig { FanId = f.Id, DutyPercent = f.TargetDuty })
            .ToList();

        return new Profile
        {
            Name = SelectedProfileName ?? Profile.DefaultName,
            Bindings = bindings,
            ManualDuties = manual,
        };
    }

    /// <summary>
    /// Applies the saved profile once, after the first poll has told us what exists.
    /// </summary>
    private void ApplySavedBindings(HardwareSnapshot snapshot)
    {
        if (_savedBindingsApplied || _monitor is null)
        {
            return;
        }

        _savedBindingsApplied = true;
        ApplyProfile(_config.Active, snapshot);
    }

    /// <summary>
    /// Puts a profile into force.
    /// </summary>
    /// <remarks>
    /// Fans the incoming profile does not mention are handed back to the firmware
    /// first. Without that, switching from a profile that pinned a fan to one that
    /// does not would leave it stuck at the old duty — silently inheriting settings
    /// from a profile the user just switched away from.
    /// <para>
    /// Entries naming hardware this machine does not have are dropped: a curve bound
    /// to a missing sensor drives nothing, and leaving it in place would make the fan
    /// look configured when it is not.
    /// </para>
    /// </remarks>
    private void ApplyProfile(Profile profile, HardwareSnapshot snapshot)
    {
        if (_monitor is null)
        {
            return;
        }

        var fanIds = snapshot.Fans.Select(f => f.Id).ToHashSet();
        var sensorIds = snapshot.Temperatures.Select(t => t.Id).ToHashSet();

        var bindings = profile.Bindings
            .Select(entry => entry.ToBinding())
            .OfType<CurveBinding>()
            .Where(b => fanIds.Contains(b.FanId) && sensorIds.Contains(b.SensorId))
            .ToList();

        var manual = profile.ManualDuties
            .Where(m => fanIds.Contains(m.FanId))
            .ToList();

        var wanted = bindings.Select(b => b.FanId)
            .Concat(manual.Select(m => m.FanId))
            .ToHashSet();

        _applyingProfile = true;

        try
        {
            foreach (var fan in snapshot.Fans)
            {
                var held = fan.Mode == FanControlMode.Software
                    || _monitor.Curves.For(fan.Id) is not null;

                if (held && !wanted.Contains(fan.Id))
                {
                    _monitor.ReleaseToFirmware(fan.Id);
                }
            }

            _monitor.Curves.Clear();

            foreach (var binding in bindings)
            {
                _monitor.Curves.Set(binding);
            }

            foreach (var entry in manual)
            {
                _monitor.SetDuty(entry.FanId, entry.DutyPercent);

                if (_fansById.TryGetValue(entry.FanId, out var fan))
                {
                    fan.ApplyProfileDuty(entry.DutyPercent);
                }
            }

            // A profile the user just chose is an instruction to run it.
            if (_monitor.Curves.IsSuspended && bindings.Count > 0)
            {
                _monitor.Curves.Resume();
            }
        }
        finally
        {
            _applyingProfile = false;
        }

        CurveEditor = null;
        RefreshCurveState();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var name = NewProfileName.Trim();

        if (name.Length == 0 || ProfileNames.Contains(name))
        {
            SettingsError = name.Length == 0
                ? "Give the profile a name."
                : $"A profile called \"{name}\" already exists.";
            return;
        }

        SettingsError = string.Empty;
        NewProfileName = string.Empty;

        // Starts as a copy of what is running, which is almost always what someone
        // wants when they save the setup they have just finished tuning.
        var captured = CaptureActiveProfile() with { Name = name };

        _config = _config with { Profiles = [.. _config.Profiles, captured] };

        ProfileNames.Add(name);
        SelectedProfileName = name;

        SaveConfig();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfileName is not { } name || _config.Profiles.Count <= 1)
        {
            // Never leave the user with no profile at all.
            SettingsError = "The last profile cannot be deleted.";
            return;
        }

        SettingsError = string.Empty;

        _config = _config with { Profiles = _config.Profiles.Where(p => p.Name != name).ToList() };
        ProfileNames.Remove(name);

        SelectedProfileName = _config.Profiles[0].Name;

        _configStore.Save(_config with { ActiveProfile = SelectedProfileName });
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Switches profile from outside the window, used by the tray menu.</summary>
    public void SelectProfile(string name) => SelectedProfileName = name;

    partial void OnSelectedProfileNameChanged(string? value)
    {
        ProfilesChanged?.Invoke(this, EventArgs.Empty);

        if (_applyingProfile || value is null || _monitor is null || !_savedBindingsApplied)
        {
            return;
        }

        if (_config.Find(value) is { } profile)
        {
            ApplyProfile(profile, _monitor.Latest);
            _config = _config with { ActiveProfile = value };
            _configStore.Save(_config);
        }
    }

    partial void OnRunAtStartupChanged(bool value)
    {
        if (_applyingProfile)
        {
            return;
        }

        // Only act on a request that disagrees with the system. Registering rewrites
        // the task with the path of whichever copy of Fanner is running, so a stray
        // write from a binding — the settings panel pushing the checkbox's own
        // initial value back before it has been given ours — is enough to point
        // logon at a copy nobody meant to install.
        if (value == StartupTask.IsRegistered())
        {
            return;
        }

        var error = value ? StartupTask.Register() : StartupTask.Unregister();

        if (error is not null)
        {
            SettingsError = $"Could not change the startup task: {error}";

            // Put the checkbox back where the system actually is.
            _applyingProfile = true;
            RunAtStartup = StartupTask.IsRegistered();
            _applyingProfile = false;
            return;
        }

        SettingsError = string.Empty;
        SaveConfig();
    }

    partial void OnCloseToTrayChanged(bool value) => SaveConfig();

    private void RefreshCurveState()
    {
        if (_monitor is null)
        {
            return;
        }

        AreCurvesSuspended = _monitor.Curves.IsSuspended && _monitor.Curves.HasBindings;
        CurvesSuspendedReason = _monitor.Curves.SuspendedReason ?? string.Empty;
    }

    private void OnSafetyTripped(object? sender, WatchdogTrip trip) =>
        Dispatcher.UIThread.Post(() =>
        {
            // The fans are already back with the firmware by the time this arrives,
            // and the curves are already suspended; this is purely telling the user
            // what happened and why.
            HasSafetyAlert = true;
            SafetyAlert = trip.Message;
            RefreshCurveState();
        });

    private void OnSnapshotUpdated(object? sender, HardwareSnapshot snapshot)
    {
        var history = _monitor?.History;
        if (history is null)
        {
            return;
        }

        // Polls arrive on the monitor thread; every collection below is bound.
        Dispatcher.UIThread.Post(() => Apply(snapshot, history));
    }

    private void OnPollFailed(object? sender, Exception error) =>
        Dispatcher.UIThread.Post(() => StatusDetail = $"Last read failed: {error.Message}");

    private void Apply(HardwareSnapshot snapshot, SensorHistory history)
    {
        if (_disposed)
        {
            return;
        }

        for (var i = 0; i < snapshot.Fans.Count; i++)
        {
            var fan = snapshot.Fans[i];

            if (!_fansById.TryGetValue(fan.Id, out var viewModel))
            {
                viewModel = new FanViewModel(fan.Id, SetDuty, ReleaseFan, OpenCurve);
                _fansById[fan.Id] = viewModel;
            }

            viewModel.Order = i;
            viewModel.Update(fan, history, _monitor?.Presence.IsEmpty(fan) ?? fan.LooksUnpopulated);
            viewModel.IsCurveDriven = _monitor?.Curves.For(fan.Id) is not null;
            viewModel.CurveTarget = _monitor?.Curves.LastDutyFor(fan.Id);
        }

        foreach (var sensor in snapshot.Temperatures)
        {
            if (!_sensorsById.TryGetValue(sensor.Id, out var viewModel))
            {
                viewModel = new SensorViewModel(sensor.Id, sensor.Kind);
                _sensorsById[sensor.Id] = viewModel;
            }

            viewModel.Update(sensor, history);
        }

        SyncFans();
        SyncTemperatures();
        ApplySavedBindings(snapshot);
        RefreshCurveState();
        UpdateCurveMarker(snapshot);

        IsDrivingAnyFan = snapshot.Fans.Any(f => f.Mode == FanControlMode.Software);
        LastUpdated = snapshot.Timestamp.ToString("HH:mm:ss");
    }

    /// <summary>Feeds the open editor the live reading of whichever sensor it is bound to.</summary>
    private void UpdateCurveMarker(HardwareSnapshot snapshot)
    {
        if (CurveEditor is not { } editor)
        {
            return;
        }

        editor.MarkerTemperature = snapshot.Temperatures
            .FirstOrDefault(t => t.Id == editor.SelectedSensor.Id)?.Value;
    }

    /// <summary>
    /// Brings the grouped list in line with the filter.
    /// </summary>
    /// <remarks>
    /// Rebuilt only when the membership actually changed. Replacing it every second
    /// would fight the user's scroll position and drop the card they are reaching
    /// for.
    /// </remarks>
    private void SyncFans()
    {
        var desired = _fansById.Values
            .Where(f => ShowEmptyHeaders || !f.IsEmptyHeader)
            .GroupBy(f => f.HardwareName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Key = g.Key,
                Category = g.First().Category,
                Fans = g.OrderBy(f => f.Order).ToList(),
            })
            .Select(g => new
            {
                g.Key,
                g.Category,
                g.Fans,
                Group = new FanGroupViewModel(g.Key, g.Category, g.Key),
            })
            .OrderBy(g => g.Group.SortOrder)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unchanged = FanGroups.Count == desired.Count
            && FanGroups.Zip(desired).All(pair =>
                pair.First.Key == pair.Second.Key
                && pair.First.Fans.SequenceEqual(pair.Second.Fans));

        if (unchanged)
        {
            return;
        }

        FanGroups.Clear();

        foreach (var entry in desired)
        {
            foreach (var fan in entry.Fans)
            {
                entry.Group.Fans.Add(fan);
            }

            FanGroups.Add(entry.Group);
        }
    }

    private void SyncTemperatures()
    {
        // Category first, so the CPU and GPU readings a curve would actually use sit
        // at the top instead of below whichever DIMM happens to sort first.
        var desired = _sensorsById.Values
            .OrderBy(s => s.Category.SortOrder())
            .ThenBy(s => s.HardwareName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (Temperatures.SequenceEqual(desired))
        {
            return;
        }

        Temperatures.Clear();

        foreach (var sensor in desired)
        {
            Temperatures.Add(sensor);
        }
    }

    partial void OnShowEmptyHeadersChanged(bool value) => SyncFans();

    private void ClearReadings()
    {
        _fansById.Clear();
        _sensorsById.Clear();
        FanGroups.Clear();
        Temperatures.Clear();
        _monitor?.Presence.Clear();

        // The editor points at view models and hardware ids from the old backend.
        CurveEditor = null;
        _savedBindingsApplied = false;
        AreCurvesSuspended = false;
    }

    private void DisposeMonitor()
    {
        if (_monitor is null)
        {
            return;
        }

        _monitor.SnapshotUpdated -= OnSnapshotUpdated;
        _monitor.PollFailed -= OnPollFailed;
        _monitor.SafetyTripped -= OnSafetyTripped;
        _monitor.Dispose();
        _monitor = null;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser, or the shell refused. Not worth interrupting the user over.
        }
    }

    private static void Shutdown()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Disposing the monitor hands every fan back to the firmware first.
        DisposeMonitor();
    }
}
