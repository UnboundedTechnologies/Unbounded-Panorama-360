using System.Diagnostics;
using System.IO;
using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Media;
using Xunit;

namespace Unbounded.Panorama.Tests;

/// <summary>Conversions shared between a background run and Apply (MediaPreparer.PrepareAsync).</summary>
public class BackgroundConversionTests : IAsyncLifetime
{
    private string _dir = "";
    private string _source = "";
    private bool _ready;

    public async Task InitializeAsync()
    {
        try
        {
            _ready = File.Exists(Tools.FfmpegPath);
        }
        catch (Exception)
        {
            _ready = false;
        }
        if (!_ready)
            return;
        _dir = Path.Combine(Path.GetTempPath(), "upn360-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // 40 s of 4K: converting it takes several seconds, long enough to join or stop it midway.
        _source = Path.Combine(_dir, "source.mkv");
        await MediaPreparer.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=30:d=40",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "35", _source], CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        if (_ready)
            Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    private BackgroundMedia Media()
    {
        var box = Placement.Fill(Placement.AreaBox(ScreenArea.Whole, Orientation.Portrait), 3840, 2160);
        return new BackgroundMedia { SourcePath = _source, SourceWidth = 3840, SourceHeight = 2160, MediaX = box.X, MediaY = box.Y, MediaWidth = box.W };
    }

    private static int FfmpegCount() => Process.GetProcessesByName("ffmpeg").Length;

    [Fact]
    public async Task Apply_joins_a_background_conversion_instead_of_starting_another()
    {
        if (!_ready)
            return;
        var preparer = new MediaPreparer(Path.Combine(_dir, "cache-join"));
        var before = FfmpegCount();
        var background = preparer.PrepareAsync(Media(), Orientation.Portrait, null, null, CancellationToken.None, background: true);
        await Task.Delay(1500);
        Assert.False(background.IsCompleted, "the test conversion finished too early to be joined");
        var seen = new List<ProgressInfo>();
        var apply = preparer.PrepareAsync(Media(), Orientation.Portrait, null, new SyncProgress(seen.Add), CancellationToken.None);
        await Task.Delay(500);
        Assert.Equal(before + 1, FfmpegCount()); // one conversion, not two
        preparer.CancelBackground(); // an edit now must not stop a conversion Apply is waiting for
        var (a, b) = (await apply, await background);
        Assert.Equal(a.StreamPath, b.StreamPath);
        Assert.True(File.Exists(a.StreamPath));
        Assert.Contains(seen, p => p.Fraction is > 0); // Apply sees the shared conversion's progress
    }

    [Fact]
    public async Task An_edit_stops_a_background_conversion_and_leaves_nothing()
    {
        if (!_ready)
            return;
        var cache = Path.Combine(_dir, "cache-edit");
        var preparer = new MediaPreparer(cache);
        var background = preparer.PrepareAsync(Media(), Orientation.Portrait, null, null, CancellationToken.None, background: true);
        await Task.Delay(1500);
        var clock = Stopwatch.StartNew();
        preparer.CancelBackground();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => background);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public async Task Closing_the_app_stops_every_conversion()
    {
        if (!_ready)
            return;
        var preparer = new MediaPreparer(Path.Combine(_dir, "cache-close"));
        var apply = preparer.PrepareAsync(Media(), Orientation.Portrait, null, null, CancellationToken.None);
        await Task.Delay(1500);
        preparer.CancelAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
    }

    private sealed class SyncProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value)
        {
            lock (this)
                report(value);
        }
    }
}
