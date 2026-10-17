using System.Diagnostics;
using SkiaSharp;
using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Server.Lcd;
using Unbounded.Panorama.Server.Library;
using Unbounded.Panorama.Server.Model;
using Unbounded.Panorama.Server.Render;
using Unbounded.Panorama.Server.Storage;

namespace Unbounded.Panorama.Server.Display;

/// <summary>
/// Streams the design to the screen: renders a frame at the chosen rate (the Thermalright firmware
/// shows its logo again after 2-3 s without one), connects again when the screen goes away, and keeps
/// the last frame for the web page's live preview.
/// </summary>
public sealed class DisplayService : BackgroundService
{
    private readonly IScreen _screen;
    private readonly DataStore _store;
    private readonly MediaLibrary _library;
    private readonly WidgetText _text;
    private readonly FrameRenderer _renderer;
    private readonly BackgroundPlayer _player;
    private readonly ILogger<DisplayService> _log;
    private readonly object _previewGate = new();
    private byte[]? _previewJpeg;
    private long _previewStamp;
    private SKBitmap? _previewSource;
    private string? _playing;
    private volatile bool _designDirty = true;

    public DisplayService(IScreen screen, DataStore store, MediaLibrary library, WidgetText text, ILogger<DisplayService> log)
    {
        _screen = screen;
        _store = store;
        _library = library;
        _text = text;
        _log = log;
        _renderer = new FrameRenderer(Path.Combine(AppContext.BaseDirectory, "Fonts"));
        _player = new BackgroundPlayer(Path.Combine(store.CacheDir, "clips"), log);
        store.DesignChanged += () => _designDirty = true;
        store.SettingsChanged += () => _designDirty = true;
    }

    public bool Connected => _screen.Connected;
    public string ScreenName => _screen.Name;
    public int Width => _screen.Width;
    public int Height => _screen.Height;
    public double FramesPerSecond { get; private set; }
    public string? LastError { get; private set; }
    public string? MediaStatus => _player.Status;

    /// <summary>The current frame as JPEG (design orientation, before brightness), at most 10 times a second.</summary>
    public byte[]? PreviewJpeg()
    {
        lock (_previewGate)
        {
            if (_previewSource is null)
                return null;
            if (_previewJpeg is null || Stopwatch.GetElapsedTime(_previewStamp) > TimeSpan.FromMilliseconds(100))
            {
                using var image = SKImage.FromBitmap(_previewSource);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                _previewJpeg = data.ToArray();
                _previewStamp = Stopwatch.GetTimestamp();
            }
            return _previewJpeg;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var frame = Array.Empty<byte>();
        var lastConnect = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 10;
        var sent = 0;
        var rateClock = Stopwatch.StartNew();
        while (!stoppingToken.IsCancellationRequested)
        {
            var tick = Stopwatch.GetTimestamp();
            var settings = _store.Settings;
            try
            {
                if (!_screen.Connected && Stopwatch.GetElapsedTime(lastConnect) > TimeSpan.FromSeconds(2))
                {
                    lastConnect = Stopwatch.GetTimestamp();
                    if (_screen.Connect())
                    {
                        _log.LogInformation("Screen connected: {Name}", _screen.Name);
                        _designDirty = true;
                        LastError = null;
                    }
                    else
                    {
                        LastError = (_screen as ThermalrightBulkScreen)?.LastError;
                    }
                }

                var design = _store.Design;
                if (_designDirty)
                {
                    _designDirty = false;
                    UpdatePlayer(design, settings);
                }

                if (frame.Length != _screen.Width * _screen.Height * 4)
                    frame = new byte[_screen.Width * _screen.Height * 4];
                var drawing = design.HasDrawing ? _store.DrawingFile : null;
                var canvas = _renderer.Render(_screen.Width, _screen.Height, design, settings, _player,
                    w => _text.Resolve(w, settings.Fahrenheit), drawing, frame);
                lock (_previewGate)
                {
                    if (_previewSource is null || _previewSource.Width != canvas.Width || _previewSource.Height != canvas.Height)
                    {
                        _previewSource?.Dispose();
                        _previewSource = new SKBitmap(canvas.Info);
                    }
                    canvas.CopyTo(_previewSource);
                }

                if (_screen.Connected)
                {
                    _screen.Send(frame);
                    sent++;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log.LogWarning("Display: {Message}", ex.Message);
            }

            if (rateClock.Elapsed >= TimeSpan.FromSeconds(2))
            {
                FramesPerSecond = sent / rateClock.Elapsed.TotalSeconds;
                sent = 0;
                rateClock.Restart();
            }
            var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(settings.FramesPerSecond, 5, 30));
            var wait = interval - Stopwatch.GetElapsedTime(tick);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts the right media when the background or its placement changed.</summary>
    private void UpdatePlayer(PanelDesign design, PanelSettings settings)
    {
        var bg = design.Background;
        var item = _library.Find(bg.MediaId);
        var quarter = settings.Rotation is 90 or 270;
        var (w, h) = quarter ? (_screen.Height, _screen.Width) : (_screen.Width, _screen.Height);
        if (item is null)
        {
            _playing = null;
            _player.Stop();
            return;
        }
        var placement = MediaPlacement.Compute(w, h, item.Width, item.Height, bg.X, bg.Y, bg.Width);
        var fps = Math.Clamp(settings.FramesPerSecond, 5, 30);
        var key = $"{item.Id}|{item.ClipStart}|{placement}|{bg.StartSeconds}|{bg.DurationSeconds}|{fps}";
        if (key == _playing)
            return;
        _playing = key;
        if (placement is null)
        {
            _player.Stop();
            return;
        }
        // Times are relative to the item: a YouTube section starts ClipStart seconds into its file.
        var start = Math.Clamp(bg.StartSeconds, 0, Math.Max(0, item.Duration - 0.1));
        var length = Math.Min(bg.DurationSeconds ?? double.MaxValue, item.Duration - start);
        _player.Play(_library.PathOf(item), item.IsImage, placement, item.ClipStart + start, item.IsImage ? null : length, fps);
    }

    public override void Dispose()
    {
        _player.Dispose();
        _renderer.Dispose();
        _previewSource?.Dispose();
        base.Dispose();
    }
}
