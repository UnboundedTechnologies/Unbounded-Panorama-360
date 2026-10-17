using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Unbounded.Panorama.Media;

namespace Unbounded.Panorama.Server.Render;

/// <summary>Where the media sits on the screen and which part of it is visible, in screen pixels.</summary>
public sealed record MediaPlacement(int MediaWidth, int MediaHeight, int VisibleX, int VisibleY, int VisibleWidth, int VisibleHeight, int CropX, int CropY)
{
    /// <summary>
    /// Media of <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/> drawn at (x, y) with
    /// <paramref name="width"/>; null placement covers the screen (centered crop). Null when nothing is visible.
    /// </summary>
    public static MediaPlacement? Compute(int screenWidth, int screenHeight, int sourceWidth, int sourceHeight, double? x, double? y, double? width)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return null;
        double w, h, left, top;
        if (width is { } mw && mw > 0)
        {
            w = mw;
            h = mw * sourceHeight / sourceWidth;
            left = x ?? (screenWidth - w) / 2;
            top = y ?? (screenHeight - h) / 2;
        }
        else
        {
            var scale = Math.Max((double)screenWidth / sourceWidth, (double)screenHeight / sourceHeight);
            w = sourceWidth * scale;
            h = sourceHeight * scale;
            left = (screenWidth - w) / 2;
            top = (screenHeight - h) / 2;
        }
        // Even sizes: ffmpeg's scaler and most decoders want them.
        var mediaW = Math.Max(2, (int)Math.Round(w / 2) * 2);
        var mediaH = Math.Max(2, (int)Math.Round(h / 2) * 2);
        var l = (int)Math.Round(left);
        var t = (int)Math.Round(top);
        var vx = Math.Max(0, l);
        var vy = Math.Max(0, t);
        var vr = Math.Min(screenWidth, l + mediaW);
        var vb = Math.Min(screenHeight, t + mediaH);
        if (vr - vx < 2 || vb - vy < 2)
            return null;
        var vw = (vr - vx) / 2 * 2;
        var vh = (vb - vy) / 2 * 2;
        return new MediaPlacement(mediaW, mediaH, vx, vy, vw, vh, vx - l, vy - t);
    }

    public string Filter(int fps) => string.Create(CultureInfo.InvariantCulture,
        $"scale='if(lte(sar,0),iw,max(1,round(iw*sar)))':ih,setsar=1,scale={MediaWidth}:{MediaHeight}:flags=lanczos,crop={VisibleWidth}:{VisibleHeight}:{CropX}:{CropY},fps={fps}");
}

/// <summary>
/// Plays the background: a picture is decoded once; a video is first rendered (once, cached) as a
/// small clip of exactly the visible part, then looped in real time. The renderer takes the latest frame.
/// </summary>
public sealed class BackgroundPlayer : IDisposable
{
    private readonly string _cacheDir;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private byte[]? _frame;
    private byte[]? _back;

    public BackgroundPlayer(string cacheDir, ILogger log)
    {
        _cacheDir = Directory.CreateDirectory(cacheDir).FullName;
        _log = log;
    }

    /// <summary>Where the current frame goes on the screen; null when there is no media.</summary>
    public MediaPlacement? Placement { get; private set; }

    /// <summary>Text for the web page while a video clip is being prepared ("Preparing the video… 40 %").</summary>
    public string? Status { get; private set; }

