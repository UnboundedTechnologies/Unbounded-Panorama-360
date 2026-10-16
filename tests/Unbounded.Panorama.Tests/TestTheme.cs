using System.Windows;

namespace Unbounded.Panorama.Tests;

/// <summary>
/// The app's look for WPF tests, without the app: a plain Application with Theme.xaml merged in.
/// Never create the real App class in tests: once the dispatcher runs, WPF calls its OnStartup, which
/// starts the real program (single-instance signals, the cooler connection, sensors).
/// </summary>
public static class TestTheme
{
    public static void Ensure()
    {
        if (Application.Current is not null)
            return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/UnboundedPanorama360;component/Theme.xaml", UriKind.Absolute),
        });
    }
}
