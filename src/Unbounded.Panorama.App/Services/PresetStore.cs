using System.IO;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.App.Services;

public sealed record PresetItem(string Name, bool IsBuiltIn)
{
    public string Display => IsBuiltIn ? $"{Name}  ·  template" : Name;
}

/// <summary>User presets as JSON files in %AppData%\UnboundedPanorama360\presets, plus built-in templates.</summary>
public sealed class PresetStore
{
    private readonly string _dir;

    public PresetStore(string roamingDir)
    {
        _dir = Directory.CreateDirectory(Path.Combine(roamingDir, "presets")).FullName;
    }

    public IReadOnlyList<PresetItem> List()
    {
        var user = Directory.EnumerateFiles(_dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new PresetItem(n, false));
        var builtIn = BuiltInPresets.All.Select(p => new PresetItem(p.Name, true));
        return [.. user, .. builtIn];
    }

    public ScreenLayout Load(PresetItem item)
    {
        if (item.IsBuiltIn)
            return BuiltInPresets.All.First(p => p.Name == item.Name).Create();
        return ScreenLayout.FromJson(File.ReadAllText(PathOf(item.Name)));
    }

    public void Save(string name, ScreenLayout layout)
    {
        var path = PathOf(name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, layout.ToJson());
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string name) => File.Delete(PathOf(name));

    public bool Exists(string name) => File.Exists(PathOf(name));

    /// <summary>Keeps names file-system safe.</summary>
    public static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    private string PathOf(string name) => Path.Combine(_dir, Clean(name) + ".json");
}
