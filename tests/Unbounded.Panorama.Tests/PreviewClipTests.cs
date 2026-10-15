using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Unbounded.Panorama.Media;
using Xunit;

namespace Unbounded.Panorama.Tests;

public class PreviewClipTests
{
    /// <summary>Opens a file with WPF's MediaPlayer (the engine behind MediaElement) and reports what happened.</summary>
    public static (bool Opened, TimeSpan Duration, string? Error) OpenInWindowsPlayer(string file)
    {
        (bool, TimeSpan, string?) result = (false, TimeSpan.Zero, "timed out");
        var thread = new Thread(() =>
        {
            var player = new MediaPlayer { IsMuted = true };
            var frame = new DispatcherFrame();
            player.MediaOpened += (_, _) =>
            {
                result = (true, player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero, null);
                frame.Continue = false;
            };
            player.MediaFailed += (_, e) =>
            {
                result = (false, TimeSpan.Zero, e.ErrorException.Message);
                frame.Continue = false;
            };
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            player.Open(new Uri(file));
            player.Play();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            player.Close();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public async Task Upload_stream_preview_is_upright_capped_and_plays()
    {
        string ffmpeg;
        try
        {
            ffmpeg = Tools.FfmpegPath;
        }
        catch (Exception)
        {
            return;
        }
        if (!File.Exists(ffmpeg))
            return;

        var dir = Path.Combine(Path.GetTempPath(), "upn360-stream-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A portrait design as uploaded: 1080x2240 turned counter-clockwise into a raw 2240x1080 stream, 4 s.
            var h264 = Path.Combine(dir, "upload.h264");
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1080x2240:r=30:d=4",
                "-vf", "transpose=cclock,format=yuv420p", "-c:v", "libx264", "-preset", "ultrafast", "-f", "h264", h264], CancellationToken.None);

            Assert.InRange(await MediaPreparer.ProbeStreamSecondsAsync(h264, CancellationToken.None), 3.9, 4.1);

            var clip = Path.Combine(dir, "clip.mp4");
            await MediaPreparer.RenderStreamPreviewClipAsync(h264, portrait: true, maxSeconds: 2, clip, CancellationToken.None);
            var (w, h) = await MediaPreparer.ProbeSizeAsync(clip, CancellationToken.None);
            Assert.True(h > w, $"expected upright (tall), got {w}x{h}");
            Assert.Equal(480, w);

            var (opened, duration, error) = OpenInWindowsPlayer(clip);
            Assert.True(opened, error);
            Assert.InRange(duration.TotalSeconds, 1.9, 2.1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_clip_of_a_vp9_video_plays_in_the_windows_player()
    {
        string ffmpeg;
        try
        {
            ffmpeg = Tools.FfmpegPath;
        }
        catch (Exception)
        {
            return; // FFmpeg is not installed on this machine: nothing to check.
        }
        if (!File.Exists(ffmpeg))
            return;

        var dir = Path.Combine(Path.GetTempPath(), "upn360-clip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A 6 s VP9 source like a YouTube download, wider than the preview limit.
            var source = Path.Combine(dir, "source.webm");
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=6",
                "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "1M", source], CancellationToken.None);

            var clip = Path.Combine(dir, "clip.mp4");
            await MediaPreparer.RenderPreviewClipAsync(source, 1, 4, clip, CancellationToken.None);

            var (opened, duration, error) = OpenInWindowsPlayer(clip);
            Assert.True(opened, error);
            // Start 1 s and length 4 s are applied, like the upload.
            Assert.InRange(duration.TotalSeconds, 3.8, 4.2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
