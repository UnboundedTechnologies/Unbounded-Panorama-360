using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Unbounded.Panorama.App.ViewModels;
using Unbounded.Panorama.App.Views;
using Xunit;

namespace Unbounded.Panorama.Tests;

public class LightingViewTests
{
    private sealed class Host(LightingViewModel lighting)
    {
        public LightingViewModel Lighting { get; } = lighting;
    }

    /// <summary>
    /// The ARGB page loads with the app's styles. With OpenRGB running it also lists the real devices
    /// (read only: nothing is applied, settings live in a temporary folder) and, with UPN360_RENDER set,
    /// draws the page to a PNG for a visual check.
    /// </summary>
    [Fact]
    public void Lighting_page_loads_and_lists_devices_without_changing_them()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "upn360-light-" + Guid.NewGuid().ToString("N"));
            try
            {
                // As in the app: code after 'await' continues on this (UI) thread.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                TestTheme.Ensure();
                var vm = new LightingViewModel(Directory.CreateDirectory(dir).FullName, Dispatcher.CurrentDispatcher, _ => { });
                var view = new LightingView { DataContext = new Host(vm), Width = 1500, Height = 1300 };

                // Read the devices (no apply), running this thread's dispatcher until it is done.
                var refresh = vm.RefreshCommand.ExecuteAsync(null);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!refresh.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(20))
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => Thread.Sleep(20));
                Assert.True(refresh.IsCompleted, "refresh did not finish");

                // Nothing was configured, so nothing may have been sent or saved.
                Assert.False(File.Exists(Path.Combine(dir, "lighting.json")));

                view.Measure(new Size(1500, 1300));
                view.Arrange(new Rect(0, 0, 1500, 1300));
                view.UpdateLayout();
                if (Environment.GetEnvironmentVariable("UPN360_RENDER") is { Length: > 0 } output)
                {
                    var bitmap = new RenderTargetBitmap(1500, 1300, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var dc = background.RenderOpen())
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(5, 7, 13)), null, new Rect(0, 0, 1500, 1300));
                    bitmap.Render(background);
                    bitmap.Render(view);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, "argb-page.png"));
                    encoder.Save(file);
                    File.WriteAllText(Path.Combine(output, "argb-status.txt"), $"{vm.StatusText}\nconnected={vm.Connected} devices={vm.Devices.Count} armoury={vm.ArmouryLightingRunning} openrgbMissing={vm.OpenRgbMissing}");
                }
                vm.Dispose();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
