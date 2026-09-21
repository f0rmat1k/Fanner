using Avalonia;
using Fanner.App.Services;
using System;

namespace Fanner.App;

sealed class Program
{
    /// <summary>
    /// The claim on being the running copy, for as long as this process lives.
    /// </summary>
    internal static SingleInstance? Instance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Before anything else, and before the window: two copies of a fan
        // controller fight over the same headers. A duplicate launch has already
        // been redirected to the copy that is running by the time this returns null.
        using var instance = SingleInstance.Acquire();

        if (instance is null)
        {
            return;
        }

        Instance = instance;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
