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
        if (!first)
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

        DispatcherUnhandledException += OnUnhandled;

        var storage = new AppStorage();
        _sensors = new SensorHub();
        _sensors.Start();
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
        if (!startHidden)
            ShowWindow();
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
            await _engine.DisposeAsync();
        _sensors?.Dispose();
        _showSignal?.Set();
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Unbounded Panorama 360", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