    /// <summary>Copies the latest frame (BGRA, Placement.VisibleWidth × VisibleHeight); false when none yet.</summary>
    public bool TryCopyFrame(Span<byte> target)
    {
        lock (_gate)
        {
            if (_frame is null || _frame.Length > target.Length)
                return false;
            _frame.CopyTo(target);
            return true;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? run;
        lock (_gate)
        {
            run = _run;
            _run = null;
            _frame = null;
            Placement = null;
            Status = null;
        }
        run?.Cancel();
        run?.Dispose();
    }

    public void Play(string source, bool image, MediaPlacement placement, double start, double? duration, int fps)
    {
        Stop();
        var run = new CancellationTokenSource();
        lock (_gate)
        {
            _run = run;
            Placement = placement;
        }
        _ = Task.Run(() => image ? ShowImageAsync(source, placement, run.Token) : PlayVideoAsync(source, placement, start, duration, fps, run.Token));
    }

    private async Task ShowImageAsync(string source, MediaPlacement placement, CancellationToken token)
    {
        try
        {
            var size = placement.VisibleWidth * placement.VisibleHeight * 4;
            await foreach (var frame in DecodeAsync(["-i", source, "-frames:v", "1", "-vf", placement.Filter(1)], size, token))
            {
                Publish(frame, size, token);
                break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Background picture: {Message}", ex.Message);
        }
    }

    private async Task PlayVideoAsync(string source, MediaPlacement placement, double start, double? duration, int fps, CancellationToken token)
    {
        try
        {
            var clip = await PrepareClipAsync(source, placement, start, duration, fps, token);
            Status = null;
            var size = placement.VisibleWidth * placement.VisibleHeight * 4;
            // The whole clip loops (a cut taken while looping froze in testing; this is the clip itself).
            await foreach (var frame in DecodeAsync(["-re", "-stream_loop", "-1", "-i", clip], size, token))
                Publish(frame, size, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = "The video could not be played: " + ex.Message;
            _log.LogWarning("Background video: {Message}", ex.Message);
        }
    }

    /// <summary>The visible part of the chosen section at the screen's size, as a small H.264 clip (cached).</summary>
    private async Task<string> PrepareClipAsync(string source, MediaPlacement placement, double start, double? duration, int fps, CancellationToken token)
    {
        var info = new FileInfo(source);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{placement}|{start}|{duration}|{fps}"))))[..20];
        var clip = Path.Combine(_cacheDir, key + ".mp4");
        if (File.Exists(clip))
            return clip;
        Status = "Preparing the video…";
        var length = duration ?? Math.Max(0, await MediaPreparer.ProbeDurationAsync(source, token) - start);
        var temp = clip + ".part.mp4";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (start > 0)
            args.AddRange(["-ss", start.ToString("0.###", CultureInfo.InvariantCulture)]);
        if (duration is { } d)
            args.AddRange(["-t", d.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", source, "-an", "-vf", placement.Filter(fps), "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p", temp]);
        await MediaPreparer.RunFfmpegAsync(args, token, seconds =>
        {
            if (length > 0)
                Status = $"Preparing the video… {Math.Clamp(seconds / length, 0, 1):P0}";
        });
        File.Move(temp, clip, overwrite: true);
        return clip;
    }

    private void Publish(byte[] frame, int size, CancellationToken token)
    {
        lock (_gate)
        {
            // A run that was replaced meanwhile must not show its last frame.
            if (_run is null || _run.Token != token)
                return;
            if (_back is null || _back.Length != size)
                _back = new byte[size];
            Buffer.BlockCopy(frame, 0, _back, 0, size);
            (_frame, _back) = (_back, _frame);
        }
    }

    /// <summary>Runs ffmpeg with raw BGRA output and yields each frame (the buffer is reused).</summary>
    private static async IAsyncEnumerable<byte[]> DecodeAsync(IReadOnlyList<string> input, int frameSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var psi = new ProcessStartInfo(Tools.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" })
            psi.ArgumentList.Add(a);
        foreach (var a in input)
            psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var errors = process.StandardError.ReadToEndAsync(token);
        using var kill = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[frameSize];
        while (!token.IsCancellationRequested)
        {
            try
            {
                await stream.ReadExactlyAsync(buffer, token);
            }
            catch (EndOfStreamException)
            {
                break;
            }
            yield return buffer;
        }
        await process.WaitForExitAsync(CancellationToken.None);
        if (!token.IsCancellationRequested && process.ExitCode != 0)
            throw new InvalidOperationException((await errors).Trim());
    }

    public void Dispose() => Stop();
}
