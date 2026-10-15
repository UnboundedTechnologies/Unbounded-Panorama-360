using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.Media;

public sealed record PreparedMedia(string StreamPath, string RemoteName, string PreviewPath, long Size);

/// <summary>
/// Converts any image, GIF or video (or a plain black screen) into the cooler's format: raw
/// Annex-B H.264, 2240×1080, yuv420p, 30 fps (libx264 veryfast, CRF 23). Content is composed on
/// the editor canvas (1080×2240 portrait or 2240×1080 landscape), an optional transparent drawing
/// is laid on top, and portrait output is turned counter-clockwise, which plays upright with
/// ui_rotation 270 (verified on hardware).
/// </summary>
public sealed class MediaPreparer
{
    public const long MaxPreparedBytes = 500L * 1024 * 1024;

    /// <summary>Length of the clip used for a black or drawing-only screen.</summary>
    private const double StillSeconds = 2;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];

    private readonly string _cacheDir;

    public MediaPreparer(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <param name="overlayPng">Transparent PNG of the canvas size drawn over the background, or null.</param>
    public async Task<PreparedMedia> PrepareAsync(BackgroundMedia media, Orientation orientation, string? overlayPng, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        var source = media.SourcePath;
        if (source is not null && !File.Exists(source))
            throw new FileNotFoundException("Background file not found.", source);
        if (overlayPng is not null && !File.Exists(overlayPng))
            throw new FileNotFoundException("Drawing image not found.", overlayPng);

        var key = CacheKey(media, orientation, overlayPng);
        var stream = Path.Combine(_cacheDir, key + ".h264");
        var preview = Path.Combine(_cacheDir, key + ".png");
        var remoteName = $"upn360-{key}.mp4.h264_{ScreenLayout.PanelWidth}x{ScreenLayout.PanelHeight}";

        if (!File.Exists(stream) || new FileInfo(stream).Length == 0)
        {
            progress?.Report(new ProgressInfo("Converting background for the cooler…", 0));
            var expected = await ExpectedSecondsAsync(media, cancellationToken).ConfigureAwait(false);
            var temp = stream + ".tmp";
            await RunFfmpegAsync(BuildEncodeArgs(media, orientation, overlayPng, temp), cancellationToken, seconds =>
            {
                var fraction = expected > 0 ? Math.Clamp(seconds / expected, 0, 1) : (double?)null;
                progress?.Report(new ProgressInfo(
                    fraction is { } f ? $"Converting background… {f:P0}" : "Converting background…",
                    fraction,
                    $"{seconds:0} s of {expected:0} s"));
            }).ConfigureAwait(false);
            var size = new FileInfo(temp).Length;
            if (size == 0 || size > MaxPreparedBytes)
            {
                File.Delete(temp);
                throw new InvalidOperationException($"Converted file is {size / 1048576.0:F1} MB; the cooler accepts 1 byte to 500 MB. Shorten the clip.");
            }
            File.Move(temp, stream, overwrite: true);
        }

        if (!File.Exists(preview) && source is not null)
        {
            try
            {
                await RenderPreviewAsync(media, orientation, preview, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The preview is cosmetic (library thumbnail); never fail an upload for it.
            }
        }

        return new PreparedMedia(stream, remoteName, preview, new FileInfo(stream).Length);
    }

    /// <summary>Upright still of the background as the editor shows it (canvas orientation, before rotation).</summary>
    public async Task RenderPreviewAsync(BackgroundMedia media, Orientation orientation, string pngPath, CancellationToken cancellationToken)
    {
        var source = media.SourcePath ?? throw new InvalidOperationException("No background file.");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (!IsImage(source))
            args.AddRange(["-ss", Seconds(media.StartSeconds + 0.5)]);
        args.AddRange(["-i", source, "-frames:v", "1", "-vf", CanvasFilter(media, orientation), "-update", "1", pngPath]);
        await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Displayed size of the first video stream (width corrected for non-square pixels).</summary>
    public static async Task<(int Width, int Height)> ProbeSizeAsync(string file, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Tools.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,sample_aspect_ratio", "-of", "default=nw=1", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var text = await p.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        int width = 0, height = 0;
        double sar = 1;
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = (line.Split('=')[0], line[(line.IndexOf('=') + 1)..]);
            switch (key)
            {
                case "width": int.TryParse(value, CultureInfo.InvariantCulture, out width); break;
                case "height": int.TryParse(value, CultureInfo.InvariantCulture, out height); break;
                case "sample_aspect_ratio":
                    var parts = value.Split(':');
                    if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
                        && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0)
                        sar = n / d;
                    break;
            }
        }
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Could not read the size of this file.");
        return ((int)Math.Max(1, Math.Round(width * sar)), height);
    }

    /// <summary>Still of the source at its own shape (square pixels, at most 2240 wide) for the live editor preview.</summary>
    public static async Task RenderSourceFrameAsync(string source, double atSeconds, string pngPath, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (!IsImage(source))
            args.AddRange(["-ss", Seconds(Math.Max(0, atSeconds))]);
        args.AddRange(["-i", source, "-frames:v", "1",
            "-vf", "scale='if(lte(sar,0),iw,max(1,round(iw*sar)))':ih,setsar=1,scale='min(2240,iw)':-2",
            "-update", "1", pngPath]);
        await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The part of a video or GIF the cooler plays (same start and length as the upload), as a
    /// muted H.264 MP4 at the source's own shape and at most <paramref name="maxWidth"/> px wide, for
    /// playback in the app. H.264 is used because Windows' player cannot decode every source
    /// (verified: a VP9 .webm fails to open, its H.264 copy plays).
    /// </summary>
    public static async Task RenderPreviewClipAsync(string source, double startSeconds, double? durationSeconds, string mp4Path,
        CancellationToken cancellationToken, Action<double>? onSeconds = null, int maxWidth = 960)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (startSeconds > 0)
            args.AddRange(["-ss", Seconds(startSeconds)]);
        if (durationSeconds is { } duration)
            args.AddRange(["-t", Seconds(duration)]);
        args.AddRange(["-i", source,
            "-vf", $"scale='if(lte(sar,0),iw,max(1,round(iw*sar)))':ih,setsar=1,scale='min({maxWidth},iw)':-2,fps=30,format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-an", "-movflags", "+faststart", "-f", "mp4", mp4Path]);
        await RunFfmpegAsync(args, cancellationToken, onSeconds).ConfigureAwait(false);
    }

    /// <summary>Small upright thumbnail of any media file (library cards).</summary>
    public static async Task RenderThumbnailAsync(string source, double atSeconds, string jpgPath, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (!IsImage(source))
            args.AddRange(["-ss", Seconds(Math.Max(0, atSeconds))]);
        args.AddRange(["-i", source, "-frames:v", "1", "-vf", "scale=-2:360", "-q:v", "4", "-update", "1", jpgPath]);
        await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
    }

    public static List<string> BuildEncodeArgs(BackgroundMedia media, Orientation orientation, string? overlayPng, string output)
    {
        var (w, h) = CanvasSize(orientation);
        var source = media.SourcePath;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };

        if (source is null)
        {
            args.AddRange(["-f", "lavfi", "-i", $"color=c=black:s={w}x{h}:r=30:d={Seconds(StillSeconds)}"]);
        }
        else if (IsImage(source))
        {
            args.AddRange(["-loop", "1", "-framerate", "30", "-t", Seconds(media.DurationSeconds ?? 60), "-i", source]);
        }
        else
        {
            if (media.StartSeconds > 0)
                args.AddRange(["-ss", Seconds(media.StartSeconds)]);
            if (media.DurationSeconds is { } duration)
                args.AddRange(["-t", Seconds(duration)]);
            args.AddRange(["-i", source]);
        }

        var rotate = orientation == Orientation.Portrait ? ",transpose=cclock" : "";
        var canvas = source is null ? "setsar=1" : CanvasFilter(media, orientation);
        if (overlayPng is null)
        {
            args.AddRange(["-vf", $"{canvas}{rotate},format=yuv420p,fps=30"]);
        }
        else
        {
            // The drawing loops as a still; 'shortest' ends with the background.
            args.AddRange(["-loop", "1", "-i", overlayPng]);
            args.AddRange([
                "-filter_complex",
                $"[0:v]{canvas}[bg];[1:v]scale={w}:{h},format=rgba[ink];[bg][ink]overlay=0:0:shortest=1{rotate},format=yuv420p,fps=30[out]",
                "-map", "[out]",
            ]);
        }

        args.AddRange([
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
            "-an", "-f", "h264",
            "-fs", (MaxPreparedBytes + 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            output,
        ]);
        return args;
    }

    public static (int W, int H) CanvasSize(Orientation orientation) => orientation == Orientation.Portrait
        ? (ScreenLayout.PanelHeight, ScreenLayout.PanelWidth)
        : (ScreenLayout.PanelWidth, ScreenLayout.PanelHeight);

    /// <summary>Scales and places the source on the editor canvas (1080×2240 or 2240×1080).</summary>
    public static string CanvasFilter(BackgroundMedia media, Orientation orientation)
    {
        var (w, h) = CanvasSize(orientation);
        // Square pixels first, so anamorphic sources keep their shape.
        var sar = "scale='if(lte(sar,0),iw,max(1,round(iw*sar)))':ih,setsar=1";
        if (media.HasPlacement)
            return PlacementFilter(media, orientation, sar);
        if (media.Fit == BackgroundFit.Fit)
            return $"{sar},scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";

        var cx = Math.Clamp(media.CropX, 0, 1).ToString("0.####", CultureInfo.InvariantCulture);
        var cy = Math.Clamp(media.CropY, 0, 1).ToString("0.####", CultureInfo.InvariantCulture);
        return $"{sar},scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}:(iw-{w})*{cx}:(ih-{h})*{cy},setsar=1";
    }

    /// <summary>
    /// Exact placement: scale the source to the media rectangle, keep only the part inside the
    /// chosen area, and pad that onto a black canvas at its position. Works for media that is
    /// larger than the screen or partly off it. Values are rounded to even pixels (yuv420p).
    /// </summary>
    public static string PlacementFilter(BackgroundMedia media, Orientation orientation, string squarePixels)
    {
        var (w, h) = CanvasSize(orientation);
        var area = Placement.AreaBox(media.Area, orientation);
        var mw = Even(media.MediaWidth!.Value);
        var mh = Even(media.MediaHeight);
        var mx = Even(media.MediaX!.Value);
        var my = Even(media.MediaY!.Value);
        var visible = Placement.Visible(new Box(mx, my, mw, mh), area);
        if (visible is not { } v)
            return $"{squarePixels},scale=2:2,crop=2:2:0:0,pad={w}:{h}:0:0:color=black,setsar=1"; // nothing visible: black

        var vx = Even(v.X);
        var vy = Even(v.Y);
        var vw = Math.Max(2, Even(v.Right) - vx);
        var vh = Math.Max(2, Even(v.Bottom) - vy);
        vw = Math.Min(vw, mw - Math.Max(0, vx - mx));
        vh = Math.Min(vh, mh - Math.Max(0, vy - my));
        return string.Create(CultureInfo.InvariantCulture,
            $"{squarePixels},scale={mw}:{mh},setsar=1,crop={vw}:{vh}:{vx - mx}:{vy - my},pad={w}:{h}:{vx}:{vy}:color=black,setsar=1");
    }

    private static int Even(double value) => (int)Math.Round(value / 2) * 2;

    private static string CacheKey(BackgroundMedia media, Orientation orientation, string? overlayPng)
    {
        var parts = new List<object> { "v2", orientation };
        if (media.SourcePath is { } source)
        {
            var info = new FileInfo(source);
            parts.AddRange([Path.GetFullPath(source), info.Length, info.LastWriteTimeUtc.Ticks,
                media.Fit, Seconds(media.CropX), Seconds(media.CropY), Seconds(media.StartSeconds),
                media.DurationSeconds is { } d ? Seconds(d) : "all",
                media.HasPlacement ? PlacementFilter(media, orientation, "") : "legacy"]);
        }
        else
        {
            parts.Add("black");
        }
        if (overlayPng is not null)
            parts.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(overlayPng))));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>How many seconds of output the encode will produce (for the progress bar).</summary>
    private static async Task<double> ExpectedSecondsAsync(BackgroundMedia media, CancellationToken cancellationToken)
    {
        if (media.SourcePath is not { } source)
            return StillSeconds;
        if (IsImage(source))
            return media.DurationSeconds ?? 60;
        var total = await ProbeDurationAsync(source, cancellationToken).ConfigureAwait(false);
        var available = Math.Max(0, total - media.StartSeconds);
        return media.DurationSeconds is { } d ? Math.Min(d, available > 0 ? available : d) : available;
    }

    /// <summary>Container duration in seconds (0 when unknown).</summary>
    public static async Task<double> ProbeDurationAsync(string file, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Tools.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var text = await p.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <param name="onSeconds">Receives the output position in seconds as ffmpeg encodes (from -progress).</param>
    public static async Task RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken cancellationToken, Action<double>? onSeconds = null)
    {
        var psi = new ProcessStartInfo(Tools.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (onSeconds is not null)
        {
            // Machine-readable progress on stdout ("out_time_us=…" every half second). Global options go first.
            foreach (var a in new[] { "-progress", "pipe:1", "-stats_period", "0.5" })
                psi.ArgumentList.Add(a);
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (onSeconds is not null && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                    onSeconds(us / 1_000_000.0);
            }
        }, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {(await stderr.ConfigureAwait(false)).Trim()}");
    }
}
