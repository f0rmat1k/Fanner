using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace Fanner.Hardware.Windows;

/// <summary>
/// Detects the PawnIO kernel driver, which every motherboard sensor depends on.
/// </summary>
/// <remarks>
/// LibreHardwareMonitor 0.9.6 replaced WinRing0 with PawnIO: a signed, HVCI-compatible
/// driver that runs sandboxed bytecode modules instead of handing out raw port I/O.
/// It is a separate install, and when it is absent nothing throws — every ring-0 read
/// simply returns zero. That looks identical to an unsupported motherboard, so we
/// check for the driver up front and say so plainly instead of showing an empty list.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class PawnIoDriver
{
    /// <summary>Where the installer records itself. This is the same key LHM checks.</summary>
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>Official installer, for the message shown when the driver is missing.</summary>
    public const string DownloadUrl = "https://pawnio.eu/";

    public static bool IsInstalled => TryReadVersion(out _);

    /// <summary>Installed driver version, or null when PawnIO is not present.</summary>
    public static string? Version => TryReadVersion(out var version) ? version : null;

    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadVersion(out string? version)
    {
        version = null;

        try
        {
            // The installer is 64-bit and writes to the native view; force it so a
            // 32-bit build of Fanner does not get redirected to Wow6432Node and
            // conclude the driver is missing.
            using var view = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = view.OpenSubKey(UninstallKey);

            if (key is null)
            {
                return false;
            }

            version = key.GetValue("DisplayVersion") as string;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
