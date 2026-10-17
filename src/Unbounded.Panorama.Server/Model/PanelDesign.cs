using System.Text.Json;
using System.Text.Json.Serialization;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.Server.Model;

/// <summary>What the screen shows: a background, text widgets and a drawing, in screen pixels.</summary>
public sealed class PanelDesign
{
    public PanelBackground Background { get; set; } = new();
    public List<Widget> Widgets { get; set; } = [];

    /// <summary>A free drawing over everything (a transparent PNG the size of the screen).</summary>
    public bool HasDrawing { get; set; }

    /// <summary>Bumped on every save, so the web page can tell its copy is current.</summary>
    public long Revision { get; set; }

    public PanelDesign Clone() => JsonSerializer.Deserialize<PanelDesign>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
}

public sealed class PanelBackground
{
    /// <summary>A library item, or null for a plain colour.</summary>
    public string? MediaId { get; set; }

    /// <summary>Behind the media (letterbox bars) or the whole background when there is none.</summary>
    public string Color { get; set; } = "#000000";

    /// <summary>Top-left corner and width of the media on the screen, in screen pixels. Null: cover the screen.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }

    /// <summary>Part of a video to loop, seconds.</summary>
    public double StartSeconds { get; set; }
    public double? DurationSeconds { get; set; }
}

/// <summary>Settings that apply to the screen whatever the design.</summary>
public sealed class PanelSettings
{
    public int Brightness { get; set; } = 100;

    /// <summary>Clockwise rotation of the picture on the panel: 0, 90, 180, 270.</summary>
    public int Rotation { get; set; }

    public int FramesPerSecond { get; set; } = 24;
    public bool Fahrenheit { get; set; }

    /// <summary>The screen shows nothing (black) while this is false.</summary>
    public bool On { get; set; } = true;

    /// <summary>Optional password for the web page (stored as a salted hash), null for none.</summary>
    public string? PasswordHash { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
