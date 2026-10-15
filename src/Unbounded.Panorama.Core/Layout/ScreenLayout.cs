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

    /// <summary>
    /// Play a file already stored on the cooler (e.g. a TRYX preset) instead of uploading
    /// <see cref="SourcePath"/>. Device files cannot carry a drawing.
    /// </summary>
    public string? DeviceFile { get; set; }

    /// <summary>Displayed size of the source (square pixels), measured when the file is chosen.</summary>
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }

    /// <summary>
    /// Exact placement of the media on the canvas, in cooler pixels: top-left corner and width
    /// (height follows the source's shape). Null means "not placed yet" (older layouts use Fit/Crop).
    /// </summary>
    public double? MediaX { get; set; }
    public double? MediaY { get; set; }
    public double? MediaWidth { get; set; }

    /// <summary>The part of the screen the media is shown in; outside it the screen stays black.</summary>
    public ScreenArea Area { get; set; } = ScreenArea.Whole;

    [JsonIgnore]
    public bool HasPlacement => MediaX is not null && MediaY is not null && MediaWidth is > 0 && SourceWidth > 0 && SourceHeight > 0;

    [JsonIgnore]
    public double MediaHeight => SourceWidth > 0 ? (MediaWidth ?? 0) * SourceHeight / SourceWidth : 0;

    /// <summary>Transparent PNG (canvas size) drawn over the background. Set at apply time, not saved.</summary>
    [JsonIgnore]
    public string? OverlayPath { get; set; }
}

public enum ScreenArea
{
    /// <summary>The whole screen.</summary>
    Whole,

    /// <summary>Portrait only: the flat face above the bend.</summary>
    TopFace,

    /// <summary>Portrait only: the curved face below the bend.</summary>
    FrontFace,
}

/// <summary>Placement geometry shared by the editor preview and the ffmpeg conversion.</summary>
public static class Placement
{
    /// <summary>
    /// Where the screen bends from the flat top onto the curved front, in portrait canvas pixels.
    /// Measured on the cooler: text at y≈1100 sat on the bend.
    /// </summary>
    public const int BendY = 1100;

    public static Box AreaBox(ScreenArea area, Orientation orientation)
    {
        var (w, h) = orientation == Orientation.Portrait ? (ScreenLayout.PanelHeight, ScreenLayout.PanelWidth) : (ScreenLayout.PanelWidth, ScreenLayout.PanelHeight);
        if (orientation != Orientation.Portrait)
            return new Box(0, 0, w, h);
        return area switch
        {
            ScreenArea.TopFace => new Box(0, 0, w, BendY),
            ScreenArea.FrontFace => new Box(0, BendY, w, h - BendY),
            _ => new Box(0, 0, w, h),
        };
    }

    /// <summary>Media rectangle that covers the area (cropping the overflow), centred.</summary>
    public static Box Fill(Box area, int sourceW, int sourceH)
    {
        var scale = Math.Max(area.W / sourceW, area.H / sourceH);
        return Centered(area, sourceW * scale, sourceH * scale);
    }

    /// <summary>Media rectangle that fits entirely inside the area, centred.</summary>
    public static Box Fit(Box area, int sourceW, int sourceH)
    {
        var scale = Math.Min(area.W / sourceW, area.H / sourceH);
        return Centered(area, sourceW * scale, sourceH * scale);
    }

    public static Box Centered(Box area, double w, double h) =>
        new(area.X + (area.W - w) / 2, area.Y + (area.H - h) / 2, w, h);

    /// <summary>The part of the media that is actually visible (inside the area), or null.</summary>
    public static Box? Visible(Box media, Box area)
    {
        var x1 = Math.Max(media.X, area.X);
        var y1 = Math.Max(media.Y, area.Y);
        var x2 = Math.Min(media.Right, area.Right);
        var y2 = Math.Min(media.Bottom, area.Bottom);
        return x2 - x1 >= 1 && y2 - y1 >= 1 ? new Box(x1, y1, x2 - x1, y2 - y1) : null;
    }
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

    /// <summary>Free drawing (Windows ink, ISF, base64) in canvas coordinates; baked into the background.</summary>
    public string? Drawing { get; set; }

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
