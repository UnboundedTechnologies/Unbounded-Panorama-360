using System.Text.Json;
using Unbounded.Panorama.Server.Model;

namespace Unbounded.Panorama.Server.Storage;

/// <summary>
/// Everything the server keeps, under one folder (default /var/lib/unbounded-panorama):
/// design.json, settings.json, drawing.png, lighting.json, the media library and its cache.
/// </summary>
public sealed class DataStore
{
    private readonly object _gate = new();
    private PanelDesign _design;
    private PanelSettings _settings;

    public DataStore(string root)
    {
        Root = Directory.CreateDirectory(root).FullName;
        Directory.CreateDirectory(LibraryDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(ProfileDir);
        _design = Read<PanelDesign>(DesignFile) ?? DefaultDesign();
        _settings = Read<PanelSettings>(SettingsFile) ?? new PanelSettings();
    }

    public string Root { get; }
    public string DesignFile => Path.Combine(Root, "design.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DrawingFile => Path.Combine(Root, "drawing.png");
    public string LightingFile => Path.Combine(Root, "lighting.json");
    public string ProfileDir => Path.Combine(Root, "lighting-profiles");
    public string LibraryDir => Path.Combine(Root, "library");
    public string CacheDir => Path.Combine(Root, "cache");

    public event Action? DesignChanged;
    public event Action? SettingsChanged;

    public PanelDesign Design { get { lock (_gate) return _design; } }
    public PanelSettings Settings { get { lock (_gate) return _settings; } }

    public PanelDesign SaveDesign(PanelDesign design)
    {
        lock (_gate)
        {
            design.Revision = _design.Revision + 1;
            _design = design;
            WriteAtomic(DesignFile, JsonSerializer.Serialize(design, Json.Options));
        }
        DesignChanged?.Invoke();
        return design;
    }

    public void SaveSettings(PanelSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            WriteAtomic(SettingsFile, JsonSerializer.Serialize(settings, Json.Options));
        }
        SettingsChanged?.Invoke();
    }

    /// <summary>A clock and the CPU temperature on black, so a fresh install shows something useful.</summary>
    private static PanelDesign DefaultDesign() => new()
    {
        Widgets =
        [
            new() { Content = Layout.WidgetContent.Clock, Format = "HH:mm", X = 0, Y = 70, Width = 320, Size = 96, Weight = Layout.FontWeightName.Light, Align = Layout.TextAlign.Center },
            new() { Content = Layout.WidgetContent.Date, Format = "dddd d MMMM", X = 0, Y = 190, Width = 320, Size = 22, Color = "#BFC7D5", Align = Layout.TextAlign.Center },
        ],
    };

    private static T? Read<T>(string file) where T : class
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void WriteAtomic(string file, string text)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, file, overwrite: true);
    }
}
