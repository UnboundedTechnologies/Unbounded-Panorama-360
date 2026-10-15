using System.Diagnostics;

namespace Unbounded.Panorama.App.Services;

/// <summary>
/// Starts the app at logon through a Task Scheduler task with highest privileges, so the
/// administrator rights the CPU sensors need do not cost a UAC prompt at every login.
/// </summary>
public static class Autostart
{
    private const string TaskName = "Unbounded Panorama 360";

    public static bool IsEnabled() => Run("/Query", "/TN", TaskName) == 0;

    public static void Enable()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the app path.");
        var code = Run("/Create", "/TN", TaskName, "/TR", $"\"{exe}\" --tray", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        if (code != 0)
            throw new InvalidOperationException($"schtasks failed with exit code {code}.");
    }

    public static void Disable()
    {
        var code = Run("/Delete", "/TN", TaskName, "/F");
        if (code != 0)
            throw new InvalidOperationException($"schtasks failed with exit code {code}.");
    }

    private static int Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
