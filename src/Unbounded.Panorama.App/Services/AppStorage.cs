using System.IO;
using System.Text.Json;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.App.Services;

/// <summary>Files under %AppData% and %LocalAppData%\UnboundedPanorama360.</summary>
public sealed class AppStorage
{
    private sealed class Settings
    {
        public bool AutoApply { get; set; }
    }

    private readonly string _layoutFile;
    private readonly string _settingsFile;
    private Settings _settings;

    public string RoamingDir { get; }
    public string LocalDir { get; }
    public string MediaCacheDir { get; }
    public string PreviewDir { get; }
    public string DownloadDir { get; }

    public AppStorage()
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnboundedPanorama360");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnboundedPanorama360");
        RoamingDir = Directory.CreateDirectory(roaming).FullName;
        LocalDir = Directory.CreateDirectory(local).FullName;
        MediaCacheDir = Directory.CreateDirectory(Path.Combine(local, "media")).FullName;
        PreviewDir = Directory.CreateDirectory(Path.Combine(local, "previews")).FullName;
        DownloadDir = Directory.CreateDirectory(Path.Combine(local, "downloads")).FullName;
        _layoutFile = Path.Combine(roaming, "layout.json");
        _settingsFile = Path.Combine(roaming, "settings.json");
        _settings = ReadSettings();
    }

    /// <summary>Re-apply the saved design whenever the cooler connects (set after the first successful Apply).</summary>
    public bool AutoApply
    {
        get => _settings.AutoApply;
        set
        {
            if (_settings.AutoApply == value)
                return;
            _settings.AutoApply = value;
            WriteAtomic(_settingsFile, JsonSerializer.Serialize(_settings));
        }
    }

    public ScreenLayout LoadLayout()
    {
        try
        {
            if (File.Exists(_layoutFile))
                return ScreenLayout.FromJson(File.ReadAllText(_layoutFile));
        }
        catch (Exception)
        {
            // A damaged file must not stop the app; keep it for inspection and start fresh.
            File.Copy(_layoutFile, _layoutFile + ".broken", overwrite: true);
        }
        return ScreenLayout.CreateDefault();
    }

    public void SaveLayout(ScreenLayout layout) => WriteAtomic(_layoutFile, layout.ToJson());

    private Settings ReadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsFile)) ?? new Settings();
        }
        catch (Exception)
        {
        }
        return new Settings();
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
