using System.Collections.Concurrent;
using System.Globalization;
using Unbounded.Panorama.Media;

namespace Unbounded.Panorama.Server.Library;

/// <summary>
/// YouTube for the web page: one lookup per link (cached), exact frames at any second, a light
/// preview of the chosen section to watch the loop, and the import in the best quality, keeping
/// exactly the chosen section (the download starts at a keyframe before it).
/// </summary>
public sealed class YouTubeService
{
    private readonly MediaLibrary _library;
    private readonly YouTubeImporter _importer;
    private readonly ConcurrentDictionary<string, (Task<YouTubeVideo> Video, DateTime At)> _videos = new();
    private readonly ConcurrentDictionary<string, string> _previews = new();

    public YouTubeService(MediaLibrary library)
    {
        _library = library;
        _importer = new YouTubeImporter(library.DownloadDir);
        Prune();
    }

    /// <summary>The video's details; asked once per link for 30 minutes (each lookup takes a few seconds).</summary>
    public Task<YouTubeVideo> ProbeAsync(string url, CancellationToken token)
    {
        url = url.Trim();
        if (_videos.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(30) && !cached.Video.IsFaulted && !cached.Video.IsCanceled)
            return cached.Video;
        var task = _importer.ProbeAsync(url, CancellationToken.None);
        _videos[url] = (task, DateTime.UtcNow);
        return task.WaitAsync(token);
    }

    // Each frame or preview runs yt-dlp, Deno and FFmpeg (about 130 MB together). Dragging the section
    // asks for a new frame every moment: only the newest request per slot is kept (older ones are
    // cancelled, their processes killed) and at most two run at once, so a 1 GB container never fills up.
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _latest = new();

    /// <summary>Runs <paramref name="work"/> as the newest request for <paramref name="slot"/>, cancelling the previous one.</summary>
    private async Task<T> LatestAsync<T>(string slot, CancellationToken requestAborted, Func<CancellationToken, Task<T>> work)
    {
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        if (_latest.TryGetValue(slot, out var previous))
            previous.Cancel();
        _latest[slot] = mine;
        try
        {
            await _downloads.WaitAsync(mine.Token);
            try
            {
                return await work(mine.Token);
            }
            finally
            {
                _downloads.Release();
            }
        }
        finally
        {
            _latest.TryRemove(new KeyValuePair<string, CancellationTokenSource>(slot, mine));
        }
    }

    /// <summary>The exact frame at <paramref name="seconds"/> (PNG); <paramref name="slot"/> names the picture it is for ("first", "last").</summary>
    public async Task<string> FrameAsync(string url, double seconds, string slot, CancellationToken token)
    {
        var video = await ProbeAsync(url, token);
        var at = Math.Clamp(seconds, 0, Math.Max(0, video.DurationSeconds - 0.05));
        return await LatestAsync("frame:" + slot, token, t => _importer.FrameAsync(video, at, t));
    }

    /// <summary>
    /// A light copy of exactly the section, starting at 0 (the download keeps YouTube's timestamps and
    /// starts at a keyframe before the section; browsers would show those absolute times), so the page
    /// simply loops the whole file.
    /// </summary>
    public Task<(string Id, double Offset, double Length)> PreviewAsync(string url, double start, double length, CancellationToken token) =>
        LatestAsync("preview", token, t => MakePreviewAsync(url, start, length, t));

    private async Task<(string Id, double Offset, double Length)> MakePreviewAsync(string url, double start, double length, CancellationToken token)
    {
        var video = await ProbeAsync(url, token);
        var clip = await _importer.DownloadPreviewAsync(video, start, length, token);
        var exact = Path.ChangeExtension(clip.FilePath, null) + "-exact.mp4";
        try
        {
            await MediaPreparer.RunFfmpegAsync([
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", clip.OffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", clip.FilePath,
                "-t", clip.LengthSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p", "-movflags", "+faststart", exact,
            ], token);
        }
        finally
        {
            File.Delete(clip.FilePath);
        }
        var id = Guid.NewGuid().ToString("N")[..12];
        _previews[id] = exact;
        return (id, 0, clip.LengthSeconds);
    }

    public string? PreviewFile(string id) => _previews.TryGetValue(id, out var file) && File.Exists(file) ? file : null;

    /// <summary>Downloads the section in the best quality into the library, trimmed to exactly what was chosen.</summary>
    public async Task<MediaItem> ImportAsync(string url, double start, double length, IProgress<ProgressInfo> progress, CancellationToken token)
    {
        progress.Report(new ProgressInfo("Reading the video…"));
        var video = await ProbeAsync(url, token);
        var clip = await _importer.DownloadAsync(video, start, length, progress, token);
        progress.Report(new ProgressInfo("Adding it to the library…", 1));
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{video.Title} ({YouTubeImporter.FormatTime(start)}, {Math.Round(clip.LengthSeconds)} s){Path.GetExtension(clip.FilePath)}");
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        Prune();
        return await _library.AddAsync(clip.FilePath, name, url, token, clip.OffsetSeconds, clip.LengthSeconds);
    }

    /// <summary>Frames and previews are only useful while choosing: keep them a day.</summary>
    private void Prune()
    {
        foreach (var folder in new[] { "frames", "previews" })
        {
            var dir = Path.Combine(_library.DownloadDir, folder);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                        File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
