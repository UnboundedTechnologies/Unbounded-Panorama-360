using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Unbounded.Panorama.App.ViewModels;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.App;

public partial class MainWindow : Window
{
    /// <summary>Snap distance in screen pixels (converted to cooler pixels at the current zoom).</summary>
    private const double SnapScreenPixels = 8;

    private static readonly Brush GuideBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0x9A)));
    private static readonly Brush SpacingBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x1C)));

    private readonly MainViewModel _vm;

    // Move drag
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragStart;
    private List<(WidgetViewModel W, int X, int Y)> _dragItems = [];
    private Box _dragBox;
    private List<Box> _dragOthers = [];

    // Marquee
    private bool _marquee;
    private Point _marqueeStart;

    // Resize
    private double _resizeWidth;
    private List<Box> _resizeOthers = [];

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            Services.TrayIcon.DrawIcon().Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, _) =>
        {
            if (Log.Items.Count > 0)
                Log.ScrollIntoView(Log.Items[^1]);
        };
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += OnWheel;
        Services.WindowChrome.UseDarkTitleBar(this);
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Screen pixels per cooler pixel at the current Viewbox zoom.</summary>
    private double Zoom
    {
        get
        {
            try
            {
                var m = Screen.TransformToAncestor(this).Transform(new Point(100, 0)) - Screen.TransformToAncestor(this).Transform(new Point(0, 0));
                return Math.Max(0.01, m.X / 100);
            }
            catch (InvalidOperationException)
            {
                return 1;
            }
        }
    }

    private double SnapThreshold => SnapScreenPixels / Zoom;

    private bool SnapActive => _vm.SnapEnabled && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

    private static bool Additive => Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    // ================================================================== selection + move

    private void OnWidgetMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not WidgetViewModel w)
            return;
        e.Handled = true;
        Screen.Focus();
        _vm.Select(w, Additive);
        if (!w.IsSelected)
            return; // Ctrl-click just removed it from the selection

        _dragItems = _vm.Selection.Where(s => !s.Locked).Select(s => (s, s.X, s.Y)).ToList();
        if (_dragItems.Count == 0)
            return;
        _dragging = true;
        _dragMoved = false;
        _dragStart = e.GetPosition(Screen);
        _dragBox = Box.Union(_dragItems.Select(d => d.W.Bounds));
        var moving = _dragItems.Select(d => d.W).ToHashSet();
        _dragOthers = _vm.Widgets.Where(o => !moving.Contains(o) && !o.Hidden).Select(o => o.Bounds).ToList();
        Screen.CaptureMouse();
    }

    private void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Empty screen: start a selection box.
        Screen.Focus();
        if (!Additive)
            _vm.ClearSelectionCommand.Execute(null);
        _marquee = true;
        _marqueeStart = e.GetPosition(Screen);
        Marquee.Width = Marquee.Height = 0;
        Canvas.SetLeft(Marquee, _marqueeStart.X);
        Canvas.SetTop(Marquee, _marqueeStart.Y);
        Marquee.StrokeThickness = 2 / Zoom;
        Marquee.Visibility = Visibility.Visible;
        Screen.CaptureMouse();
    }

    private void OnScreenMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Screen);
        if (_dragging)
        {
            var dx = p.X - _dragStart.X;
            var dy = p.Y - _dragStart.Y;
            if (!_dragMoved && Math.Abs(dx) * Zoom < 2 && Math.Abs(dy) * Zoom < 2)
                return; // ignore jitter on a plain click
            _dragMoved = true;

            var proposed = _dragBox with { X = _dragBox.X + dx, Y = _dragBox.Y + dy };
            IReadOnlyList<Guide> guides = [];
            if (SnapActive)
            {
                var snap = SnapEngine.SnapMove(proposed, _dragOthers, _vm.CanvasWidth, _vm.CanvasHeight, SnapThreshold);
                proposed = proposed with { X = snap.X, Y = snap.Y };
                guides = snap.Guides;
            }
            var x = Math.Clamp(Math.Round(proposed.X), 0, _vm.CanvasWidth - _dragBox.W);
            var y = Math.Clamp(Math.Round(proposed.Y), 0, _vm.CanvasHeight - _dragBox.H);
            var ox = (int)(x - _dragBox.X);
            var oy = (int)(y - _dragBox.Y);
            foreach (var (w, sx, sy) in _dragItems)
            {
                w.X = sx + ox;
                w.Y = sy + oy;
            }
            DrawGuides(guides);
            _vm.DragInfo = $"X {x:0}  Y {y:0}  W {_dragBox.W:0}" + (_dragItems.Count > 1 ? $"  ({_dragItems.Count} widgets)" : "");
        }
        else if (_marquee)
        {
            var x = Math.Min(p.X, _marqueeStart.X);
            var y = Math.Min(p.Y, _marqueeStart.Y);
            Canvas.SetLeft(Marquee, x);
            Canvas.SetTop(Marquee, y);
            Marquee.Width = Math.Abs(p.X - _marqueeStart.X);
            Marquee.Height = Math.Abs(p.Y - _marqueeStart.Y);
        }
    }

    private void OnScreenMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            if (_dragMoved)
                _vm.CommitNow();
        }
        else if (_marquee)
        {
            _marquee = false;
            Marquee.Visibility = Visibility.Collapsed;
            var area = new Box(Canvas.GetLeft(Marquee), Canvas.GetTop(Marquee), Marquee.Width, Marquee.Height);
            if (area.W * Zoom > 4 || area.H * Zoom > 4)
            {
                var hit = _vm.Widgets.Where(w => !w.Locked && Intersects(area, w.Bounds));
                _vm.SelectOnly(hit, Additive);
            }
        }
        Screen.ReleaseMouseCapture();
        GuideLayer.Children.Clear();
        _vm.DragInfo = "";
    }

    private static bool Intersects(Box a, Box b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    // ================================================================== resize

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WidgetViewModel w)
            return;
        _resizeWidth = w.Width;
        _resizeOthers = _vm.Widgets.Where(o => o != w && !o.Hidden).Select(o => o.Bounds).ToList();
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WidgetViewModel w || w.Locked)
            return;
        _resizeWidth += e.HorizontalChange;
        var width = Math.Clamp(_resizeWidth, 40, _vm.CanvasWidth - w.X);
        IReadOnlyList<Guide> guides = [];
        if (SnapActive)
        {
            (width, guides) = SnapEngine.SnapWidth(w.Bounds with { W = width }, _resizeOthers, _vm.CanvasWidth, _vm.CanvasHeight, SnapThreshold);
            width = Math.Clamp(width, 40, _vm.CanvasWidth - w.X);
        }
        w.Width = (int)Math.Round(width);
        DrawGuides(guides);
        _vm.DragInfo = $"W {w.Width}  (X {w.X} → {w.X + w.Width})";
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        GuideLayer.Children.Clear();
        _vm.DragInfo = "";
        _vm.CommitNow();
    }

    // ================================================================== guides

    private void DrawGuides(IReadOnlyList<Guide> guides)
    {
        GuideLayer.Children.Clear();
        var px = 1 / Zoom; // one screen pixel in cooler pixels
        foreach (var g in guides)
        {
            var brush = g.IsSpacing ? SpacingBrush : GuideBrush;
            var line = new Line
            {
                Stroke = brush,
                StrokeThickness = 1.5 * px,
                X1 = g.Vertical ? g.Position : g.From,
                X2 = g.Vertical ? g.Position : g.To,
                Y1 = g.Vertical ? g.From : g.Position,
                Y2 = g.Vertical ? g.To : g.Position,
            };
            if (!g.IsSpacing)
                line.StrokeDashArray = [4, 3];
            GuideLayer.Children.Add(line);

            if (g.Label is { } text)
            {
                var label = new Border
                {
                    Background = brush,
                    CornerRadius = new CornerRadius(4 * px),
                    Padding = new Thickness(5 * px, 1 * px, 5 * px, 1 * px),
                    Child = new TextBlock { Text = text, FontSize = 11 * px, Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold },
                };
                var mid = (g.From + g.To) / 2;
                Canvas.SetLeft(label, g.Vertical ? g.Position + 4 * px : mid);
                Canvas.SetTop(label, g.Vertical ? mid : g.Position + 4 * px);
                GuideLayer.Children.Add(label);
            }
        }
    }

    // ================================================================== list, keys, wheel

    private void OnListRowClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is WidgetViewModel w)
            _vm.Select(w, Additive);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var typing = Keyboard.FocusedElement is TextBox;

        if (ctrl && e.Key == Key.Enter)
        {
            _vm.ApplyCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (typing)
            return; // let text boxes keep their own keys (including their own undo)

        switch (e.Key)
        {
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                _vm.RedoCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                _vm.UndoCommand.Execute(null);
                break;
            case Key.V when ctrl:
                if (!TryPaste())
                    return;
                break;
            case Key.D when ctrl:
                _vm.DuplicateWidgetCommand.Execute(null);
                break;
            case Key.A when ctrl:
                _vm.SelectAllCommand.Execute(null);
                break;
            case Key.Escape:
                _vm.ClearSelectionCommand.Execute(null);
                break;
            case Key.Delete:
                _vm.DeleteWidgetCommand.Execute(null);
                break;
            case Key.Left: _vm.NudgeSelection(shift ? -10 : -1, 0); break;
            case Key.Right: _vm.NudgeSelection(shift ? 10 : 1, 0); break;
            case Key.Up: _vm.NudgeSelection(0, shift ? -10 : -1); break;
            case Key.Down: _vm.NudgeSelection(0, shift ? 10 : 1); break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _vm.SelectedWidget is null)
            return;
        foreach (var w in _vm.Selection)
            w.Size = Math.Clamp(w.Size + (e.Delta > 0 ? 4 : -4), 16, 400);
        e.Handled = true;
    }

    private void OnFormatPreset(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedWidget is { } w && sender is Button { Content: string format })
            w.Format = format;
    }

    private void OnSensorTypeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string type } rb)
            rb.IsChecked = type == _vm.SensorTypeFilter;
    }

    private void OnSensorTypeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string type })
            _vm.SensorTypeFilter = type;
    }

    // ================================================================== YouTube, file drop, paste

    private void OnImportYouTube(object sender, RoutedEventArgs e) => OpenYouTube(null);

    private void OpenYouTube(string? url)
    {
        var dialog = new YouTubeWindow(_vm.YouTube, url) { Owner = this, Icon = Icon };
        Services.WindowChrome.UseDarkTitleBar(dialog);
        if (dialog.ShowDialog() == true && dialog.Result is { } clip)
            _vm.UseImportedClip(clip, dialog.VideoTitle);
    }

    /// <summary>Ctrl+V outside a text box: a copied YouTube link opens the importer, a copied file becomes the background.</summary>
    private bool TryPaste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(MainViewModel.IsMediaFile);
                if (file is not null)
                {
                    _vm.SetBackground(file);
                    return true;
                }
            }
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (text.Contains("youtu", StringComparison.OrdinalIgnoreCase))
                {
                    OpenYouTube(text);
                    return true;
                }
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy in another app.
        }
        return false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The app runs elevated (CPU sensors), and Windows blocks OLE drag-and-drop from Explorer into
        // elevated windows. Classic WM_DROPFILES still works once it is allowed through the filter.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        foreach (var message in new uint[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
            ChangeWindowMessageFilterEx(hwnd, message, MSGFLT_ALLOW, IntPtr.Zero);
        DragAcceptFiles(hwnd, true);
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DROPFILES)
            return IntPtr.Zero;
        try
        {
            var count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(wParam, i, null, 0);
                var buffer = new System.Text.StringBuilder((int)length + 1);
                DragQueryFile(wParam, i, buffer, (uint)buffer.Capacity);
                var path = buffer.ToString();
                if (MainViewModel.IsMediaFile(path))
                {
                    _vm.SetBackground(path);
                    break;
                }
                _vm.Status = $"{System.IO.Path.GetFileName(path)} is not an image, GIF or video.";
            }
        }
        finally
        {
            DragFinish(wParam);
        }
        handled = true;
        return IntPtr.Zero;
    }

    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, System.Text.StringBuilder? file, uint size);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);
}
