using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Fanner.App.ViewModels;

namespace Fanner.App.Services;

/// <summary>
/// The tray icon and its menu.
/// </summary>
/// <remarks>
/// Built in code rather than XAML because the profile entries are a live list, and
/// a native menu is rebuilt wholesale far more simply than it is data-bound.
/// </remarks>
internal sealed class TrayController : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly Action _showWindow;
    private readonly Action _quit;

    private TrayIcon? _icon;

    public TrayController(MainViewModel viewModel, Action showWindow, Action quit)
    {
        _viewModel = viewModel;
        _showWindow = showWindow;
        _quit = quit;
    }

    /// <summary>
    /// Creates the icon. Returns false if the platform refused, which the caller
    /// must treat as "there is no tray to hide into".
    /// </summary>
    public bool TryCreate()
    {
        try
        {
            _icon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Fanner/Assets/fanner.ico"))),
                ToolTipText = "Fanner",
                IsVisible = true,
            };

            _icon.Clicked += (_, _) => _showWindow();

            Rebuild();

            TrayIcon.SetIcons(Application.Current!, [_icon]);

            _viewModel.ProfilesChanged += OnProfilesChanged;

            return true;
        }
        catch (Exception)
        {
            _icon = null;
            return false;
        }
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>
    /// Rebuilds the menu from scratch. Cheap, and avoids trying to reconcile a
    /// native menu item by item as profiles are added and removed.
    /// </summary>
    private void Rebuild()
    {
        if (_icon is null)
        {
            return;
        }

        var menu = new NativeMenu();

        foreach (var name in _viewModel.ProfileNames)
        {
            var item = new NativeMenuItem(name)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = name == _viewModel.SelectedProfileName,
            };

            var captured = name;
            item.Click += (_, _) => _viewModel.SelectProfile(captured);

            menu.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.Add(new NativeMenuItemSeparator());
        }

        var restore = new NativeMenuItem("Restore all to BIOS");
        restore.Click += (_, _) => _viewModel.RestoreAllToFirmwareCommand.Execute(null);
        menu.Add(restore);

        var show = new NativeMenuItem("Show Fanner");
        show.Click += (_, _) => _showWindow();
        menu.Add(show);

        menu.Add(new NativeMenuItemSeparator());

        // Named "Quit" rather than "Close" on purpose: closing the window leaves
        // Fanner running, and this is the one that genuinely stops it — handing
        // every fan back to the firmware on the way out.
        var quit = new NativeMenuItem("Quit Fanner");
        quit.Click += (_, _) => _quit();
        menu.Add(quit);

        _icon.Menu = menu;
    }

    public void Dispose()
    {
        _viewModel.ProfilesChanged -= OnProfilesChanged;

        if (_icon is null)
        {
            return;
        }

        // Without this the icon lingers in the notification area until the user
        // hovers over it.
        _icon.IsVisible = false;
        _icon.Dispose();
        _icon = null;
    }
}
