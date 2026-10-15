using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.Media;

public sealed record PreparedMedia(string StreamPath, string RemoteName, string PreviewPath, long Size);

/// <summary>
/// Converts any image, GIF or video into the cooler's format: raw Annex-B H.264, 2240×1080,
/// yuv420p, 30 fps (libx264 veryfast, CRF 23). Portrait content is composed on a 1080×2240
/// canvas and turned counter-clockwise, which plays upright with ui_rotation 270.
/// </summary>
public sealed class MediaPreparer
{
    public const long MaxPreparedBytes = 500L * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];

    private readonly string _cacheDir;

    public MediaPreparer(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>A plain black screen (2 s still, a few KB), used when no background is chosen.</summary>
    public async Task<PreparedMedia> PrepareBlackAsync(CancellationToken cancellationToken)
    {
        const string key = "black-v1";
        var stream = Path.Combine(_cacheDir, key + ".h264");
        if (!File.Exists(stream) || new FileInfo(stream).Length == 0)
        {
            var temp = stream + ".tmp";
            await RunFfmpegAsync([
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"color=c=black:s={ScreenLayout.PanelWidth}x{ScreenLayout.PanelHeight}:r=30:d=2",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-vf", "format=yuv420p,fps=30",
                "-an", "-f", "h264", temp,
            ], cancellationToken).ConfigureAwait(false);
            File.Move(temp, stream, overwrite: true);
        }
        var remote = $"upn360-{key}.mp4.h264_{ScreenLayout.PanelWidth}x{ScreenLayout.PanelHeight}";
        return new PreparedMedia(stream, remote, "", new FileInfo(stream).Length);
    }

    public async Task<PreparedMedia> PrepareAsync(BackgroundMedia media, Orientation orientation, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var source = media.SourcePath ?? throw new InvalidOperationException("No background selected.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Background file not found.", source);

        var key = CacheKey(source, media, orientation);
        var stream = Path.Combine(_cacheDir, key + ".h264");
        var preview = Path.Combine(_cacheDir, key + ".png");
        var remoteName = $"upn360-{key}.mp4.h264_{ScreenLayout.PanelWidth}x{ScreenLayout.PanelHeight}";

        if (!File.Exists(stream) || new FileInfo(stream).Length == 0)
        {
            progress?.Report("Converting background for the cooler…");
            var temp = stream + ".tmp";
            await RunFfmpegAsync(BuildEncodeArgs(source, media, orientation, temp), cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(temp).Length;
            if (size == 0 || size > MaxPreparedBytes)
            {
                File.Delete(temp);
                throw new InvalidOperationException($"Converted file is {size / 1048576.0:F1} MB; the cooler accepts 1 byte to 500 MB. Shorten the clip.");
            }
            File.Move(temp, stream, overwrite: true);
        }

        if (!File.Exists(preview))
            await RenderPreviewAsync(media, orientation, preview, cancellationToken).ConfigureAwait(false);

        return new PreparedMedia(stream, remoteName, preview, new FileInfo(stream).Length);
    }

    /// <summary>Upright still of the background as the editor shows it (canvas orientation, before rotation).</summary>
    public async Task RenderPreviewAsync(BackgroundMedia media, Orientation orientation, string pngPath, CancellationToken cancellationToken)
    {
        var source = media.SourcePath!;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (!IsImage(source))
            args.AddRange(["-ss", Seconds(media.StartSeconds + 0.5)]);
        args.AddRange(["-i", source, "-frames:v", "1", "-vf", CanvasFilter(media, orientation), "-update", "1", pngPath]);
        await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
    }

    public static List<string> BuildEncodeArgs(string source, BackgroundMedia media, Orientation orientation, string output)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (IsImage(source))
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

        var filter = CanvasFilter(media, orientation);
        if (orientation == Orientation.Portrait)
            filter += ",transpose=cclock";
        filter += ",format=yuv420p,fps=30";

        args.AddRange([
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
            "-vf", filter, "-an", "-f", "h264",
            "-fs", (MaxPreparedBytes + 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            output,
        ]);
        return args;
    }

    /// <summary>Scales and crops/pads the source to the editor canvas (1080×2240 or 2240×1080).</summary>
    public static string CanvasFilter(BackgroundMedia media, Orientation orientation)
    {
        var (w, h) = orientation == Orientation.Portrait
            ? (ScreenLayout.PanelHeight, ScreenLayout.PanelWidth)
            : (ScreenLayout.PanelWidth, ScreenLayout.PanelHeight);
        // Square pixels first, so anamorphic sources keep their shape.
        var sar = "scale='if(lte(sar,0),iw,max(1,round(iw*sar)))':ih,setsar=1";
        if (media.Fit == BackgroundFit.Fit)
            return $"{sar},scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";

        var cx = Math.Clamp(media.CropX, 0, 1).ToString("0.####", CultureInfo.InvariantCulture);
        var cy = Math.Clamp(media.CropY, 0, 1).ToString("0.####", CultureInfo.InvariantCulture);
        return $"{sar},scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}:(iw-{w})*{cx}:(ih-{h})*{cy},setsar=1";
    }

    private static string CacheKey(string source, BackgroundMedia media, Orientation orientation)
    {
        var info = new FileInfo(source);
        var material = string.Join("|",
            "v1", Path.GetFullPath(source), info.Length, info.LastWriteTimeUtc.Ticks,
            media.Fit, Seconds(media.CropX), Seconds(media.CropY), Seconds(media.StartSeconds),
            media.DurationSeconds is { } d ? Seconds(d) : "all", orientation);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static async Task RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Tools.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
