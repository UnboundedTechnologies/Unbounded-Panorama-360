using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Unbounded.Panorama.Layout;
using TextAlign = Unbounded.Panorama.Layout.TextAlign;

namespace Unbounded.Panorama.App.ViewModels;

public sealed partial class WidgetViewModel : ObservableObject
{
    public static readonly FontFamily Roboto = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Roboto");

    public Guid Id { get; private init; } = Guid.NewGuid();

    /// <summary>Raised when anything that changes the cooler output changes.</summary>
    public event Action<WidgetViewModel>? Changed;

    [ObservableProperty] public partial WidgetContent Content { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "Text";
    [ObservableProperty] public partial string? SensorId { get; set; }
    [ObservableProperty] public partial int Decimals { get; set; }
    [ObservableProperty] public partial bool ShowUnit { get; set; } = true;
    [ObservableProperty] public partial string Prefix { get; set; } = "";
    [ObservableProperty] public partial string Suffix { get; set; } = "";
    [ObservableProperty] public partial string Format { get; set; } = "HH:mm";
    [ObservableProperty] public partial int X { get; set; }
    [ObservableProperty] public partial int Y { get; set; }
    [ObservableProperty] public partial int Width { get; set; } = 600;
    [ObservableProperty] public partial FontWeightName Weight { get; set; } = FontWeightName.Regular;
    [ObservableProperty] public partial int Size { get; set; } = 80;
    [ObservableProperty] public partial string Color { get; set; } = "#FFFFFF";
    [ObservableProperty] public partial TextAlign Align { get; set; } = TextAlign.Left;
    [ObservableProperty] public partial bool Badge { get; set; }
    [ObservableProperty] public partial string BadgeFrom { get; set; } = "#A92F2C";
    [ObservableProperty] public partial string BadgeTo { get; set; } = "#CB6236";

    /// <summary>What the widget currently shows (refreshed every second).</summary>
    [ObservableProperty] public partial string PreviewText { get; set; } = "";

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool Hidden { get; set; }
    [ObservableProperty] public partial bool Locked { get; set; }

    public Box Bounds => new(X, Y, Width, Height);
    public double Opacity => Hidden ? 0.25 : 1;

    public int Height => (int)Math.Ceiling(Size * 1.3);
    public bool IsTextContent => Content == WidgetContent.Text;
    public bool IsSensorContent => Content == WidgetContent.Sensor;
    public bool IsTimeContent => Content is WidgetContent.Clock or WidgetContent.Date;
    public bool UsesAffixes => Content != WidgetContent.Text;

    public FontWeight FontWeightValue => Weight switch
    {
        FontWeightName.Light => FontWeights.Light,
        FontWeightName.Medium => FontWeights.Medium,
        FontWeightName.Bold => FontWeights.Bold,
        _ => FontWeights.Regular,
    };

    public HorizontalAlignment HorizontalAlign => Align switch
    {
        TextAlign.Center => HorizontalAlignment.Center,
        TextAlign.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Left,
    };

    public Brush ColorBrush => BrushOf(Color);

    public Brush BadgeBrush => Badge
        ? new LinearGradientBrush(ColorOf(BadgeFrom), ColorOf(BadgeTo), 0)
        : Brushes.Transparent;

    public CornerRadius BadgeCorner => new(Badge ? Size * 0.3 : 0);

    public string Title => Content switch
    {
        WidgetContent.Text => string.IsNullOrWhiteSpace(Text) ? "Text" : Text,
        WidgetContent.Sensor => "Sensor · " + (string.IsNullOrEmpty(PreviewText) ? "?" : PreviewText),
        WidgetContent.Clock => "Clock",
        WidgetContent.Date => "Date",
        WidgetContent.CpuName => "CPU name",
        WidgetContent.GpuName => "GPU name",
        _ => Content.ToString(),
    };

    public string Kind => Content switch
    {
        WidgetContent.Text => "T",
        WidgetContent.Sensor => "°",
        WidgetContent.Clock => "◷",
        WidgetContent.Date => "▦",
        _ => "◆",
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Content):
                OnPropertyChanged(nameof(IsTextContent));
                OnPropertyChanged(nameof(IsSensorContent));
                OnPropertyChanged(nameof(IsTimeContent));
                OnPropertyChanged(nameof(UsesAffixes));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Kind));
                break;
            case nameof(Size):
                OnPropertyChanged(nameof(Height));
                OnPropertyChanged(nameof(BadgeCorner));
                break;
            case nameof(Weight):
                OnPropertyChanged(nameof(FontWeightValue));
                break;
            case nameof(Align):
                OnPropertyChanged(nameof(HorizontalAlign));
                break;
            case nameof(Color):
                OnPropertyChanged(nameof(ColorBrush));
                break;
            case nameof(Badge) or nameof(BadgeFrom) or nameof(BadgeTo):
                OnPropertyChanged(nameof(BadgeBrush));
                OnPropertyChanged(nameof(BadgeCorner));
                break;
            case nameof(Text) or nameof(PreviewText):
                OnPropertyChanged(nameof(Title));
                break;
            case nameof(Hidden):
                OnPropertyChanged(nameof(Opacity));
                break;
        }
        if (e.PropertyName is not (nameof(PreviewText) or nameof(IsSelected) or nameof(Title) or nameof(Opacity) or nameof(Bounds) or nameof(Kind)
            or nameof(IsTextContent) or nameof(IsSensorContent) or nameof(IsTimeContent) or nameof(UsesAffixes)
            or nameof(Height) or nameof(BadgeCorner) or nameof(FontWeightValue) or nameof(HorizontalAlign)
            or nameof(ColorBrush) or nameof(BadgeBrush)))
            Changed?.Invoke(this);
    }

    public static Color ColorOf(string? hex)
    {
        var rgb = LayoutCompiler.ParseColor(hex);
        return System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static Brush BrushOf(string? hex)
    {
        var brush = new SolidColorBrush(ColorOf(hex));
        brush.Freeze();
        return brush;
    }

    public Widget ToModel() => new()
    {
        Id = Id,
        Content = Content,
        Text = Text,
        SensorId = SensorId,
        Decimals = Decimals,
        ShowUnit = ShowUnit,
        Prefix = Prefix,
        Suffix = Suffix,
        Format = Format,
        X = X,
        Y = Y,
        Width = Width,
        Weight = Weight,
        Size = Size,
        Color = Color,
        Align = Align,
        Badge = Badge,
        BadgeFrom = BadgeFrom,
        BadgeTo = BadgeTo,
        Hidden = Hidden,
        Locked = Locked,
    };

    public static WidgetViewModel FromModel(Widget w) => new()
    {
        Id = w.Id,
        Content = w.Content,
        Text = w.Text,
        SensorId = w.SensorId,
        Decimals = w.Decimals,
        ShowUnit = w.ShowUnit,
        Prefix = w.Prefix,
        Suffix = w.Suffix,
        Format = w.Format,
        X = w.X,
        Y = w.Y,
        Width = w.Width,
        Weight = w.Weight,
        Size = w.Size,
        Color = w.Color,
        Align = w.Align,
        Badge = w.Badge,
        BadgeFrom = w.BadgeFrom,
        BadgeTo = w.BadgeTo,
        Hidden = w.Hidden,
        Locked = w.Locked,
    };

    public WidgetViewModel Duplicate()
    {
        var model = ToModel();
        model.Id = Guid.NewGuid();
        model.Locked = false;
        model.X += 40;
        model.Y += 40;
        return FromModel(model);
    }
}
