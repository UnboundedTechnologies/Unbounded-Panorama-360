using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Unbounded.Panorama.App.Services;

namespace Unbounded.Panorama.App.ViewModels;

/// <summary>A card in "My media".</summary>
public sealed partial class LibraryItemViewModel(LibraryEntry entry) : ObservableObject
{
    public LibraryEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public bool IsYouTube => Entry.Kind == LibraryKind.YouTube;
    public bool Exists => File.Exists(Entry.Path);
    public string Badge => IsYouTube ? "YOUTUBE" : Path.GetExtension(Entry.Path).TrimStart('.').ToUpperInvariant();

    public string Subtitle => IsYouTube
        ? $"{Entry.LengthSeconds:0} s clip · {Entry.AddedAt:d MMM HH:mm}"
        : Exists ? $"{Path.GetFileName(Entry.Path)} · {Entry.AddedAt:d MMM HH:mm}" : "File missing";

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
    [ObservableProperty] public partial bool IsCurrent { get; set; }

    /// <summary>Videos and GIFs move on their card; pictures stay still.</summary>
    public bool IsVideo => !Media.MediaPreparer.IsImage(Entry.Path);

    /// <summary>The card's looping preview, once made.</summary>
    [ObservableProperty] public partial Uri? Clip { get; set; }

    /// <summary>Played length, e.g. "0:24" ("VIDEO" until known).</summary>
    [ObservableProperty] public partial string DurationText { get; set; } = "VIDEO";
}

/// <summary>A card in "On the cooler".</summary>
public sealed partial class DeviceMediaViewModel(string name, long size, bool isPreset, bool readOnly, bool inUse) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsPreset { get; } = isPreset;
    public bool ReadOnly { get; } = readOnly;
    public bool InUse { get; } = inUse;
    public bool CanDelete => !IsPreset && !ReadOnly && !InUse;
    public bool IsOurs => Name.StartsWith(Engine.DisplayEngine.RemotePrefix, StringComparison.Ordinal);
    public string SizeText => size >= 1048576 ? $"{size / 1048576.0:F1} MB" : $"{size / 1024.0:F0} KB";

    public string Title => IsPreset ? $"TRYX preset {PresetNumber}" : IsOurs ? "Unbounded upload" : "KANALI upload";
    /// <summary>"In use" means referenced by the cooler's configuration (playing, split, power-on or standby).</summary>
    public string Badge => InUse ? "IN USE" : IsPreset ? "PRESET" : "UPLOAD";

    private string PresetNumber
    {
        get
        {
            var digits = new string(Name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            return digits.Length > 0 ? digits : Name.Split('.')[0];
        }
    }

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
}
