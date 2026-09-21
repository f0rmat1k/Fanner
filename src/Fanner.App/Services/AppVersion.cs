using System.Reflection;

namespace Fanner.App.Services;

/// <summary>
/// What version of Fanner this is, for the window and for bug reports.
/// </summary>
/// <remarks>
/// Released builds are downloaded as <c>Fanner-v0.4.2-win-x64.exe</c> and renamed,
/// moved and kept alongside their predecessors, so the file name is not an answer to
/// "which one am I looking at".
/// </remarks>
internal static class AppVersion
{
    static AppVersion()
    {
        // The informational version carries the commit after a '+', put there by the
        // build. The assembly version cannot: it only holds four numbers.
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            Number = "unknown";
            Full = "unknown";
            return;
        }

        var plus = informational.IndexOf('+');

        if (plus < 0)
        {
            Number = informational;
            Full = informational;
            return;
        }

        Number = informational[..plus];

        var commit = informational[(plus + 1)..];
        Full = commit.Length >= 7
            ? $"{Number} ({commit[..7]})"
            : Number;
    }

    /// <summary>Just the version, e.g. "0.4.2".</summary>
    public static string Number { get; }

    /// <summary>
    /// Version and the commit it was built from, e.g. "0.4.2 (e12db61)".
    /// </summary>
    /// <remarks>
    /// The commit is the part that matters when someone reports behaviour from a
    /// build between releases, or from a release rebuilt after a tag was moved.
    /// </remarks>
    public static string Full { get; }
}
