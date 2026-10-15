using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Unbounded.Panorama.App.ViewModels;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.App.Views;

/// <summary>The editor: canvas interaction (select, drag with smart guides, resize, marquee) and drawing.</summary>
public partial class StudioView : UserControl
{
    private const double SnapScreenPixels = 8;

    private static readonly Brush GuideBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x2B, 0xD6)));
    private static readonly Brush SpacingBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)));

    private MainViewModel? _vm;
    private Window? _window;

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

    // Drawing
    private bool _loadingInk;
    private Stroke? _shape;
    private Point _shapeStart;

    public StudioView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Ink.StrokeCollected += (_, _) => SaveInk();
        Ink.StrokeErased += (_, _) => SaveInk();
        Ink.PreviewMouseLeftButtonDown += OnInkDown;
        Ink.PreviewMouseMove += OnInkMove;
        Ink.PreviewMouseLeftButtonUp += OnInkUp;
        SelectMode.IsChecked = true;
        PenTool.IsChecked = true;
    }

    private MainViewModel Vm => _vm ??= (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.DrawingReplaced += LoadInk;
        Vm.PropertyChanged += OnVmChanged;
        LoadInk(Vm.Drawing);
        ApplyInkSettings();
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.PreviewKeyDown += OnKey;
            _window.PreviewMouseWheel += OnWheel;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.DrawingReplaced -= LoadInk;
            _vm.PropertyChanged -= OnVmChanged;
        }
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnKey;
            _window.PreviewMouseWheel -= OnWheel;
        }
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    private bool IsActivePage => Vm.CurrentPage == "Studio";

    /// <summary>Screen pixels per cooler pixel at the current Viewbox zoom.</summary>
    private double Zoom
    {
        get
        {
            try
            {
                var root = (Visual?)_window ?? this;
                var a = Screen.TransformToAncestor(root).Transform(new Point(0, 0));
                var b = Screen.TransformToAncestor(root).Transform(new Point(100, 0));
                return Math.Max(0.01, (b.X - a.X) / 100);
            }
            catch (InvalidOperationException)
            {
                return 1;
            }
        }
    }

    private double SnapThreshold => SnapScreenPixels / Zoom;
    private bool SnapActive => Vm.SnapEnabled && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
    private static bool Additive => Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    // ================================================================== modes

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode } && _vm is not null)
            Vm.EditorMode = mode;
    }

    private void OnBackToSelect(object sender, RoutedEventArgs e) => SelectMode.IsChecked = true;

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: DrawTool tool } && DataContext is MainViewModel vm)
            vm.DrawTool = tool;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.DrawTool):
            case nameof(MainViewModel.DrawColor):
            case nameof(MainViewModel.DrawSize):
            case nameof(MainViewModel.EditorMode):
                ApplyInkSettings();
                break;
        }
        if (e.PropertyName == nameof(MainViewModel.EditorMode) && Vm.EditorMode == "Select")
            SelectMode.IsChecked = true;
    }

    private DrawingAttributes CurrentAttributes(bool freehand) => new()
    {
        Color = WidgetViewModel.ColorOf(Vm.DrawColor),
        Width = Vm.DrawSize,
        Height = Vm.DrawSize,
        FitToCurve = freehand,
        IgnorePressure = true,
        StylusTip = StylusTip.Ellipse,
    };

    private void ApplyInkSettings()
    {
        Ink.DefaultDrawingAttributes = CurrentAttributes(freehand: true);
        Ink.EditingMode = Vm.DrawTool switch
        {
            DrawTool.Pen => InkCanvasEditingMode.Ink,
            DrawTool.Eraser => InkCanvasEditingMode.EraseByStroke,
            _ => InkCanvasEditingMode.None, // shapes are drawn by our own mouse handlers
        };
        Ink.Cursor = Vm.DrawTool is DrawTool.Line or DrawTool.Rectangle or DrawTool.Ellipse ? Cursors.Cross : null;
    }

    // ================================================================== ink persistence

    private void LoadInk(string? base64)
    {
        _loadingInk = true;
        try
        {
            Ink.Strokes = string.IsNullOrEmpty(base64)
                ? new StrokeCollection()
                : new StrokeCollection(new MemoryStream(Convert.FromBase64String(base64)));
        }
        catch (Exception)
        {
            Ink.Strokes = new StrokeCollection();
        }
        finally
        {
            _loadingInk = false;
        }
    }

    private void SaveInk()
    {
        if (_loadingInk)
            return;
        string? base64 = null;
        if (Ink.Strokes.Count > 0)
        {
            using var stream = new MemoryStream();
            Ink.Strokes.Save(stream, compress: true);
            base64 = Convert.ToBase64String(stream.ToArray());
        }
        Vm.SetDrawingFromCanvas(base64);
    }

    // ================================================================== shape tools

    private void OnInkDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm.DrawTool is not (DrawTool.Line or DrawTool.Rectangle or DrawTool.Ellipse))
            return;
        _shapeStart = e.GetPosition(Ink);
        _shape = new Stroke(ShapePoints(_shapeStart, _shapeStart), CurrentAttributes(freehand: false));
        Ink.Strokes.Add(_shape);
        Ink.CaptureMouse();
        e.Handled = true;
    }

    private void OnInkMove(object sender, MouseEventArgs e)
    {
        if (_shape is null)
            return;
        _shape.StylusPoints = ShapePoints(_shapeStart, e.GetPosition(Ink));
        e.Handled = true;
    }

    private void OnInkUp(object sender, MouseButtonEventArgs e)
    {
        if (_shape is null)
            return;
        var end = e.GetPosition(Ink);
        if ((end - _shapeStart).Length < 2)
            Ink.Strokes.Remove(_shape); // a click, not a shape
        else
            _shape.StylusPoints = ShapePoints(_shapeStart, end);
        _shape = null;
        Ink.ReleaseMouseCapture();
        SaveInk();
        e.Handled = true;
    }

    private StylusPointCollection ShapePoints(Point a, Point b)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var points = new StylusPointCollection();
        switch (Vm.DrawTool)
        {
            case DrawTool.Line:
                if (shift)
                {
                    // Snap to 45° steps.
                    var angle = Math.Round(Math.Atan2(b.Y - a.Y, b.X - a.X) / (Math.PI / 4)) * (Math.PI / 4);
                    var length = (b - a).Length;
                    b = new Point(a.X + Math.Cos(angle) * length, a.Y + Math.Sin(angle) * length);
                }
                points.Add(new StylusPoint(a.X, a.Y));
                points.Add(new StylusPoint(b.X, b.Y));
                break;
            case DrawTool.Rectangle:
            {
                var (x2, y2) = Square(a, b, shift);
                foreach (var (x, y) in new[] { (a.X, a.Y), (x2, a.Y), (x2, y2), (a.X, y2), (a.X, a.Y) })
                    points.Add(new StylusPoint(x, y));
                break;
            }
            case DrawTool.Ellipse:
            {
                var (x2, y2) = Square(a, b, shift);
                var cx = (a.X + x2) / 2;
                var cy = (a.Y + y2) / 2;
                var rx = Math.Abs(x2 - a.X) / 2;
                var ry = Math.Abs(y2 - a.Y) / 2;
                for (var i = 0; i <= 96; i++)
                {
                    var t = i * 2 * Math.PI / 96;
                    points.Add(new StylusPoint(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
                }
                break;
            }
        }
        return points;
    }

    private static (double X, double Y) Square(Point a, Point b, bool square)
    {
        if (!square)
            return (b.X, b.Y);
        var size = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return (a.X + Math.Sign(b.X - a.X) * size, a.Y + Math.Sign(b.Y - a.Y) * size);
    }

    // ================================================================== selection + move

    private void OnWidgetMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm.IsDrawMode || ((FrameworkElement)sender).Tag is not WidgetViewModel w)
            return;
        e.Handled = true;
        Screen.Focus();
        Vm.Select(w, Additive);
        if (!w.IsSelected)
            return;

        _dragItems = Vm.Selection.Where(s => !s.Locked).Select(s => (s, s.X, s.Y)).ToList();
        if (_dragItems.Count == 0)
            return;
        _dragging = true;
        _dragMoved = false;
        _dragStart = e.GetPosition(Screen);
        _dragBox = Box.Union(_dragItems.Select(d => d.W.Bounds));
        var moving = _dragItems.Select(d => d.W).ToHashSet();
        _dragOthers = Vm.Widgets.Where(o => !moving.Contains(o) && !o.Hidden).Select(o => o.Bounds).ToList();
        Screen.CaptureMouse();
    }

    private void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm.IsDrawMode)
            return;
        Screen.Focus();
        if (!Additive)
            Vm.ClearSelectionCommand.Execute(null);
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
                return;
            _dragMoved = true;

            var proposed = _dragBox with { X = _dragBox.X + dx, Y = _dragBox.Y + dy };
            IReadOnlyList<Guide> guides = [];
            if (SnapActive)
            {
                var snap = SnapEngine.SnapMove(proposed, _dragOthers, Vm.CanvasWidth, Vm.CanvasHeight, SnapThreshold);
                proposed = proposed with { X = snap.X, Y = snap.Y };
                guides = snap.Guides;
            }
            var x = Math.Clamp(Math.Round(proposed.X), 0, Vm.CanvasWidth - _dragBox.W);
            var y = Math.Clamp(Math.Round(proposed.Y), 0, Vm.CanvasHeight - _dragBox.H);
            var ox = (int)(x - _dragBox.X);
            var oy = (int)(y - _dragBox.Y);
            foreach (var (w, sx, sy) in _dragItems)
            {
                w.X = sx + ox;
                w.Y = sy + oy;
            }
            DrawGuides(guides);
            Vm.DragInfo = $"X {x:0}  Y {y:0}  W {_dragBox.W:0}" + (_dragItems.Count > 1 ? $"  ({_dragItems.Count} widgets)" : "");
        }
        else if (_marquee)
        {
            Canvas.SetLeft(Marquee, Math.Min(p.X, _marqueeStart.X));
            Canvas.SetTop(Marquee, Math.Min(p.Y, _marqueeStart.Y));
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
                Vm.CommitNow();
        }
        else if (_marquee)
        {
            _marquee = false;
            Marquee.Visibility = Visibility.Collapsed;
            var area = new Box(Canvas.GetLeft(Marquee), Canvas.GetTop(Marquee), Marquee.Width, Marquee.Height);
            if (area.W * Zoom > 4 || area.H * Zoom > 4)
                Vm.SelectOnly(Vm.Widgets.Where(w => !w.Locked && Intersects(area, w.Bounds)), Additive);
        }
        Screen.ReleaseMouseCapture();
        GuideLayer.Children.Clear();
        Vm.DragInfo = "";
    }

    private static bool Intersects(Box a, Box b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    // ================================================================== resize

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WidgetViewModel w)
            return;
        _resizeWidth = w.Width;
        _resizeOthers = Vm.Widgets.Where(o => o != w && !o.Hidden).Select(o => o.Bounds).ToList();
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WidgetViewModel w || w.Locked)
            return;
        _resizeWidth += e.HorizontalChange;
        var width = Math.Clamp(_resizeWidth, 40, Vm.CanvasWidth - w.X);
        IReadOnlyList<Guide> guides = [];
        if (SnapActive)
        {
            (width, guides) = SnapEngine.SnapWidth(w.Bounds with { W = width }, _resizeOthers, Vm.CanvasWidth, Vm.CanvasHeight, SnapThreshold);
            width = Math.Clamp(width, 40, Vm.CanvasWidth - w.X);
        }
        w.Width = (int)Math.Round(width);
        DrawGuides(guides);
        Vm.DragInfo = $"W {w.Width}  (X {w.X} → {w.X + w.Width})";
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        GuideLayer.Children.Clear();
        Vm.DragInfo = "";
        Vm.CommitNow();
    }

    // ================================================================== guides

    private void DrawGuides(IReadOnlyList<Guide> guides)
    {
        GuideLayer.Children.Clear();
        var px = 1 / Zoom;
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

    // ================================================================== list, keys, wheel, misc

    private void OnListRowClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is WidgetViewModel w)
        {
            if (Vm.IsDrawMode)
                SelectMode.IsChecked = true;
            Vm.Select(w, Additive);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!IsActivePage)
            return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (Keyboard.FocusedElement is TextBox)
            return; // text boxes keep their own keys

        switch (e.Key)
        {
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                Vm.RedoCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                Vm.UndoCommand.Execute(null);
                break;
            case Key.D when ctrl:
                Vm.DuplicateWidgetCommand.Execute(null);
                break;
            case Key.A when ctrl:
                Vm.SelectAllCommand.Execute(null);
                break;
            case Key.Escape:
                if (Vm.IsDrawMode)
                    SelectMode.IsChecked = true;
                else
                    Vm.ClearSelectionCommand.Execute(null);
                break;
            case Key.Delete:
                Vm.DeleteWidgetCommand.Execute(null);
                break;
            case Key.Left: Vm.NudgeSelection(shift ? -10 : -1, 0); break;
            case Key.Right: Vm.NudgeSelection(shift ? 10 : 1, 0); break;
            case Key.Up: Vm.NudgeSelection(0, shift ? -10 : -1); break;
            case Key.Down: Vm.NudgeSelection(0, shift ? 10 : 1); break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsActivePage || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        if (Vm.IsDrawMode)
        {
            Vm.DrawSize = Math.Clamp(Vm.DrawSize + (e.Delta > 0 ? 2 : -2), 2, 80);
            e.Handled = true;
            return;
        }
        if (Vm.SelectedWidget is null)
            return;
        foreach (var w in Vm.Selection)
            w.Size = Math.Clamp(w.Size + (e.Delta > 0 ? 4 : -4), 16, 400);
        e.Handled = true;
    }

    private void OnFormatPreset(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedWidget is { } w && sender is Button { Content: string format })
            w.Format = format;
    }

    private void OnSensorTypeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string type } rb)
            rb.IsChecked = type == Vm.SensorTypeFilter;
    }

    private void OnSensorTypeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string type })
            Vm.SensorTypeFilter = type;
    }

    private void OnImportYouTube(object sender, RoutedEventArgs e) => (_window as MainWindow)?.OpenYouTube(null);
}
