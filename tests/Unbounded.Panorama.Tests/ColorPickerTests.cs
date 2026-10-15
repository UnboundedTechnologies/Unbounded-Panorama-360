using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Unbounded.Panorama.App.Controls;
using Xunit;

namespace Unbounded.Panorama.Tests;

public class ColorPickerTests
{
    /// <summary>Runs on an STA thread with the app's resources loaded, as the real picker needs.</summary>
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                    new Unbounded.Panorama.App.App().InitializeComponent();
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t))
                return t;
            if (Find(child, match) is { } hit)
                return hit;
        }
        return null;
    }

    [Fact]
    public void Quick_colours_include_black_and_white()
    {
        Assert.Contains("#000000", ColorPicker.QuickColours);
        Assert.Contains("#FFFFFF", ColorPicker.QuickColours);
    }

    [Fact]
    public void Clicking_the_black_quick_colour_sets_black()
    {
        OnSta(() =>
        {
            var picker = new ColorPicker { Hex = "#00E5FF" };
            var popup = (Border)((Popup)picker.FindName("Pop")).Child;
            popup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            popup.Arrange(new Rect(popup.DesiredSize));
            popup.UpdateLayout();

            var black = Find<Button>(popup, b => b.Tag as string == "#000000");
            Assert.NotNull(black);
            black!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("#000000", picker.Hex);
            Assert.Equal(Colors.Black, picker.CurrentColor);

            // Optional visual check: set UPN360_RENDER to a folder to get a PNG of the picker.
            if (Environment.GetEnvironmentVariable("UPN360_RENDER") is { Length: > 0 } dir)
            {
                popup.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(popup.ActualWidth), (int)Math.Ceiling(popup.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(popup);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(dir, "color-picker.png"));
                encoder.Save(file);
            }
        });
    }

    [Fact]
    public void Area_picker_loads_and_its_keys_choose_the_place()
    {
        OnSta(() =>
        {
            foreach (var (key, expected) in new[] { (System.Windows.Input.Key.W, Layout.ScreenArea.Whole), (System.Windows.Input.Key.T, Layout.ScreenArea.TopFace), (System.Windows.Input.Key.F, Layout.ScreenArea.FrontFace) })
            {
                var window = new Unbounded.Panorama.App.AreaPickerWindow("clip.mp4");
                var content = (FrameworkElement)window.Content;
                window.Content = null; // render the content on its own (the window is never shown)
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                content.Arrange(new Rect(content.DesiredSize));
                content.UpdateLayout();
                if (key == System.Windows.Input.Key.W && Environment.GetEnvironmentVariable("UPN360_RENDER") is { Length: > 0 } dir)
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(dir, "area-picker.png"));
                    encoder.Save(file);
                }
                // The key handler records the choice (DialogResult itself needs a shown dialog).
                var handler = typeof(Unbounded.Panorama.App.AreaPickerWindow).GetMethod("OnKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                try
                {
                    handler.Invoke(window, [window, new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero), 0, key)]);
                }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
                {
                    // Setting DialogResult on a window that was never shown as a dialog throws after Result is set.
                }
                Assert.Equal(expected, window.Result);
                window.Close();
            }
        });
    }

    [Fact]
    public void Compact_hides_the_hex_box()
    {
        OnSta(() =>
        {
            var picker = new ColorPicker { Compact = true };
            Assert.Equal(Visibility.Collapsed, ((TextBox)picker.FindName("HexBox")).Visibility);
        });
    }
}
