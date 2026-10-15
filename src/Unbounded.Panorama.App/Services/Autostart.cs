using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Unbounded.Panorama.App.Services;

/// <summary>
/// Two Task Scheduler tasks run the app with highest privileges, so the administrator rights the
/// CPU sensors need never cost a UAC prompt: one starts it at logon (in the tray), the other is
/// started on demand by the app's shortcuts (see <see cref="Program"/>).
/// </summary>
public static class Autostart
{
    private const string TaskName = "Unbounded Panorama 360";
    public const string LauncherTaskName = "Unbounded Panorama 360 Launcher";

    /// <summary>Where setup.exe installs the app (updates replace it in place).</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unbounded Panorama 360");

    public static string InstalledExe => Path.Combine(InstallDir, "UnboundedPanorama360.exe");

    /// <summary>What the tasks should start: the installed app when present, otherwise this executable.</summary>
    public static string TargetExe => File.Exists(InstalledExe)
        ? InstalledExe
        : Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the app path.");

    public static bool IsEnabled() => Run(out _, "/Query", "/TN", TaskName) == 0;

    public static void Enable() => Register(TaskName, "--tray", atLogon: true);

    public static void Disable()
    {
        var code = Run(out var output, "/Delete", "/TN", TaskName, "/F");
        if (code != 0)
            throw new InvalidOperationException($"schtasks failed with exit code {code}: {output.Trim()}");
    }

    /// <summary>The on-demand task the shortcuts use to open the app without a UAC prompt.</summary>
    public static void RegisterLauncher() => Register(LauncherTaskName, "", atLogon: false);

    /// <summary>Starts the app through the launcher task. Works without administrator rights.</summary>
    public static bool TryStartThroughLauncher() => Run(out _, "/Run", "/TN", LauncherTaskName) == 0;

    /// <summary>
    /// Brings the installed app's tasks up to date: points them at <see cref="TargetExe"/> and replaces
    /// tasks created with schtasks defaults (stopped after 72 hours, below-normal priority, no start
    /// on battery). Returns true when something was changed.
    /// </summary>
    public static bool EnsureCurrent()
    {
        // Only the installed app fixes the tasks; a development build must never re-point them at itself.
        if (!File.Exists(InstalledExe))
            return false;
        var changed = false;
        if (!IsCurrent(LauncherTaskName))
        {
            RegisterLauncher();
            changed = true;
        }
        if (IsEnabled() && !IsCurrent(TaskName))
        {
            Enable();
            changed = true;
        }
        return changed;
    }

    private static bool IsCurrent(string name)
    {
        if (Run(out var xml, "/Query", "/TN", name, "/XML") != 0)
            return false;
        try
        {
            var doc = XDocument.Parse(xml.Trim());
            string? Value(string element) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == element)?.Value.Trim();
            return string.Equals(Value("Command")?.Trim('"'), TargetExe, StringComparison.OrdinalIgnoreCase)
                   && Value("ExecutionTimeLimit") == "PT0S";
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Registers a task from XML: schtasks /Create switches cannot set the time limit, priority or
    /// battery behaviour, and its defaults stop the app after 72 hours.
    /// </summary>
    private static void Register(string name, string arguments, bool atLogon)
    {
        var user = WindowsIdentity.GetCurrent();
        var sid = user.User?.Value ?? throw new InvalidOperationException("Cannot determine the current user.");
        string X(string s) => SecurityElement.Escape(s);
        var trigger = atLogon ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{X(user.Name)}</UserId></LogonTrigger>" : "";
        // Shortcuts may be clicked while the app runs: a parallel copy then shows the running window and exits.
        var instances = atLogon ? "IgnoreNew" : "Parallel";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Unbounded Panorama 360</Description></RegistrationInfo>
              <Triggers>{trigger}</Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{X(sid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>{instances}</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{X(TargetExe)}</Command>
                  {(arguments.Length > 0 ? $"<Arguments>{X(arguments)}</Arguments>" : "")}
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"upn360-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var code = Run(out var output, "/Create", "/TN", name, "/XML", file, "/F");
            if (code != 0)
                throw new InvalidOperationException($"schtasks failed with exit code {code}: {output.Trim()}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static int Run(out string output, params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected schtasks output (including /XML) is 8-bit console text, not UTF-16 (checked).
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
