using System.Drawing;
using System.IO;
using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Media;
using Xunit;

namespace Unbounded.Panorama.Tests;

public class TwoMediaTests
{
    private static bool HasFfmpeg()
    {
        try
        {
            return File.Exists(Tools.FfmpegPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static BackgroundMedia Filling(string path, ScreenArea area, int w, int h)
    {
        var box = Placement.Fill(Placement.AreaBox(area, Orientation.Portrait), w, h);
        return new BackgroundMedia { SourcePath = path, Area = area, SourceWidth = w, SourceHeight = h, MediaX = box.X, MediaY = box.Y, MediaWidth = box.W };
    }

    /// <summary>Upright frame of the uploaded stream at a time, as the cooler shows it.</summary>
    private static async Task<Color[]> SampleAsync(string h264, double seconds, string dir, params Point[] points)
    {
        var png = Path.Combine(dir, $"frame-{seconds:0.00}.png");
        await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "h264", "-framerate", "30", "-i", h264,
            "-vf", $"select=gte(t\\,{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}),transpose=clock", "-frames:v", "1", png], CancellationToken.None);
        using var bitmap = new Bitmap(png);
        Assert.Equal(1080, bitmap.Width);
        Assert.Equal(2240, bitmap.Height);
        return points.Select(p => bitmap.GetPixel(p.X, p.Y)).ToArray();
    }

    private static string Name(Color c) =>
        c.R > 150 && c.G < 100 && c.B < 100 ? "red"
        : c.B > 150 && c.R < 100 && c.G < 100 ? "blue"
        : c.R > 150 && c.G > 150 && c.B < 100 ? "yellow"
        : c.R < 40 && c.G < 40 && c.B < 40 ? "black"
        : $"({c.R},{c.G},{c.B})";

    [Fact]
    public async Task Top_and_front_media_are_combined_and_the_shorter_one_loops()
    {
        if (!HasFfmpeg())
            return;
        var dir = Path.Combine(Path.GetTempPath(), "upn360-two-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Top: 3 s of red. Front: a 1 s clip, blue then yellow, which has to loop 3 times.
            var red = Path.Combine(dir, "red.mp4");
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=red:s=640x360:r=30:d=3",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", red], CancellationToken.None);
            var flip = Path.Combine(dir, "flip.mp4");
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=blue:s=640x360:r=30:d=0.5",
                "-f", "lavfi", "-i", "color=yellow:s=640x360:r=30:d=0.5", "-filter_complex", "concat=n=2:v=1",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", flip], CancellationToken.None);

            var top = Filling(red, ScreenArea.TopFace, 640, 360);
            var front = Filling(flip, ScreenArea.FrontFace, 640, 360);
            var prepared = await new MediaPreparer(Path.Combine(dir, "cache")).PrepareAsync(top, Orientation.Portrait, null, null, CancellationToken.None, front);

            Assert.InRange(await MediaPreparer.ProbeStreamSecondsAsync(prepared.StreamPath, CancellationToken.None), 2.9, 3.1);
            var topPoint = new Point(540, 550);
            var frontPoint = new Point(540, 1650);
            var expected = new (double At, string Front)[] { (0.25, "blue"), (0.75, "yellow"), (1.25, "blue"), (1.75, "yellow"), (2.25, "blue"), (2.75, "yellow") };
            foreach (var (at, frontColour) in expected)
            {
                var c = await SampleAsync(prepared.StreamPath, at, dir, topPoint, frontPoint);
                Assert.Equal("red", Name(c[0]));
                Assert.True(Name(c[1]) == frontColour, $"front at {at} s: expected {frontColour}, got {Name(c[1])}");
            }

            // The library/thumbnail still shows both faces too.
            Assert.True(File.Exists(prepared.PreviewPath));
            using var still = new Bitmap(prepared.PreviewPath);
            Assert.Equal("red", Name(still.GetPixel(topPoint.X, topPoint.Y)));
            Assert.Contains(Name(still.GetPixel(frontPoint.X, frontPoint.Y)), new[] { "blue", "yellow" });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Pictures_make_a_short_looping_clip_not_a_minute()
    {
        if (!HasFfmpeg())
            return;
        var dir = Path.Combine(Path.GetTempPath(), "upn360-stills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var redPng = Path.Combine(dir, "red.png");
            var bluePng = Path.Combine(dir, "blue.png");
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=red:s=1920x1080", "-frames:v", "1", redPng], CancellationToken.None);
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=blue:s=1920x1080", "-frames:v", "1", bluePng], CancellationToken.None);
            var preparer = new MediaPreparer(Path.Combine(dir, "cache"));

            // One picture on the whole screen.
            var single = await preparer.PrepareAsync(Filling(redPng, ScreenArea.Whole, 1920, 1080), Orientation.Portrait, null, null, CancellationToken.None);
            Assert.InRange(await MediaPreparer.ProbeStreamSecondsAsync(single.StreamPath, CancellationToken.None), 1.9, 2.1);

            // Two pictures, one per face.
            var both = await preparer.PrepareAsync(Filling(redPng, ScreenArea.TopFace, 1920, 1080), Orientation.Portrait, null, null, CancellationToken.None,
                Filling(bluePng, ScreenArea.FrontFace, 1920, 1080));
            Assert.InRange(await MediaPreparer.ProbeStreamSecondsAsync(both.StreamPath, CancellationToken.None), 1.9, 2.1);
            var c = await SampleAsync(both.StreamPath, 1.0, dir, new Point(540, 550), new Point(540, 1650));
            Assert.Equal("red", Name(c[0]));
            Assert.Equal("blue", Name(c[1]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_drawing_stays_on_top_of_pictures_and_videos()
    {
        if (!HasFfmpeg())
            return;
        var dir = Path.Combine(Path.GetTempPath(), "upn360-ink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bluePng = Path.Combine(dir, "blue.png");
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=blue:s=1920x1080", "-frames:v", "1", bluePng], CancellationToken.None);
            var red = Path.Combine(dir, "red.mp4");
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=red:s=640x360:r=30:d=3",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", red], CancellationToken.None);
            // A drawing like the app makes: a transparent canvas-size PNG, here a yellow square at (100,100)-(300,300).
            // (FFmpeg's drawbox on rgba leaves alpha at 0, which made an invisible fixture.)
            var ink = Path.Combine(dir, "ink.png");
            using (var canvas = new Bitmap(1080, 2240, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.FillRectangle(Brushes.Yellow, 100, 100, 200, 200);
                }
                canvas.Save(ink, System.Drawing.Imaging.ImageFormat.Png);
            }
            var preparer = new MediaPreparer(Path.Combine(dir, "cache"));
            var drawn = new Point(200, 200);
            var plain = new Point(700, 1600);

            // A picture with the drawing: 2 s, drawing over the picture.
            var picture = await preparer.PrepareAsync(Filling(bluePng, ScreenArea.Whole, 1920, 1080), Orientation.Portrait, ink, null, CancellationToken.None);
            Assert.InRange(await MediaPreparer.ProbeStreamSecondsAsync(picture.StreamPath, CancellationToken.None), 1.9, 2.1);
            var c = await SampleAsync(picture.StreamPath, 1.5, dir, drawn, plain);
            Assert.Equal("yellow", Name(c[0]));
            Assert.Equal("blue", Name(c[1]));

            // A video with the drawing: the video's length, drawing still there at the end.
            var video = await preparer.PrepareAsync(Filling(red, ScreenArea.Whole, 640, 360), Orientation.Portrait, ink, null, CancellationToken.None);
            Assert.InRange(await MediaPreparer.ProbeStreamSecondsAsync(video.StreamPath, CancellationToken.None), 2.9, 3.1);
            c = await SampleAsync(video.StreamPath, 2.8, dir, drawn, plain);
            Assert.Equal("yellow", Name(c[0]));
            Assert.Equal("red", Name(c[1]));

            // Two media with the drawing (top: video, front: picture).
            var both = await preparer.PrepareAsync(Filling(red, ScreenArea.TopFace, 640, 360), Orientation.Portrait, ink, null, CancellationToken.None,
                Filling(bluePng, ScreenArea.FrontFace, 1920, 1080));
            Assert.InRange(await MediaPreparer.ProbeStreamSecondsAsync(both.StreamPath, CancellationToken.None), 2.9, 3.1);
            c = await SampleAsync(both.StreamPath, 2.8, dir, drawn, plain, new Point(700, 600));
            Assert.Equal("yellow", Name(c[0]));
            Assert.Equal("blue", Name(c[1]));
            Assert.Equal("red", Name(c[2]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Stopping_a_conversion_ends_ffmpeg_at_once_and_leaves_nothing()
    {
        if (!HasFfmpeg())
            return;
        var dir = Path.Combine(Path.GetTempPath(), "upn360-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A 2-minute 4K source: its conversion takes far longer than the 1 s before the stop.
            var big = Path.Combine(dir, "big.mkv");
            await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=30:d=120",
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "40", big], CancellationToken.None);
            var cache = Path.Combine(dir, "cache");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new MediaPreparer(cache).PrepareAsync(Filling(big, ScreenArea.Whole, 3840, 2160), Orientation.Portrait, null, null, stop.Token));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"stopping took {clock.Elapsed.TotalSeconds:0.0} s");
            Assert.Empty(Directory.GetFiles(cache));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static BackgroundMedia Media(string name, ScreenArea area) => Filling(name, area, 1920, 1080);

    [Fact]
    public void Placing_on_the_whole_screen_replaces_everything()
    {
        var top = Media("a.mp4", ScreenArea.TopFace);
        var front = Media("b.mp4", ScreenArea.FrontFace);
        Assert.Null(Placement.MakeRoom(top, front, ScreenArea.Whole, Orientation.Portrait));
    }

    [Fact]
    public void A_whole_screen_media_moves_to_the_other_face_and_fills_it()
    {
        var whole = Media("a.mp4", ScreenArea.Whole);
        var stays = Placement.MakeRoom(whole, null, ScreenArea.TopFace, Orientation.Portrait);
        Assert.Same(whole, stays);
        Assert.Equal(ScreenArea.FrontFace, stays!.Area);
        var front = Placement.AreaBox(ScreenArea.FrontFace, Orientation.Portrait);
        var expected = Placement.Fill(front, 1920, 1080);
        Assert.Equal(expected.X, stays.MediaX);
        Assert.Equal(expected.Y, stays.MediaY);
        Assert.Equal(expected.W, stays.MediaWidth);
    }

    [Fact]
    public void Placing_on_a_face_keeps_the_other_faces_media()
    {
        var top = Media("a.mp4", ScreenArea.TopFace);
        var front = Media("b.mp4", ScreenArea.FrontFace);
        // Replacing the top (the edited one) keeps the front.
        Assert.Same(front, Placement.MakeRoom(top, front, ScreenArea.TopFace, Orientation.Portrait));
        // Placing on the front while editing the top: the top stays, the old front goes.
        var stays = Placement.MakeRoom(top, front, ScreenArea.FrontFace, Orientation.Portrait);
        Assert.Same(top, stays);
        Assert.Equal(ScreenArea.TopFace, stays!.Area);
    }

    [Fact]
    public void Nothing_yet_or_landscape_keeps_nothing_on_the_target_face()
    {
        var front = Media("b.mp4", ScreenArea.FrontFace);
        Assert.Same(front, Placement.MakeRoom(null, front, ScreenArea.TopFace, Orientation.Portrait));
        Assert.Null(Placement.MakeRoom(null, front, ScreenArea.FrontFace, Orientation.Portrait));
        Assert.Null(Placement.MakeRoom(Media("a.mp4", ScreenArea.Whole), null, ScreenArea.TopFace, Orientation.Landscape));
    }

    [Fact]
    public void Second_media_needs_portrait_and_the_opposite_face()
    {
        var top = new BackgroundMedia { SourcePath = "a.mp4", Area = ScreenArea.TopFace };
        var front = new BackgroundMedia { SourcePath = "b.mp4", Area = ScreenArea.FrontFace, SourceWidth = 10, SourceHeight = 10, MediaX = 0, MediaY = 0, MediaWidth = 10 };
        Assert.Same(front, Placement.Pairable(top, front, Orientation.Portrait));
        Assert.Null(Placement.Pairable(top, front, Orientation.Landscape));
        Assert.Null(Placement.Pairable(new BackgroundMedia { SourcePath = "a.mp4", Area = ScreenArea.Whole }, front, Orientation.Portrait));
        Assert.Null(Placement.Pairable(new BackgroundMedia { SourcePath = "a.mp4", Area = ScreenArea.FrontFace }, front, Orientation.Portrait));
        Assert.Null(Placement.Pairable(new BackgroundMedia { DeviceFile = "preset.mp4", Area = ScreenArea.TopFace }, front, Orientation.Portrait));
    }
}
