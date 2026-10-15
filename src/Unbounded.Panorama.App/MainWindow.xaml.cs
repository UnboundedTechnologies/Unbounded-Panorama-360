using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Unbounded.Panorama.App.ViewModels;

namespace Unbounded.Panorama.App;

public partial class MainWindow : Window
{
    private const int Snap = 2;

    private readonly MainViewModel _vm;
    private double _dragX;
    private double _dragY;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            Services.TrayIcon.DrawIcon().Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        // Keep the activity log scrolled to the newest line.
        ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, _) =>
        {
            if (Log.Items.Count > 0)
                Log.ScrollIntoView(Log.Items[^1]);
        };
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += OnWheel;
        Services.WindowChrome.UseDarkTitleBar(this);
    }

    private void OnImportYouTube(object sender, RoutedEventArgs e)
    {
        var dialog = new YouTubeWindow(_vm.YouTube) { Owner = this, Icon = Icon };
        Services.WindowChrome.UseDarkTitleBar(dialog);
        if (dialog.ShowDialog() == true && dialog.Result is { } clip)
            _vm.UseImportedClip(clip, dialog.VideoTitle);
    }

    private void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking empty screen area deselects.
        if (e.OriginalSource == Screen)
            _vm.SelectedWidget = null;
        Screen.Focus();
    }

    private void OnWidgetDragStarted(object sender, DragStartedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WidgetViewModel w)
            return;
        _vm.SelectedWidget = w;
        _dragX = w.X;
        _dragY = w.Y;
        Keyboard.Focus(Screen);
    }

    private void OnWidgetDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WidgetViewModel w)
            return;
        // Thumb deltas are in the canvas' own coordinates (cooler pixels), even inside the Viewbox.
        _dragX += e.HorizontalChange;
        _dragY += e.VerticalChange;
        _vm.MoveWidget(w, (int)Math.Round(_dragX / Snap) * Snap, (int)Math.Round(_dragY / Snap) * Snap);
    }

    private void OnWidgetResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WidgetViewModel w)
            return;
        w.Width = Math.Clamp((int)Math.Round(w.Width + e.HorizontalChange), 40, _vm.CanvasWidth - w.X);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox || _vm.SelectedWidget is not { } w)
            return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: _vm.MoveWidget(w, w.X - step, w.Y); break;
            case Key.Right: _vm.MoveWidget(w, w.X + step, w.Y); break;
            case Key.Up: _vm.MoveWidget(w, w.X, w.Y - step); break;
            case Key.Down: _vm.MoveWidget(w, w.X, w.Y + step); break;
            case Key.Delete: _vm.DeleteWidgetCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _vm.SelectedWidget is not { } w)
            return;
        w.Size = Math.Clamp(w.Size + (e.Delta > 0 ? 4 : -4), 16, 400);
        e.Handled = true;
    }

    private void OnFormatPreset(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedWidget is { } w && sender is Button { Content: string format })
            w.Format = format;
    }
}
