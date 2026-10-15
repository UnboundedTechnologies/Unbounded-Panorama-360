using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unbounded.Panorama.Layout;

public enum Orientation
{
    /// <summary>1080 × 2240 canvas, upright on a portrait-mounted cooler (ui_rotation 270).</summary>
    Portrait,

    /// <summary>2240 × 1080 canvas, the panel's native orientation (ui_rotation 0).</summary>
    Landscape,
}

public enum WidgetContent
{
    Text,
    Sensor,
    Clock,
    Date,
    CpuName,
    GpuName,
}

public enum FontWeightName
{
    Light,
    Regular,
    Medium,
    Bold,
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

public enum BackgroundFit
{
    /// <summary>Cover the whole screen, cropping the overflow.</summary>
    Fill,

    /// <summary>Show the whole source, letterboxed in black.</summary>
    Fit,
}

/// <summary>One element on the screen. Rendered by the cooler's firmware as a text group.</summary>
public sealed class Widget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public WidgetContent Content { get; set; } = WidgetContent.Text;

    public string Text { get; set; } = "Text";
    public string? SensorId { get; set; }
    public int Decimals { get; set; }
    public bool ShowUnit { get; set; } = true;
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    public string Format { get; set; } = "HH:mm";

    public int X { get; set; } = 100;
    public int Y { get; set; } = 100;
    public int Width { get; set; } = 600;

    public FontWeightName Weight { get; set; } = FontWeightName.Regular;
    public int Size { get; set; } = 80;
    public string Color { get; set; } = "#FFFFFF";
    public TextAlign Align { get; set; } = TextAlign.Left;

    /// <summary>Plain text has no background. A badge is a gradient pill behind the text.</summary>
    public bool Badge { get; set; }
    public string BadgeFrom { get; set; } = "#A92F2C";
    public string BadgeTo { get; set; } = "#CB6236";

    /// <summary>Hidden widgets stay in the design but are not sent to the cooler.</summary>
    public bool Hidden { get; set; }

    /// <summary>Locked widgets cannot be moved or resized on the canvas.</summary>
    public bool Locked { get; set; }

    /// <summary>Height of the firmware group box; text sits inside it.</summary>
    [JsonIgnore]
    public int Height => (int)Math.Ceiling(Size * 1.3);
}

public sealed class BackgroundMedia
{
    public string? SourcePath { get; set; }
    public BackgroundFit Fit { get; set; } = BackgroundFit.Fill;

    /// <summary>Horizontal/vertical position of the crop window when Fill crops, 0..1 (0.5 = centered).</summary>
    public double CropX { get; set; } = 0.5;
    public double CropY { get; set; } = 0.5;

    /// <summary>Start offset into a video, seconds.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Length to keep, seconds; null keeps the rest of the source (60 s for still images).</summary>
    public double? DurationSeconds { get; set; }
}

public sealed class ScreenLayout
{
    public const int PanelWidth = 2240;
    public const int PanelHeight = 1080;

    public Orientation Orientation { get; set; } = Orientation.Portrait;
    public int Brightness { get; set; } = 80;
    public bool Fahrenheit { get; set; }
    public BackgroundMedia Background { get; set; } = new();
    public List<Widget> Widgets { get; set; } = [];

    [JsonIgnore]
    public int CanvasWidth => Orientation == Orientation.Portrait ? PanelHeight : PanelWidth;

    [JsonIgnore]
    public int CanvasHeight => Orientation == Orientation.Portrait ? PanelWidth : PanelHeight;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ScreenLayout FromJson(string json) =>
        JsonSerializer.Deserialize<ScreenLayout>(json, JsonOptions) ?? new ScreenLayout();

    public ScreenLayout Clone() => FromJson(ToJson());

    /// <summary>A starting layout for a portrait-mounted cooler.</summary>
    public static ScreenLayout CreateDefault() => new()
    {
        Widgets =
        [
            new Widget { Content = WidgetContent.Clock, Format = "HH:mm", X = 90, Y = 120, Width = 900, Size = 220, Weight = FontWeightName.Light, Align = TextAlign.Center },
            new Widget { Content = WidgetContent.Date, Format = "dddd d MMMM", X = 90, Y = 420, Width = 900, Size = 56, Align = TextAlign.Center, Color = "#BFC7D5" },
            new Widget { Content = WidgetContent.Sensor, SensorId = "role:cpu-temp", Prefix = "CPU  ", X = 90, Y = 640, Width = 900, Size = 110, Weight = FontWeightName.Medium, Align = TextAlign.Center, Color = "#FF7A59" },
            new Widget { Content = WidgetContent.Sensor, SensorId = "role:gpu-temp", Prefix = "GPU  ", X = 90, Y = 820, Width = 900, Size = 110, Weight = FontWeightName.Medium, Align = TextAlign.Center, Color = "#76B900" },
        ],
    };
}
