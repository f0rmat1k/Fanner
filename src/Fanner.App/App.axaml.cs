using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Fanner.App.Services;
using Fanner.App.ViewModels;
using Fanner.App.Views;

namespace Fanner.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayController? _tray;

    /// <summary>True once the user has asked to quit, as opposed to closing the window.</summary>
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            var args = desktop.Args ?? [];
            _viewModel = new MainViewModel(args);
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closing += OnWindowClosing;

            desktop.MainWindow = _window;

            // Fans must never be left pinned to a software duty cycle after we exit.
            // The monitor's Dispose blocks until its thread has handed every header
            // back to the firmware.
            desktop.ShutdownRequested += (_, _) =>
            {
                _tray?.Dispose();
                _viewModel?.Dispose();
            };

            SetUpTray(args);

            // Bringing the driver up takes a moment; let the window paint first so a
            // slow start looks like loading rather than a hang.
            _ = _viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpTray(string[] args)
    {
        if (_viewModel is null || _desktop is null || _window is null)
        {
            return;
        }

        _tray = new TrayController(_viewModel, ShowWindow, Quit);

        if (_tray.TryCreate())
        {
            // The window is no longer the app's lifetime — closing it only hides it,
            // so the app has to be told explicitly when to stop.
            _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        else
        {
            // No tray means no way back to a hidden window, and no way to quit. Fall
            // back to ordinary behaviour rather than stranding the user.
            _tray.Dispose();
            _tray = null;
            _viewModel.CloseToTray = false;
        }

        var startHidden = _tray is not null
            && args.Any(a => string.Equals(a, StartupTask.MinimisedSwitch, StringComparison.OrdinalIgnoreCase));

        if (startHidden)
        {
            // Launched by the logon task: apply the profile without putting a window
            // in front of someone who has just signed in.
            _window.ShowInTaskbar = false;
            _window.WindowState = WindowState.Minimized;
            _window.Hide();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || _tray is null || _viewModel?.CloseToTray != true)
        {
            return;
        }

        // Closing the window on a fan controller usually means "get out of my way",
        // not "stop controlling my fans".
        e.Cancel = true;
        HideWindow();
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.ShowInTaskbar = true;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void HideWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Hide();
        _window.ShowInTaskbar = false;
    }

    private void Quit()
    {
        _quitting = true;
        _desktop?.Shutdown();
    }
}
