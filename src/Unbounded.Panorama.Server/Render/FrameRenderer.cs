using SkiaSharp;
using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Server.Model;

namespace Unbounded.Panorama.Server.Render;

/// <summary>
/// Composes one frame: background colour, the media's current frame, the text widgets (drawn the
/// way the Windows app previews them: Roboto, gradient badges) and the drawing, then brightness and
/// rotation for the panel.
/// </summary>
public sealed class FrameRenderer : IDisposable
{
    private readonly Dictionary<FontWeightName, SKTypeface> _fonts = new();
    private SKBitmap? _canvasBitmap;
    private SKBitmap? _output;
    private SKBitmap? _media;
    private SKImage? _drawing;
    private string? _drawingStamp;

    public FrameRenderer(string fontDir)
    {
        foreach (var (weight, file) in new[] { (FontWeightName.Light, "Roboto-Light.ttf"), (FontWeightName.Regular, "Roboto-Regular.ttf"), (FontWeightName.Medium, "Roboto-Medium.ttf"), (FontWeightName.Bold, "Roboto-Bold.ttf") })
            _fonts[weight] = SKTypeface.FromFile(Path.Combine(fontDir, file)) ?? SKTypeface.Default;
    }

    /// <summary>Renders into a BGRA buffer of the panel's size (after rotation) and returns the design-orientation frame for the preview.</summary>
    public SKBitmap Render(int panelWidth, int panelHeight, PanelDesign design, PanelSettings settings, BackgroundPlayer player,
        Func<Widget, string> textOf, string? drawingFile, Span<byte> output)
    {
        var quarter = settings.Rotation is 90 or 270;
        var (w, h) = quarter ? (panelHeight, panelWidth) : (panelWidth, panelHeight);
        _canvasBitmap = Ensure(_canvasBitmap, w, h);
        using (var canvas = new SKCanvas(_canvasBitmap))
        {
            canvas.Clear(ParseColor(design.Background.Color, SKColors.Black));
            if (settings.On)
            {
                DrawMedia(canvas, player);
                DrawDrawing(canvas, drawingFile, w, h);
                foreach (var widget in design.Widgets)
                    if (!widget.Hidden)
                        DrawWidget(canvas, widget, textOf(widget));
            }
            else
            {
                canvas.Clear(SKColors.Black);
            }
        }

        _output = Ensure(_output, panelWidth, panelHeight);
        using (var canvas = new SKCanvas(_output))
        {
            canvas.Clear(SKColors.Black);
            canvas.Translate(panelWidth / 2f, panelHeight / 2f);
            canvas.RotateDegrees(settings.Rotation);
            canvas.Translate(-w / 2f, -h / 2f);
            using var paint = new SKPaint();
            var level = Math.Clamp(settings.Brightness, 0, 100) / 100f;
            if (level < 1)
                paint.ColorFilter = SKColorFilter.CreateColorMatrix([level, 0, 0, 0, 0, 0, level, 0, 0, 0, 0, 0, level, 0, 0, 0, 0, 0, 1, 0]);
            using var frame = SKImage.FromBitmap(_canvasBitmap);
            canvas.DrawImage(frame, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        _output.GetPixelSpan().CopyTo(output);
        return _canvasBitmap;
    }

    private void DrawMedia(SKCanvas canvas, BackgroundPlayer player)
    {
        if (player.Placement is not { } p)
            return;
        _media = Ensure(_media, p.VisibleWidth, p.VisibleHeight);
        var pixels = _media.GetPixelSpan();
        unsafe
        {
            fixed (byte* ptr = pixels)
            {
                if (!player.TryCopyFrame(new Span<byte>(ptr, pixels.Length)))
                    return;
            }
        }
        _media.NotifyPixelsChanged();
        using var image = SKImage.FromBitmap(_media);
        canvas.DrawImage(image, p.VisibleX, p.VisibleY, new SKSamplingOptions(SKFilterMode.Nearest));
    }

    private void DrawDrawing(SKCanvas canvas, string? file, int w, int h)
    {
        if (file is null || !File.Exists(file))
        {
            _drawing?.Dispose();
            _drawing = null;
            _drawingStamp = null;
            return;
        }
        var stamp = $"{File.GetLastWriteTimeUtc(file).Ticks}";
        if (stamp != _drawingStamp)
        {
            _drawing?.Dispose();
            using var data = SKData.Create(file);
            _drawing = data is null ? null : SKImage.FromEncodedData(data);
            _drawingStamp = stamp;
        }
        if (_drawing is not null)
            canvas.DrawImage(_drawing, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear));
    }

    /// <summary>Text in its box (X, Y, Width), aligned left/center/right, optionally on a gradient pill.</summary>
    public void DrawWidget(SKCanvas canvas, Widget w, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        using var font = new SKFont(_fonts.GetValueOrDefault(w.Weight) ?? SKTypeface.Default, Math.Max(4, w.Size)) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var textWidth = font.MeasureText(text);
        var metrics = font.Metrics;
        var lineHeight = metrics.Descent - metrics.Ascent;
        float padLeft = 0, padTop = 0, padRight = 0, padBottom = 0;
        if (w.Badge)
        {
            padLeft = padRight = w.Size * 0.35f;
            padTop = w.Size * 0.05f;
            padBottom = w.Size * 0.08f;
        }
        var boxWidth = textWidth + padLeft + padRight;
        var left = w.Align switch
        {
            TextAlign.Center => w.X + (w.Width - boxWidth) / 2f,
            TextAlign.Right => w.X + w.Width - boxWidth,
            _ => (float)w.X,
        };
        var top = (float)w.Y;
        if (w.Badge)
        {
            var rect = new SKRect(left, top, left + boxWidth, top + padTop + lineHeight + padBottom);
            using var pill = new SKPaint { IsAntialias = true };
            pill.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0),
                [ParseColor(w.BadgeFrom, SKColors.DarkRed), ParseColor(w.BadgeTo, SKColors.OrangeRed)], SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(rect, w.Size * 0.3f, w.Size * 0.3f, pill);
        }
        using var paint = new SKPaint { Color = ParseColor(w.Color, SKColors.White), IsAntialias = true };
        canvas.DrawText(text, left + padLeft, top + padTop - metrics.Ascent, SKTextAlign.Left, font, paint);
    }

    public static SKColor ParseColor(string? hex, SKColor fallback) =>
        hex is not null && SKColor.TryParse(hex, out var c) ? c : fallback;

    private static SKBitmap Ensure(SKBitmap? bitmap, int w, int h)
    {
        if (bitmap is not null && bitmap.Width == w && bitmap.Height == h)
            return bitmap;
        bitmap?.Dispose();
        return new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
    }

    public void Dispose()
    {
        _canvasBitmap?.Dispose();
        _output?.Dispose();
        _media?.Dispose();
        _drawing?.Dispose();
        foreach (var f in _fonts.Values)
            f.Dispose();
    }
}
