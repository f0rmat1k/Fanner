using System.Diagnostics;

namespace Fanner.App.Services;

/// <summary>
/// Relaunches Fanner with administrator rights.
/// </summary>
/// <remarks>
/// The manifest asks for <c>asInvoker</c> rather than <c>requireAdministrator</c> on
/// purpose. Demanding elevation before the window even opens means a user who just
/// wants to look at their fans gets a UAC prompt with no explanation, and a
/// developer cannot run the app from a normal shell. Instead the app starts, reports
/// exactly what it cannot do, and offers this.
/// </remarks>
internal static class Elevation
{
    /// <summary>
    /// Starts an elevated copy and reports whether it launched. The caller shuts the
    /// current instance down on success; on failure — almost always the user
    /// dismissing the UAC prompt — nothing has changed and the app carries on.
    /// </summary>
    public static bool TryRelaunchElevated()
    {
        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch (Exception)
        {
            // The user cancelled the UAC prompt, or policy forbids elevation.
            return false;
        }
    }
}
