using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Unbounded.Panorama.App.Services;

namespace Unbounded.Panorama.App;

/// <summary>
/// Entry point. The app needs administrator rights (CPU sensors are read through a kernel driver)
/// but its manifest does not demand them: opened normally, it starts the elevated copy through the
/// launcher task, so there is no UAC prompt. Only when that task does not exist (a development
/// build, or setup not run) does it ask Windows for elevation.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--register-tasks", StringComparer.OrdinalIgnoreCase))
            return RegisterTasks(args);

        if (!IsElevated())
            return Relaunch(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Relaunch(string[] args)
    {
        var self = Environment.ProcessPath!;
        var installed = string.Equals(self, Autostart.InstalledExe, StringComparison.OrdinalIgnoreCase);
        if (args.Length == 0 && installed && Autostart.TryStartThroughLauncher())
            return 0;

        try
        {
            var psi = new ProcessStartInfo(self) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            Process.Start(psi);
            return 0;
        }
        catch (Win32Exception)
        {
            // The UAC prompt was declined.
            return 1;
        }
    }

    /// <summary>
    /// Used by setup.exe (elevated): "--register-tasks autostart" registers the launcher and the
    /// logon task, "--register-tasks" alone registers the launcher and removes the logon task.
    /// </summary>
    private static int RegisterTasks(string[] args)
    {
        if (!IsElevated())
            return 2;
        try
        {
            Autostart.RegisterLauncher();
            if (args.Contains("autostart", StringComparer.OrdinalIgnoreCase))
                Autostart.Enable();
            else if (Autostart.IsEnabled())
                Autostart.Disable();
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
