using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Unbounded.Panorama.Media;

namespace Unbounded.Panorama.App.Services;

public enum LibraryKind { File, YouTube }

public sealed class LibraryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LibraryKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public double StartSeconds { get; set; }
    public double? LengthSeconds { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>Everything the user has used as a background, newest first (library.json + thumbnails).</summary>
public sealed class MediaLibrary
{
    private const int Limit = 200;
    private readonly string _file;
    private readonly string _thumbDir;
    private List<LibraryEntry> _entries;

    public MediaLibrary(string roamingDir, string localDir)
    {
        _file = System.IO.Path.Combine(roamingDir, "library.json");
        _thumbDir = Directory.CreateDirectory(System.IO.Path.Combine(localDir, "thumbs")).FullName;
        _entries = Read();
    }

    public IReadOnlyList<LibraryEntry> Entries => _entries;

    /// <summary>Adds (or moves to the top) an entry for this file and section.</summary>
    public LibraryEntry Remember(LibraryKind kind, string path, string? title, double start = 0, double? length = null)
    {
        var existing = _entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)
                                                    && Math.Abs(e.StartSeconds - start) < 0.01 && e.LengthSeconds == length);
        if (existing is not null)
            _entries.Remove(existing);
        var entry = existing ?? new LibraryEntry { Kind = kind, Path = path, StartSeconds = start, LengthSeconds = length };
        entry.Title = string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title;
        entry.AddedAt = DateTime.Now;
        _entries.Insert(0, entry);
        if (_entries.Count > Limit)
            _entries.RemoveRange(Limit, _entries.Count - Limit);
        Write();
        return entry;
    }

    /// <summary>Removes the entry; YouTube downloads (owned by the app) are deleted from disk too.</summary>
    public void Forget(LibraryEntry entry, string downloadDir)
    {
        _entries.RemoveAll(e => e.Id == entry.Id);
        Write();
        var full = System.IO.Path.GetFullPath(entry.Path);
        var stillUsed = _entries.Any(e => string.Equals(System.IO.Path.GetFullPath(e.Path), full, StringComparison.OrdinalIgnoreCase));
        if (entry.Kind == LibraryKind.YouTube && !stillUsed && full.StartsWith(System.IO.Path.GetFullPath(downloadDir), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(full); } catch { }
        }
    }

    /// <summary>Thumbnail JPEG for an entry, generated once.</summary>
    public async Task<string?> ThumbnailAsync(LibraryEntry entry, CancellationToken cancellationToken)
    {
        if (!File.Exists(entry.Path))
            return null;
        var info = new FileInfo(entry.Path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{entry.StartSeconds}")), 0, 8);
        var jpg = System.IO.Path.Combine(_thumbDir, key + ".jpg");
        if (!File.Exists(jpg))
            await MediaPreparer.RenderThumbnailAsync(entry.Path, entry.StartSeconds + 0.5, jpg, cancellationToken).ConfigureAwait(false);
        return File.Exists(jpg) ? jpg : null;
    }

    private List<LibraryEntry> Read()
    {
        try
        {
            if (File.Exists(_file))
                return JsonSerializer.Deserialize<List<LibraryEntry>>(File.ReadAllText(_file)) ?? [];
        }
        catch (Exception)
        {
        }
        return [];
    }

    private void Write()
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, overwrite: true);
    }
}
