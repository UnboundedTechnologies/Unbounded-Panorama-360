using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.Media;

public sealed record PreparedMedia(string StreamPath, string RemoteName, string PreviewPath, long Size);

/// <summary>
/// Converts any image, GIF or video (or a plain black screen) into the cooler's format: raw
/// Annex-B H.264, 2240×1080, yuv420p, 30 fps (see <see cref="EncoderArgs"/>). Content is composed on
/// the editor canvas (1080×2240 portrait or 2240×1080 landscape), an optional transparent drawing
/// is laid on top, and portrait output is turned counter-clockwise, which plays upright with
/// ui_rotation 270 (verified on hardware).
/// </summary>
public sealed class MediaPreparer
{
    public const long MaxPreparedBytes = 500L * 1024 * 1024;

    /// <summary>
    /// Length of the clip for anything that does not move: a picture, a black or drawing-only screen,
    /// two pictures. The cooler loops the file, so 2 s looks the same as any length but converts and
    /// uploads in a fraction of the time (a black 2 s clip was verified on the cooler).
    /// </summary>
    private const double StillSeconds = 2;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];

    private readonly string _cacheDir;

    public MediaPreparer(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ------------------------------------------------------------------ encoder

    /// <summary>
    /// The stream sent to the cooler. NVIDIA's hardware encoder (NVENC) is used when present: on a
    /// 4K60 AV1 source it converted in 4.2 s per 30 s where libx264 veryfast took 7.6 s, at higher
    /// quality (SSIM 0.9873 against 0.9853). Without it (other GPUs, older drivers) the software
    /// encoder is used, which every PC has.
    /// </summary>
    /// <param name="seconds">
    /// Length of the output. It caps the average bitrate so the file stays under the cooler's 500 MB
    /// limit: far above what short clips use (24 s allows ~150 Mbit/s), it only matters for long,
    /// busy clips, and the large buffer still lets short peaks through at full quality.
    /// </param>
    public static IReadOnlyList<string> EncoderArgs(bool hardware, double seconds = 0)
    {
        List<string> args = hardware
            ? ["-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "26", "-b:v", "0", "-spatial-aq", "1", "-profile:v", "high"]
            : ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23"];
        if (seconds > 0)
        {
            // At most 200 Mbit/s (far above what 1080×2240 ever needs; short clips would otherwise get a
            // value, and a buffer, larger than the encoders accept: verified, libx264 rejects > 2^31).
            var cap = Math.Min(200_000_000L, (long)(MaxPreparedBytes * 0.94 * 8 / seconds));
            args.AddRange(["-maxrate", cap.ToString(CultureInfo.InvariantCulture), "-bufsize", (cap * 4).ToString(CultureInfo.InvariantCulture)]);
        }
        return args;
    }

    /// <summary>
    /// Repeats one finished frame endlessly at 30 fps. Pictures and the drawing are scaled and placed
    /// once and then repeated, instead of being read and resized again for every frame (with -loop 1
    /// a 4K picture made a 30 s video + picture conversion take 21.2 s instead of 3.9 s).
    /// </summary>
    private const string Repeat = "loop=loop=-1:size=1:start=0,setpts=N/30/TB";

    private static bool? _hardwareAvailable;
    private static bool _hardwareFailed;

    /// <summary>Whether NVENC works here, found once with a tiny test encode at the cooler's size.</summary>
    public static async Task<bool> HardwareEncoderAvailableAsync(CancellationToken cancellationToken)
    {
        if (_hardwareFailed)
            return false;
        if (_hardwareAvailable is { } known)
            return known;
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=2240x1080:r=30:d=0.2" };
            args.AddRange(EncoderArgs(true));
            args.AddRange(["-f", "null", "-"]);
            await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
            _hardwareAvailable = true;
        }
        catch (InvalidOperationException)
        {
            _hardwareAvailable = false;
        }
        return _hardwareAvailable.Value;
    }

    // ------------------------------------------------------------------ shared conversions

    /// <summary>One conversion, shared by everyone who wants the same result (a background run and Apply).</summary>
    private sealed class Job
    {
        public required CancellationTokenSource Cancel { get; init; }
        public Task<PreparedMedia>? Task { get; set; }
        public bool Foreground { get; set; }
        public Process? Process { get; set; }
        public List<IProgress<ProgressInfo>> Listeners { get; } = [];
        public ProgressInfo? Last { get; set; }

        /// <summary>Callers waiting for it; when the last one gives up, the conversion stops.</summary>
        public int Waiters { get; set; }
    }

    private readonly object _jobsGate = new();
    private readonly Dictionary<string, Job> _jobs = [];

    /// <summary>Lets RunFfmpegAsync hand each ffmpeg it starts to the conversion running it (for its priority).</summary>
    private static readonly AsyncLocal<Action<Process>?> ProcessStarted = new();

    /// <summary>
    /// Converts a design for the cooler (cached by its content). A request for a design that is already
    /// being converted joins that conversion instead of starting another. <paramref name="background"/>
    /// conversions run at below-normal priority and can be stopped with <see cref="CancelBackground"/>;
    /// when Apply joins one, it is raised to normal priority and is no longer cancelled by edits.
    /// Cancelling <paramref name="cancellationToken"/> only stops this caller from waiting.
    /// </summary>
    /// <param name="overlayPng">Transparent PNG of the canvas size drawn over the background, or null.</param>
    /// <param name="second">Media for the other face (portrait), combined with <paramref name="media"/>; see <see cref="Placement.Pairable"/>.</param>
    public async Task<PreparedMedia> PrepareAsync(BackgroundMedia media, Orientation orientation, string? overlayPng, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken,
        BackgroundMedia? second = null, bool background = false)
    {
        var pair = Placement.Pairable(media, second, orientation);
        foreach (var path in new[] { media.SourcePath, pair?.SourcePath, overlayPng })
        {
            if (path is not null && !File.Exists(path))
                throw new FileNotFoundException("A file of this design was not found.", path);
        }
        var key = CacheKey(media, orientation, overlayPng, pair);
        if (File.Exists(Path.Combine(_cacheDir, key + ".h264")))
            return await PrepareCoreAsync(media, orientation, overlayPng, progress, cancellationToken, second).ConfigureAwait(false);

        Job job;
        lock (_jobsGate)
        {
            if (!_jobs.TryGetValue(key, out job!))
            {
                job = new Job { Cancel = new CancellationTokenSource() };
                var shared = job;
                var report = new Progress(p =>
                {
                    List<IProgress<ProgressInfo>> listeners;
                    lock (_jobsGate)
                    {
                        shared.Last = p;
                        listeners = [.. shared.Listeners];
                    }
                    foreach (var l in listeners)
                        l.Report(p);
                });
                _jobs[key] = job;
                job.Task = System.Threading.Tasks.Task.Run(async () =>
                {
                    ProcessStarted.Value = p =>
                    {
                        lock (_jobsGate)
                            shared.Process = p;
                        SetPriority(p, shared.Foreground);
                    };
                    try
                    {
                        return await PrepareCoreAsync(media, orientation, overlayPng, report, shared.Cancel.Token, second).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_jobsGate)
                            _jobs.Remove(key);
                        shared.Cancel.Dispose();
                    }
                });
            }
            if (!background && !job.Foreground)
            {
                job.Foreground = true;
                if (job.Process is { } running)
                    SetPriority(running, foreground: true);
            }
            if (progress is not null)
            {
                job.Listeners.Add(progress);
                if (job.Last is { } last)
                    progress.Report(last);
            }
            job.Waiters++;
        }
        try
        {
            return await job.Task!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This caller gave up. If nobody else waits for it, stop the conversion and return only once
            // ffmpeg is gone (its files are released).
            bool last;
            lock (_jobsGate)
            {
                last = --job.Waiters == 0;
                job.Waiters++; // balanced in finally
                if (last)
                    try { job.Cancel.Cancel(); } catch (ObjectDisposedException) { }
            }
            if (last)
                try { await job.Task!.ConfigureAwait(false); } catch { }
            throw;
        }
        finally
        {
            lock (_jobsGate)
            {
                job.Waiters--;
                if (progress is not null)
                    job.Listeners.Remove(progress);
            }
        }
    }

    /// <summary>
    /// Identity of a design's converted file: equal keys mean the same video for the cooler (text
    /// widgets are not part of it). Throws when one of its files is missing.
    /// </summary>
    public static string KeyOf(BackgroundMedia media, Orientation orientation, string? overlayPng, BackgroundMedia? second) =>
        CacheKey(media, orientation, overlayPng, Placement.Pairable(media, second, orientation));

    /// <summary>Stops background conversions nobody is waiting for (the design changed again).</summary>
    public void CancelBackground()
    {
        lock (_jobsGate)
        {
            foreach (var job in _jobs.Values.Where(j => !j.Foreground))
                job.Cancel.Cancel();
        }
    }

    /// <summary>Stops every conversion (the app is closing).</summary>
    public void CancelAll()
    {
        lock (_jobsGate)
        {
            foreach (var job in _jobs.Values)
                job.Cancel.Cancel();
        }
    }

    private static void SetPriority(Process process, bool foreground)
    {
        try
        {
            process.PriorityClass = foreground ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already finished.
        }
    }

    private sealed class Progress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) => report(value);
    }

    private async Task<PreparedMedia> PrepareCoreAsync(BackgroundMedia media, Orientation orientation, string? overlayPng, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken,
        BackgroundMedia? second)
    {
        var source = media.SourcePath;
        if (source is not null && !File.Exists(source))
            throw new FileNotFoundException("Background file not found.", source);
        if (overlayPng is not null && !File.Exists(overlayPng))
            throw new FileNotFoundException("Drawing image not found.", overlayPng);
        second = Placement.Pairable(media, second, orientation);
        if (second?.SourcePath is { } secondSource && !File.Exists(secondSource))
            throw new FileNotFoundException("Background file not found.", secondSource);

        var key = CacheKey(media, orientation, overlayPng, second);
        var stream = Path.Combine(_cacheDir, key + ".h264");
        var preview = Path.Combine(_cacheDir, key + ".png");
        var remoteName = $"upn360-{key}.mp4.h264_{ScreenLayout.PanelWidth}x{ScreenLayout.PanelHeight}";

        if (!File.Exists(stream) || new FileInfo(stream).Length == 0)
        {
            progress?.Report(new ProgressInfo("Converting background for the cooler…", 0));
            var temp = stream + ".tmp";
            var pieces = new List<string>();
            try
            {
                async Task EncodeAsync(bool hardware)
                {
                    double expected;
                    List<string> args;
                    if (second is null)
                    {
                        expected = await ExpectedSecondsAsync(media, cancellationToken).ConfigureAwait(false);
                        args = BuildEncodeArgs(media, orientation, overlayPng, temp, hardware, expected);
                    }
                    else
                    {
                        (args, expected) = await BuildComposedArgsAsync(media, second, orientation, overlayPng, temp, pieces, progress, cancellationToken, hardware).ConfigureAwait(false);
                    }
                    await RunFfmpegAsync(args, cancellationToken, seconds =>
                    {
                        var fraction = expected > 0 ? Math.Clamp(seconds / expected, 0, 1) : (double?)null;
                        progress?.Report(new ProgressInfo(fraction is { } f ? $"Converting background… {f:P0}" : "Converting background…", fraction));
                    }).ConfigureAwait(false);
                }

                var useHardware = await HardwareEncoderAvailableAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EncodeAsync(useHardware).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (useHardware)
                {
                    // The GPU encoder failed on this content (or is busy): retry with the software encoder.
                    progress?.Report(new ProgressInfo("Converting background…", 0));
                    await EncodeAsync(false).ConfigureAwait(false);
                    // Only now is it known to be the GPU's fault (the same job worked without it): stop using it.
                    _hardwareFailed = true;
                }
            }
            catch
            {
                // Stopped (app closing) or failed: leave nothing half-written behind.
                try { File.Delete(temp); } catch { }
                throw;
            }
            finally
            {
                foreach (var piece in pieces)
                    try { File.Delete(piece); } catch { }
            }
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
                await RenderPreviewAsync(media, orientation, preview, cancellationToken, second).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The preview is cosmetic (library thumbnail); never fail an upload for it.
            }
        }

        return new PreparedMedia(stream, remoteName, preview, new FileInfo(stream).Length);
    }

    /// <summary>Upright still of the background as the editor shows it (canvas orientation, before rotation).</summary>
    public async Task RenderPreviewAsync(BackgroundMedia media, Orientation orientation, string pngPath, CancellationToken cancellationToken, BackgroundMedia? second = null)
    {
        var source = media.SourcePath ?? throw new InvalidOperationException("No background file.");
        second = Placement.Pairable(media, second, orientation);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var m in second is null ? new[] { media } : new[] { media, second })
        {
            if (!IsImage(m.SourcePath!))
                args.AddRange(["-ss", Seconds(m.StartSeconds + 0.5)]);
            args.AddRange(["-i", m.SourcePath!]);
        }
        if (second is null)
            args.AddRange(["-vf", CanvasFilter(media, orientation)]);
        else
            args.AddRange(["-filter_complex", $"[0:v]{CanvasFilter(media, orientation)}[a];[1:v]{SecondPiece(second, orientation)}[b];[a][b]{SecondOverlay(second, orientation)}[out]", "-map", "[out]"]);
        args.AddRange(["-frames:v", "1", "-update", "1", pngPath]);
        await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The second media placed on the canvas, cut down to its own face.</summary>
    private static string SecondPiece(BackgroundMedia second, Orientation orientation)
    {
        var area = Placement.AreaBox(second.Area, orientation);
        return string.Create(CultureInfo.InvariantCulture,
            $"{CanvasFilter(second, orientation)},crop={(int)area.W}:{(int)area.H}:{(int)area.X}:{(int)area.Y}");
    }

    /// <summary>Lays the second media's face over the first (whose own content stays on its face).</summary>
    private static string SecondOverlay(BackgroundMedia second, Orientation orientation)
    {
        var area = Placement.AreaBox(second.Area, orientation);
        return string.Create(CultureInfo.InvariantCulture, $"overlay={(int)area.X}:{(int)area.Y}");
    }

    /// <summary>
    /// Two media, one per face, into one stream. The longest video sets the length (pictures stay
    /// up the whole time); a shorter video loops its own start..length section. FFmpeg's
    /// -stream_loop ignores an input's -ss/-t (verified: it froze on the last frame), so a looping
    /// section is first cut into a small piece of its own, which then loops exactly (verified).
    /// </summary>
    private async Task<(List<string> Args, double Seconds)> BuildComposedArgsAsync(BackgroundMedia first, BackgroundMedia second, Orientation orientation,
        string? overlayPng, string output, List<string> pieces, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken, bool hardware)
    {
        var media = new[] { first, second };
        var lengths = new double?[2];
        for (var i = 0; i < 2; i++)
            lengths[i] = IsImage(media[i].SourcePath!) ? null : await ExpectedSecondsAsync(media[i], cancellationToken).ConfigureAwait(false);
        var videos = lengths.Where(l => l is > 0).Select(l => l!.Value).ToList();
        var total = videos.Count > 0 ? videos.Max() : StillSeconds;

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var filters = new string[2];
        for (var i = 0; i < 2; i++)
        {
            var m = media[i];
            var placed = CanvasFilter(m, orientation);
            if (lengths[i] is null)
            {
                // A picture is placed once, then that finished frame repeats (see Repeat).
                args.AddRange(["-i", m.SourcePath!]);
                filters[i] = $"{placed},{Repeat}";
            }
            else if (lengths[i]!.Value >= total - 0.05)
            {
                if (m.StartSeconds > 0)
                    args.AddRange(["-ss", Seconds(m.StartSeconds)]);
                if (m.DurationSeconds is { } d)
                    args.AddRange(["-t", Seconds(d)]);
                args.AddRange(["-i", m.SourcePath!]);
                filters[i] = placed;
            }
            else
            {
                // Shorter video: its section, already placed on the canvas, cut into a piece that loops exactly.
                progress?.Report(new ProgressInfo("Preparing the shorter clip to loop…"));
                var piece = Path.Combine(_cacheDir, $"loop-{Guid.NewGuid():N}.mp4");
                pieces.Add(piece);
                var cut = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
                if (m.StartSeconds > 0)
                    cut.AddRange(["-ss", Seconds(m.StartSeconds)]);
                cut.AddRange(["-t", Seconds(lengths[i]!.Value), "-i", m.SourcePath!, "-vf", $"fps=30,{placed},format=yuv420p",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-an", piece]);
                await RunFfmpegAsync(cut, cancellationToken).ConfigureAwait(false);
                args.AddRange(["-stream_loop", "-1", "-i", piece]);
                filters[i] = "setsar=1";
            }
        }

        var (w, h) = CanvasSize(orientation);
        var area = Placement.AreaBox(second.Area, orientation);
        var graph = string.Create(CultureInfo.InvariantCulture,
            $"[0:v]fps=30,{filters[0]}[a];[1:v]fps=30,{filters[1]},crop={(int)area.W}:{(int)area.H}:{(int)area.X}:{(int)area.Y}[b];[a][b]{SecondOverlay(second, orientation)}[ab]");
        var last = "ab";
        if (overlayPng is not null)
        {
            args.AddRange(["-i", overlayPng]);
            graph += $";[2:v]scale={w}:{h},format=yuva420p,{Repeat}[ink];[ab][ink]overlay=0:0[abi]";
            last = "abi";
        }
        var rotate = orientation == Orientation.Portrait ? "transpose=cclock," : "";
        graph += $";[{last}]{rotate}format=yuv420p,fps=30[out]";
        args.AddRange(["-filter_complex", graph, "-map", "[out]", "-t", Seconds(total)]);
        args.AddRange(EncoderArgs(hardware, total));
        args.AddRange([
            "-an", "-f", "h264",
            "-fs", (MaxPreparedBytes + 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            output,
        ]);
        return (args, total);
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
            "-vf", $"fps=30,scale='if(lte(sar,0),iw,max(1,round(iw*sar)))':ih,setsar=1,scale='min({maxWidth},iw)':-2,format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-an", "-movflags", "+faststart", "-f", "mp4", mp4Path]);
        await RunFfmpegAsync(args, cancellationToken, onSeconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Card preview of a prepared upload (raw H.264, exactly as sent to the cooler): turned back
    /// upright for portrait designs, the first <paramref name="maxSeconds"/>, at most 480 px wide.
    /// </summary>
    public static async Task RenderStreamPreviewClipAsync(string h264Path, bool portrait, double maxSeconds, string mp4Path, CancellationToken cancellationToken)
    {
        // A raw stream has no container timing: it is read at the 30 fps it was encoded at.
        var upright = portrait ? "transpose=clock," : "";
        await RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "h264", "-framerate", "30", "-i", h264Path,
            "-t", Seconds(maxSeconds), "-vf", $"{upright}scale='min(480,iw)':-2,format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-an", "-movflags", "+faststart", "-f", "mp4", mp4Path],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Length in seconds of a prepared upload (its frame count at 30 fps).</summary>
    public static async Task<double> ProbeStreamSecondsAsync(string h264Path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Tools.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-f", "h264", "-count_packets", "-select_streams", "v:0", "-show_entries", "stream=nb_read_packets", "-of", "default=nw=1:nk=1", h264Path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var text = await p.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) ? frames / 30.0 : 0;
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

    public static List<string> BuildEncodeArgs(BackgroundMedia media, Orientation orientation, string? overlayPng, string output, bool hardware = false, double seconds = 0)
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
            // Placed once, then the finished frame repeats for StillSeconds (see Repeat).
            args.AddRange(["-i", source]);
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
        var still = source is not null && IsImage(source);
        var canvas = source is null ? "setsar=1" : still ? $"{CanvasFilter(media, orientation)},{Repeat}" : CanvasFilter(media, orientation);
        if (overlayPng is null)
        {
            args.AddRange(["-vf", $"fps=30,{canvas}{rotate},format=yuv420p"]);
        }
        else
        {
            // The drawing is prepared once and repeats; 'shortest' ends with the background.
            args.AddRange(["-i", overlayPng]);
            args.AddRange([
                "-filter_complex",
                $"[0:v]fps=30,{canvas}[bg];[1:v]scale={w}:{h},format=yuva420p,{Repeat}[ink];[bg][ink]overlay=0:0:shortest=1{rotate},format=yuv420p[out]",
                "-map", "[out]",
            ]);
        }

        // Output option (after every input): a repeated picture is endless, so stop after StillSeconds.
        if (still)
            args.AddRange(["-t", Seconds(StillSeconds)]);
        args.AddRange(EncoderArgs(hardware, seconds));
        args.AddRange([
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

    private static string CacheKey(BackgroundMedia media, Orientation orientation, string? overlayPng, BackgroundMedia? second = null)
    {
        var parts = new List<object> { "v2", orientation };
        void Add(BackgroundMedia m, string source)
        {
            var info = new FileInfo(source);
            parts.AddRange([Path.GetFullPath(source), info.Length, info.LastWriteTimeUtc.Ticks,
                m.Fit, Seconds(m.CropX), Seconds(m.CropY), Seconds(m.StartSeconds),
                m.DurationSeconds is { } d ? Seconds(d) : "all",
                m.HasPlacement ? PlacementFilter(m, orientation, "") : "legacy"]);
        }
        if (media.SourcePath is { } source)
            Add(media, source);
        else
            parts.Add("black");
        if (second?.SourcePath is { } secondSource)
        {
            // A single-media key never contains this marker, so earlier cached files stay valid.
            parts.Add("second");
            Add(second, secondSource);
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
            return StillSeconds;
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
        ProcessStarted.Value?.Invoke(process);
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
            // Kill returns before the process is gone; wait for it, so its files are released when this returns.
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(5000); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {(await stderr.ConfigureAwait(false)).Trim()}");
    }
}
