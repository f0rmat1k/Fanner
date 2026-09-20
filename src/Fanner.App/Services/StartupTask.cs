using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace Fanner.App.Services;

/// <summary>
/// Registers Fanner to start at logon, as a scheduled task.
/// </summary>
/// <remarks>
/// Not the Run registry key, which is the obvious choice and the wrong one: an entry
/// there launches without elevation, so Fanner would come up at every logon showing
/// "administrator rights needed" and controlling nothing. A scheduled task with
/// <c>HighestAvailable</c> starts elevated and raises no UAC prompt, which is how
/// every other fan utility solves this.
/// </remarks>
internal static class StartupTask
{
    public const string TaskName = "Fanner";

    /// <summary>Tells the app it was started by the logon task, so it opens hidden.</summary>
    public const string MinimisedSwitch = "--minimised";

    public static bool IsRegistered() =>
        Run("/query", "/tn", TaskName).ExitCode == 0;

    /// <summary>Creates or replaces the task. Returns null on success, else the reason.</summary>
    public static string? Register()
    {
        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
        {
            return "Could not determine where Fanner is running from.";
        }

        var xmlPath = Path.Combine(Path.GetTempPath(), $"fanner-startup-{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks insists on UTF-16 for /xml and rejects UTF-8 files outright.
            File.WriteAllText(xmlPath, BuildXml(executable), new UnicodeEncoding(false, true));

            var result = Run("/create", "/tn", TaskName, "/xml", xmlPath, "/f");

            return result.ExitCode == 0 ? null : Describe(result);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch
            {
                // Temp file in the user's temp directory; not worth reporting.
            }
        }
    }

    /// <summary>Removes the task. Returns null on success, else the reason.</summary>
    public static string? Unregister()
    {
        if (!IsRegistered())
        {
            return null;
        }

        var result = Run("/delete", "/tn", TaskName, "/f");

        return result.ExitCode == 0 ? null : Describe(result);
    }

    private static string BuildXml(string executable)
    {
        var user = WindowsIdentity.GetCurrent().Name;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Fanner at logon so fan curves are applied without signing in to a window.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <!-- A desktop's fans matter on battery too, and a laptop's matter
                     more; none of the power-saving defaults make sense here. -->
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <!-- It runs for the whole session; a time limit would kill it. -->
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(executable)}</Command>
                  <Arguments>{MinimisedSwitch}</Arguments>
                  <WorkingDirectory>{Escape(Path.GetDirectoryName(executable) ?? string.Empty)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Describe(ProcessResult result) =>
        string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();

    private static ProcessResult Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, "Could not start schtasks.exe.");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);

            return new ProcessResult(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string Output, string Error);
}
