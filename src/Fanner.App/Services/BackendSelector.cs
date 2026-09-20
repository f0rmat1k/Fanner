using Fanner.Core.Abstractions;
using Fanner.Core.Simulation;
using Fanner.Hardware.Windows;

namespace Fanner.App.Services;

/// <summary>
/// Picks the backend for this run.
/// </summary>
internal static class BackendSelector
{
    /// <summary>Command-line switch that forces the fake backend.</summary>
    public const string SimulateSwitch = "--simulate";

    public static bool IsSimulationRequested(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, SimulateSwitch, StringComparison.OrdinalIgnoreCase));

    public static IHardwareBackend Create(IReadOnlyList<string> args) =>
        IsSimulationRequested(args)
            ? new SimulatedBackend()
            : new LhmBackend();
}
