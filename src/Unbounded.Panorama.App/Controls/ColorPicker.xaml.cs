using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Unbounded.Panorama.App.Controls;

/// <summary>
/// Swatch + hex box; the swatch opens a picker with quick and recent colours, a
/// saturation/brightness square, a hue bar, and exact R/G/B and hex fields. Bound through
/// <see cref="Hex"/> ("#RRGGBB", two-way).
/// </summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColorPicker)d).OnHexChanged((string?)e.NewValue)));

    public static readonly DependencyProperty CurrentColorProperty = DependencyProperty.Register(
        nameof(CurrentColor), typeof(Color), typeof(ColorPicker), new PropertyMetadata(Colors.White));

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    /// <summary>The colour shown on the swatch.</summary>
    public Color CurrentColor
    {
        get => (Color)GetValue(CurrentColorProperty);
        private set => SetValue(CurrentColorProperty, value);
    }

    // HSV state is kept separately so hue survives at zero saturation/brightness.
    private double _h;
    private double _s;
    private double _v = 1;
    private bool _dragSv;
    private bool _dragHue;
    private bool _updating;

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(ColorPicker), new PropertyMetadata(false, (d, e) => ((ColorPicker)d).ApplyCompact()));

    /// <summary>Swatch only, no hex box (toolbars). The popup is the same.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>One-click colours in every picker; black is a real colour on the cooler's AMOLED.</summary>
    public static readonly IReadOnlyList<string> QuickColours =
    [
        "#FFFFFF", "#000000", "#BFC7D5", "#7C8799", "#3A4256",
        "#FF5A5F", "#FF7A59", "#FFC247", "#76B900", "#3DDC97",
        "#00E5FF", "#22D3EE", "#4F8CFF", "#8B5CF6", "#A78BFA", "#F472B6",
    ];

    /// <summary>Colours chosen recently in any picker (newest first), shared for this session.</summary>
    private static readonly System.Collections.ObjectModel.ObservableCollection<string> Recent = [];
    private const int RecentMax = 10;

    private string? _hexWhenOpened;

    public ColorPicker()
    {
        InitializeComponent();
        QuickList.ItemsSource = QuickColours;
        RecentList.ItemsSource = Recent;
        UpdateRecentVisibility();
        Recent.CollectionChanged += (_, _) => UpdateRecentVisibility();
        Pop.Opened += (_, _) =>
        {
            _hexWhenOpened = Hex;
            Dispatcher.BeginInvoke(PositionMarkers, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        // Remember the colour once the popup closes (not every step of a drag).
        Pop.Closed += (_, _) =>
        {
            if (!string.Equals(Hex, _hexWhenOpened, StringComparison.OrdinalIgnoreCase))
                Remember(Hex);
        };
        SvArea.SizeChanged += (_, _) => PositionMarkers();
        HueArea.SizeChanged += (_, _) => PositionMarkers();
        OnHexChanged(Hex);
    }

    private void ApplyCompact()
    {
        HexBox.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        Opener.Margin = Compact ? new Thickness(0) : new Thickness(0, 0, 8, 0);
    }

    private void UpdateRecentVisibility() =>
        RecentPanel.Visibility = Recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private static void Remember(string? hex)
    {
        if (!TryParse(hex, out var c))
            return;
        var normal = ToHex(c);
        var existing = Recent.IndexOf(normal);
        if (existing == 0)
            return;
        if (existing > 0)
            Recent.RemoveAt(existing);
        Recent.Insert(0, normal);
        while (Recent.Count > RecentMax)
            Recent.RemoveAt(Recent.Count - 1);
    }

    private void OnDotClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex } && TryParse(hex, out var c))
        {
            (_h, _s, _v) = ToHsv(c, _h);
            Commit(c);
        }
    }

    // ------------------------------------------------------------------ external value

    private void OnHexChanged(string? hex)
    {
        if (_updating || !TryParse(hex, out var c))
        {
            if (!_updating)
                HexBox.Text = hex ?? "";
            return;
        }
        (_h, _s, _v) = ToHsv(c, _h);
        Show(c);
    }

    // ------------------------------------------------------------------ square + hue

    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        _dragSv = true;
        SvArea.CaptureMouse();
        PickSv(e.GetPosition(SvArea));
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (_dragSv)
            PickSv(e.GetPosition(SvArea));
    }

    private void OnSvUp(object sender, MouseButtonEventArgs e)
    {
        _dragSv = false;
        SvArea.ReleaseMouseCapture();
    }

    private void PickSv(Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(1, SvArea.ActualWidth), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, SvArea.ActualHeight), 0, 1);
        Commit(FromHsv(_h, _s, _v));
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _dragHue = true;
        HueArea.CaptureMouse();
        PickHue(e.GetPosition(HueArea));
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (_dragHue)
            PickHue(e.GetPosition(HueArea));
    }

    private void OnHueUp(object sender, MouseButtonEventArgs e)
    {
        _dragHue = false;
        HueArea.ReleaseMouseCapture();
    }

    private void PickHue(Point p)
    {
        _h = Math.Clamp(p.X / Math.Max(1, HueArea.ActualWidth), 0, 1) * 360;
        if (_h >= 360)
            _h = 359.999;
        Commit(FromHsv(_h, _s, _v));
    }

    // ------------------------------------------------------------------ number / hex fields

    private void OnRgbKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnRgbCommit(sender, e);
    }

    private void OnRgbCommit(object sender, RoutedEventArgs e)
    {
        if (byte.TryParse(RBox.Text.Trim(), out var r) && byte.TryParse(GBox.Text.Trim(), out var g) && byte.TryParse(BBox.Text.Trim(), out var b))
        {
            var c = Color.FromRgb(r, g, b);
            (_h, _s, _v) = ToHsv(c, _h);
            Commit(c);
        }
        else
        {
            Show(CurrentColor); // invalid entry: put the current values back
        }
    }

    private void OnHexBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnHexBoxCommit(sender, e);
    }

    private void OnHexBoxCommit(object sender, RoutedEventArgs e)
    {
        var text = ((TextBox)sender).Text;
        if (TryParse(text, out var c))
        {
            (_h, _s, _v) = ToHsv(c, _h);
            Commit(c);
        }
        else
        {
            Show(CurrentColor);
        }
    }

    // ------------------------------------------------------------------ state

    private void Commit(Color c)
    {
        Show(c);
        _updating = true;
        try
        {
            Hex = ToHex(c);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Show(Color c)
    {
        CurrentColor = c;
        var hex = ToHex(c);
        if (!HexBox.IsKeyboardFocused)
            HexBox.Text = hex;
        if (!PopHexBox.IsKeyboardFocused)
            PopHexBox.Text = hex;
        if (!RBox.IsKeyboardFocused)
            RBox.Text = c.R.ToString(CultureInfo.InvariantCulture);
        if (!GBox.IsKeyboardFocused)
            GBox.Text = c.G.ToString(CultureInfo.InvariantCulture);
        if (!BBox.IsKeyboardFocused)
            BBox.Text = c.B.ToString(CultureInfo.InvariantCulture);
        HueFill.Color = FromHsv(_h, 1, 1);
        HsvText.Text = $"H {_h:0}°  S {_s * 100:0}%  V {_v * 100:0}%";
        PositionMarkers();
    }

    private void PositionMarkers()
    {
        Canvas.SetLeft(SvMarker, _s * SvArea.ActualWidth - SvMarker.Width / 2);
        Canvas.SetTop(SvMarker, (1 - _v) * SvArea.ActualHeight - SvMarker.Height / 2);
        Canvas.SetLeft(HueMarker, _h / 360 * HueArea.ActualWidth - HueMarker.Width / 2);
    }

    // ------------------------------------------------------------------ colour math

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.White;
        var s = (text ?? "").Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s.Select(ch => new string(ch, 2))); // #abc → #aabbcc
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>RGB → HSV (h in degrees). Keeps the previous hue for greys, where hue is undefined.</summary>
    public static (double H, double S, double V) ToHsv(Color c, double previousHue = 0)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double h;
        if (delta < 1e-9)
            h = previousHue;
        else if (max == r)
            h = 60 * (((g - b) / delta) % 6);
        else if (max == g)
            h = 60 * ((b - r) / delta + 2);
        else
            h = 60 * ((r - g) / delta + 4);
        if (h < 0)
            h += 360;
        var s = max <= 0 ? 0 : delta / max;
        return (h, s, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte To(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
        return Color.FromRgb(To(r + m), To(g + m), To(b + m));
    }
}
