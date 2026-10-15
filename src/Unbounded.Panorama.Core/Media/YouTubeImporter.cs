using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Unbounded.Panorama.Media;

public sealed record VideoFormatInfo(string Id, int Width, int Height, double Fps, string Codec, bool Hdr);

public sealed record YouTubeVideo(string Url, string Id, string Title, string Channel, double DurationSeconds, string? ThumbnailUrl, VideoFormatInfo Best, bool OnlyHdrAvailable)
{
    public string BestDescription =>
        $"{Best.Width}×{Best.Height} · {Best.Fps:0.#} fps · {(Best.Hdr ? "HDR" : "SDR")} · {Best.Codec}";
}

/// <summary>A downloaded section and where the requested start lies inside it.</summary>
public sealed record ImportedClip(string FilePath, double OffsetSeconds, double LengthSeconds);

/// <summary>
/// YouTube import through yt-dlp: picks the highest-resolution stream (SDR preferred, since HDR
/// would wash out when converted for the cooler), downloads only the chosen section and keeps the
/// source timestamps (-copyts), so the requested start can be cut frame-exactly afterwards.
/// </summary>
public sealed partial class YouTubeImporter(string downloadDir)
{
    /// <summary>Highest resolution, SDR first; HDR only when nothing else exists.</summary>
    public const string FormatSelector = "bv*[dynamic_range=SDR]/bv*";

