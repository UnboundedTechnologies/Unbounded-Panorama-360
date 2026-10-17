using System.Globalization;
using System.Text.Json;
using Unbounded.Panorama.Media;
using Unbounded.Panorama.Server.Model;
using Unbounded.Panorama.Server.Storage;

namespace Unbounded.Panorama.Server.Library;

public sealed class MediaItem
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string File { get; init; }
    public bool IsImage { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Playable length in seconds (for a YouTube import: exactly the chosen section).</summary>
    public double Duration { get; init; }

    /// <summary>
    /// Where the item starts inside its file. A YouTube section is downloaded from the keyframe before the
    /// chosen start, so the file begins a little early; playback skips to here.
    /// </summary>
    public double ClipStart { get; init; }

    /// <summary>
    /// The first timestamp in the file (a YouTube section keeps the original times, e.g. 58.4 s). Some browsers
    /// show those absolute times, so the page needs it to play exactly the section.
    /// </summary>
    public double FileStart { get; init; }
    public long Size { get; init; }
    public string? Source { get; init; }
    public DateTime Added { get; init; } = DateTime.UtcNow;
}

/// <summary>Pictures and videos kept in the container (uploads and YouTube imports), with thumbnails.</summary>
public sealed class MediaLibrary
{
    private static readonly HashSet<string> Allowed = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v"];

    private readonly DataStore _store;
    private readonly object _gate = new();
    private List<MediaItem> _items;

    public MediaLibrary(DataStore store)
    {
        _store = store;
        Directory.CreateDirectory(ThumbDir);
        Directory.CreateDirectory(DownloadDir);
        try
        {
            _items = File.Exists(IndexFile) ? JsonSerializer.Deserialize<List<MediaItem>>(File.ReadAllText(IndexFile), Json.Options) ?? [] : [];
        }
        catch (JsonException)
        {
            _items = [];
        }
        _items.RemoveAll(i => !System.IO.File.Exists(PathOf(i)));
    }

    private string IndexFile => Path.Combine(_store.LibraryDir, "library.json");
    public string ThumbDir => Path.Combine(_store.CacheDir, "thumbs");
    public string DownloadDir => Path.Combine(_store.CacheDir, "downloads");

    public IReadOnlyList<MediaItem> Items { get { lock (_gate) return [.. _items.OrderByDescending(i => i.Added)]; } }
    public MediaItem? Find(string? id) { lock (_gate) return _items.FirstOrDefault(i => i.Id == id); }
    public string PathOf(MediaItem item) => Path.Combine(_store.LibraryDir, item.File);
    public string ThumbOf(MediaItem item) => Path.Combine(ThumbDir, item.Id + ".jpg");

    public static bool IsAllowed(string fileName) => Allowed.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Stores an uploaded file, measures it and makes its thumbnail.</summary>
    public async Task<MediaItem> AddAsync(string tempFile, string originalName, string? source, CancellationToken token, double clipStart = 0, double? clipLength = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var file = id + ext;
        var target = Path.Combine(_store.LibraryDir, file);
        System.IO.File.Move(tempFile, target);
        try
        {
            var image = MediaPreparer.IsImage(target);
            var (w, h) = await MediaPreparer.ProbeSizeAsync(target, token);
            var fileDuration = image ? 0 : await MediaPreparer.ProbeDurationAsync(target, token);
            var start = image ? 0 : Math.Clamp(clipStart, 0, Math.Max(0, fileDuration - 0.1));
            var duration = image ? 0 : Math.Min(clipLength ?? fileDuration, fileDuration - start);
            var item = new MediaItem
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(originalName),
                File = file,
                IsImage = image,
                Width = w,
                Height = h,
                Duration = duration,
                ClipStart = start,
                FileStart = image ? 0 : await ProbeStartAsync(target, token),
                Size = new FileInfo(target).Length,
                Source = source,
            };
            await MediaPreparer.RenderThumbnailAsync(target, image ? 0 : start + Math.Min(1, duration / 2), ThumbOf(item), token);
            lock (_gate)
            {
                _items.Add(item);
                Save();
            }
            return item;
        }
        catch
        {
            System.IO.File.Delete(target);
            throw;
        }
    }

    private static async Task<double> ProbeStartAsync(string file, CancellationToken token)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Tools.FfprobePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=start_time", "-of", "default=nw=1:nk=1", file })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync(token);
        await p.WaitForExitAsync(token);
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 0;
    }

    public bool Rename(string id, string name)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null || string.IsNullOrWhiteSpace(name))
                return false;
            item.Name = name.Trim();
            Save();
            return true;
        }
    }

    public bool Delete(string id)
    {
        MediaItem? item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return false;
            _items.Remove(item);
            Save();
        }
        TryDelete(PathOf(item));
        TryDelete(ThumbOf(item));
        return true;
    }

    private void Save() => DataStore.WriteAtomic(IndexFile, JsonSerializer.Serialize(_items, Json.Options));

    private static void TryDelete(string file)
    {
        try
        {
            System.IO.File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
