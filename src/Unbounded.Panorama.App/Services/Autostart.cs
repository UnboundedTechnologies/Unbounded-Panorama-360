using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace Unbounded.Panorama.App.Services;

/// <summary>
/// Starts the app at logon through a Task Scheduler task with highest privileges, so the
/// administrator rights the CPU sensors need do not cost a UAC prompt at every login.
/// </summary>
public static class Autostart
{
    private const string TaskName = "Unbounded Panorama 360";

    /// <summary>Where setup.exe installs the app (updates replace it in place).</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unbounded Panorama 360");

    public static string InstalledExe => Path.Combine(InstallDir, "UnboundedPanorama360.exe");

    /// <summary>What the task should start: the installed app when present, otherwise this executable.</summary>
    public static string TargetExe => File.Exists(InstalledExe)
        ? InstalledExe
        : Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the app path.");

    public static bool IsEnabled() => Run(out _, "/Query", "/TN", TaskName) == 0;

    public static void Enable()
    {
        var code = Run(out var output, "/Create", "/TN", TaskName, "/TR", $"\"{TargetExe}\" --tray", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        if (code != 0)
            throw new InvalidOperationException($"schtasks failed with exit code {code}: {output.Trim()}");
    }

    public static void Disable()
    {
        var code = Run(out var output, "/Delete", "/TN", TaskName, "/F");
        if (code != 0)
            throw new InvalidOperationException($"schtasks failed with exit code {code}: {output.Trim()}");
    }

    /// <summary>
    /// If autostart is on but the task starts a different executable (an older build folder),
    /// point it at <see cref="TargetExe"/>. Returns true when it was updated.
    /// </summary>
    public static bool EnsureCurrent()
    {
        // Only the installed app fixes the task; a development build must never re-point it at itself.
        if (!File.Exists(InstalledExe) || !IsEnabled() || Run(out var xml, "/Query", "/TN", TaskName, "/XML") != 0)
            return false;
        string? command;
        try
        {
            var doc = XDocument.Parse(xml.Trim());
            command = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim().Trim('"');
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
        if (string.Equals(command, TargetExe, StringComparison.OrdinalIgnoreCase))
            return false;
        Enable();
        return true;
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