    public async Task<YouTubeVideo> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        var json = await RunYtDlpAsync(["--no-playlist", "-J", url], null, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var formats = new List<VideoFormatInfo>();
        foreach (var f in root.GetProperty("formats").EnumerateArray())
        {
            var vcodec = Str(f, "vcodec");
            if (vcodec is null or "none" || !f.TryGetProperty("height", out var h) || h.ValueKind != JsonValueKind.Number)
                continue;
            var width = f.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
            var fps = f.TryGetProperty("fps", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : 0;
            var range = Str(f, "dynamic_range") ?? "SDR";
            formats.Add(new VideoFormatInfo(Str(f, "format_id") ?? "?", width, h.GetInt32(), fps, ShortCodec(vcodec), !range.Equals("SDR", StringComparison.OrdinalIgnoreCase)));
        }
        if (formats.Count == 0)
            throw new InvalidOperationException("This video has no downloadable video streams.");

        static IEnumerable<VideoFormatInfo> Ranked(IEnumerable<VideoFormatInfo> list) =>
            list.OrderByDescending(f => f.Height).ThenByDescending(f => f.Width).ThenByDescending(f => f.Fps);
        var sdr = Ranked(formats.Where(f => !f.Hdr)).FirstOrDefault();
        var best = sdr ?? Ranked(formats).First();

        return new YouTubeVideo(
            url,
            Str(root, "id") ?? "",
            Str(root, "title") ?? "(untitled)",
            Str(root, "channel") ?? Str(root, "uploader") ?? "",
            root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
            Str(root, "thumbnail"),
            best,
            OnlyHdrAvailable: sdr is null);
    }

    /// <summary>Exact still at <paramref name="seconds"/> (downloads half a second at ≤720p).</summary>
    public async Task<string> FrameAsync(YouTubeVideo video, double seconds, CancellationToken cancellationToken)
    {
        var dir = Directory.CreateDirectory(Path.Combine(downloadDir, "frames")).FullName;
        var stem = $"{video.Id}-{seconds.ToString("0.###", CultureInfo.InvariantCulture)}";
        var png = Path.Combine(dir, stem + ".png");
        if (File.Exists(png))
            return png;

        var clip = await DownloadSectionAsync(video, seconds, 0.5, "bv*[height<=720][dynamic_range=SDR]/bv*[height<=720]/bv*", Path.Combine(dir, stem), null, cancellationToken).ConfigureAwait(false);
        try
        {
            await MediaPreparer.RunFfmpegAsync([
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", clip.OffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", clip.FilePath,
                "-frames:v", "1", "-update", "1", png,
            ], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(clip.FilePath);
        }
        return png;
    }

    /// <summary>Downloads [start, start+length] at the best resolution.</summary>
    public Task<ImportedClip> DownloadAsync(YouTubeVideo video, double start, double length, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var stem = Path.Combine(downloadDir, $"{video.Id}-{start.ToString("0.###", CultureInfo.InvariantCulture)}-{length.ToString("0.###", CultureInfo.InvariantCulture)}");
        return DownloadSectionAsync(video, start, length, FormatSelector, stem, progress, cancellationToken);
    }

    private async Task<ImportedClip> DownloadSectionAsync(YouTubeVideo video, double start, double length, string format, string stem, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        start = Math.Max(0, start);
        var end = video.DurationSeconds > 0 ? Math.Min(start + length, video.DurationSeconds) : start + length;
        if (end <= start)
            throw new InvalidOperationException("The chosen start is past the end of the video.");

        foreach (var stale in Directory.EnumerateFiles(Path.GetDirectoryName(stem)!, Path.GetFileName(stem) + ".*"))
            TryDelete(stale);

        var section = $"*{start.ToString("0.###", CultureInfo.InvariantCulture)}-{end.ToString("0.###", CultureInfo.InvariantCulture)}";
        var output = await RunYtDlpAsync([
            "--no-playlist", "--no-part", "--newline",
            "-f", format,
            "--download-sections", section,
            // Keep source timestamps so the true section start (a keyframe before 'start') is known.
            "--downloader-args", "ffmpeg_o:-copyts",
            "-o", stem + ".%(ext)s",
            "--print", "after_move:filepath",
            video.Url,
        ], progress, cancellationToken).ConfigureAwait(false);

        var file = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(File.Exists)
            ?? Directory.EnumerateFiles(Path.GetDirectoryName(stem)!, Path.GetFileName(stem) + ".*").FirstOrDefault()
            ?? throw new InvalidOperationException("yt-dlp finished but no file was written.");

        var startTime = await ProbeStartTimeAsync(file, cancellationToken).ConfigureAwait(false);
        var offset = Math.Max(0, start - startTime);
        return new ImportedClip(file, offset, end - start);
    }

    private static async Task<double> ProbeStartTimeAsync(string file, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Tools.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=start_time", "-of", "default=nw=1:nk=1", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0;
    }

    private static async Task<string> RunYtDlpAsync(IReadOnlyList<string> args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Tools.YtDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--encoding");
        psi.ArgumentList.Add("utf-8");
        psi.ArgumentList.Add("--ffmpeg-location");
        psi.ArgumentList.Add(Path.GetDirectoryName(Tools.FfmpegPath)!);
        if (Tools.DenoPath is { } deno)
        {
            // YouTube's player checks need a JavaScript runtime.
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add("deno:" + deno);
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp.");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readOut = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                stdout.AppendLine(line);
                if (progress is not null && ProgressLine().Match(line) is { Success: true } m)
                    progress.Report($"Downloading… {m.Groups[1].Value}%");
            }
        }, cancellationToken);
        var readErr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                stderr.AppendLine(line);
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var message = stderr.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal))
                ?? stderr.ToString().Trim();
            throw new InvalidOperationException($"yt-dlp failed: {message}");
        }
        return stdout.ToString();
    }

    [GeneratedRegex(@"\[download\]\s+([\d.]+)%")]
    private static partial Regex ProgressLine();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ShortCodec(string vcodec) => vcodec switch
    {
        _ when vcodec.StartsWith("av01", StringComparison.Ordinal) => "AV1",
        _ when vcodec.StartsWith("vp09", StringComparison.Ordinal) || vcodec.StartsWith("vp9", StringComparison.Ordinal) => "VP9",
        _ when vcodec.StartsWith("avc1", StringComparison.Ordinal) => "H.264",
        _ => vcodec,
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>Parses "90", "1:30" or "1:02:03" into seconds.</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3)
            return false;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                return false;
            seconds = seconds * 60 + v;
        }
        return true;
    }

    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
