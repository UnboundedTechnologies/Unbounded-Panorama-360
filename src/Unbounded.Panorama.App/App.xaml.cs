using System.Windows;
using System.Windows.Threading;
using Unbounded.Panorama.App.Services;
using Unbounded.Panorama.App.ViewModels;
using Unbounded.Panorama.Engine;
using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Media;
using Unbounded.Panorama.Sensors;

namespace Unbounded.Panorama.App;

public partial class App : Application
{
    private const string InstanceName = "UnboundedPanorama360.SingleInstance";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _quitSignal;

    /// <summary>Signals the running copy to quit and waits (up to 20 s) to own the single-instance mutex.</summary>
    private bool TakeOverFromRunningInstance()
    {
        try
        {
            using var quit = EventWaitHandle.OpenExisting(InstanceName + ".Quit");
            quit.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // An older build without the quit signal: it has to be exited by hand.
        }
        try
        {
            return _instanceMutex!.WaitOne(TimeSpan.FromSeconds(20));
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous owner ended without releasing it; we own it now
        }
    }
    private SensorHub? _sensors;
    private DisplayEngine? _engine;
    private TrayIcon? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, InstanceName, out var first);

        // --quit (used by the installer and uninstaller): close the running copy cleanly, then exit.
        if (e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            if (!first)
                TakeOverFromRunningInstance();
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            Shutdown();
            return;
        }

        if (!first)
        {
            if (e.Args.Contains("--replace", StringComparer.OrdinalIgnoreCase))
            {
                // A newer build: ask the running copy to exit cleanly (closing the USB session), then take over.
                if (!TakeOverFromRunningInstance())
                {
                    MessageBox.Show("The running copy did not close. Exit it from the tray and start again.", "Unbounded Panorama 360",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }
            }
            else
            {
                // Already running: ask that instance to show its window.
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(InstanceName + ".Show");
                    signal.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }
                Shutdown();
                return;
            }
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                if (_exiting)
                    return;
                Dispatcher.BeginInvoke(ShowWindow);
            }
        }) { IsBackground = true };
        listener.Start();

        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Quit");
        var quitListener = new Thread(() =>
        {
            if (_quitSignal.WaitOne() && !_exiting)
                Dispatcher.BeginInvoke(ExitApp);
        }) { IsBackground = true };
        quitListener.Start();

        DispatcherUnhandledException += OnUnhandled;

        var storage = new AppStorage();
        // Sensors are opened after the window is shown (see below): it takes a second or more.
        _sensors = new SensorHub();
        var preparer = new MediaPreparer(storage.MediaCacheDir);
        _engine = new DisplayEngine(preparer, new WidgetText(_sensors));
        _viewModel = new MainViewModel(_sensors, _engine, preparer, storage);

        _tray = new TrayIcon(() => Dispatcher.BeginInvoke(ShowWindow), () => Dispatcher.BeginInvoke(ExitApp));
        _window = new MainWindow(_viewModel);
        _window.Closing += (_, args) =>
        {
            if (_exiting)
                return;
            // Closing the window keeps the cooler running from the tray.
            args.Cancel = true;
            _window.Hide();
            _tray.ShowHint("Still running in the tray. Your cooler keeps updating.");
        };

        var startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        if (!startHidden)
        {
            _window.ContentRendered += (_, _) => LogStartup($"window visible after {(DateTime.Now - started).TotalMilliseconds:0} ms");
            ShowWindow();
        }

        var sensors = _sensors;
        _ = sensors.StartInBackground().ContinueWith(t => Dispatcher.BeginInvoke(() =>
            LogStartup(t.IsFaulted
                ? $"sensors failed to start: {t.Exception?.GetBaseException().Message}"
                : $"sensors ready after {(DateTime.Now - started).TotalMilliseconds:0} ms")));
    }

    /// <summary>Startup timings go to the Activity log and to startup.log (last 50 lines).</summary>
    private void LogStartup(string line)
    {
        _viewModel?.Report(line);
        try
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnboundedPanorama360", "startup.log");
            var lines = System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path).ToList() : [];
            lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}");
            System.IO.File.WriteAllLines(path, lines.TakeLast(50));
        }
        catch (System.IO.IOException)
        {
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void ExitApp()
    {
        if (_exiting)
            return;
        _exiting = true;
        _viewModel?.Shutdown();
        _tray?.Dispose();
        _window?.Close();
        if (_engine is not null)
            await _engine.DisposeAsync(); // closes the USB session
        _sensors?.Dispose();
        _showSignal?.Set();
        // Only now let a replacing copy take over: the cooler is free.
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned on this thread (should not happen); the OS releases it at exit anyway.
        }
        Shutdown();
    }

    private bool _showingError;

    /// <summary>Writes the full exception to crash.log and shows one message (never a cascade).</summary>
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var log = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnboundedPanorama360", "crash.log");
        try
        {
            System.IO.File.AppendAllText(log, $"==== {DateTime.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}");
        }
        catch
        {
        }
        if (_showingError)
            return;
        _showingError = true;
        try
        {
            var inner = e.Exception.GetBaseException();
            MessageBox.Show($"{inner.Message}\n\nDetails were saved to:\n{log}", "Unbounded Panorama 360", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
