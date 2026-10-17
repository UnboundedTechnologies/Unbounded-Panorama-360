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
    public double Duration { get; init; }
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
    private string DownloadDir => Path.Combine(_store.CacheDir, "downloads");

    public IReadOnlyList<MediaItem> Items { get { lock (_gate) return [.. _items.OrderByDescending(i => i.Added)]; } }
    public MediaItem? Find(string? id) { lock (_gate) return _items.FirstOrDefault(i => i.Id == id); }
    public string PathOf(MediaItem item) => Path.Combine(_store.LibraryDir, item.File);
    public string ThumbOf(MediaItem item) => Path.Combine(ThumbDir, item.Id + ".jpg");

    public static bool IsAllowed(string fileName) => Allowed.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Stores an uploaded file, measures it and makes its thumbnail.</summary>
    public async Task<MediaItem> AddAsync(string tempFile, string originalName, string? source, CancellationToken token)
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
            var duration = image ? 0 : await MediaPreparer.ProbeDurationAsync(target, token);
            var item = new MediaItem
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(originalName),
                File = file,
                IsImage = image,
                Width = w,
                Height = h,
                Duration = duration,
                Size = new FileInfo(target).Length,
                Source = source,
            };
            await MediaPreparer.RenderThumbnailAsync(target, image ? 0 : Math.Min(1, duration / 2), ThumbOf(item), token);
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

    /// <summary>Downloads a section of a YouTube video (best quality, like the Windows app) into the library.</summary>
    public async Task<MediaItem> ImportYouTubeAsync(string url, double start, double length, IProgress<ProgressInfo> progress, CancellationToken token)
    {
        var importer = new YouTubeImporter(DownloadDir);
        progress.Report(new ProgressInfo("Reading the video…"));
        var video = await importer.ProbeAsync(url, token);
        var clip = await importer.DownloadAsync(video, start, length, progress, token);
        progress.Report(new ProgressInfo("Adding it to the library…", 1));
        var name = string.Create(CultureInfo.InvariantCulture, $"{video.Title} ({YouTubeImporter.FormatTime(clip.OffsetSeconds)}){Path.GetExtension(clip.FilePath)}");
        return await AddAsync(clip.FilePath, Sanitize(name), url, token);
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

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

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
