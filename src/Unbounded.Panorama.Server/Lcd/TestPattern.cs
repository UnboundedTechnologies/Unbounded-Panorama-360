using SkiaSharp;

namespace Unbounded.Panorama.Server.Lcd;

/// <summary>`unbounded-panorama --test-pattern`: quadrants and labels to check colours and orientation (15 s).</summary>
public static class TestPattern
{
    public static int Run()
    {
        using var screen = new ThermalrightBulkScreen();
        if (!screen.Connect())
        {
            Console.Error.WriteLine("screen: " + screen.LastError);
            return 1;
        }
        Console.WriteLine($"{screen.Name}: PM {screen.ProductMode}, sub {screen.SubModel}, firmware \"{screen.Firmware}\"");
        using var typeface = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "Roboto-Bold.ttf"));
        using var bitmap = new SKBitmap(new SKImageInfo(screen.Width, screen.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        int w = screen.Width, h = screen.Height;
        using (var p = new SKPaint())
        {
            p.Color = SKColors.Red; canvas.DrawRect(0, 0, w / 2f, h / 2f, p);
            p.Color = SKColors.Lime; canvas.DrawRect(w / 2f, 0, w / 2f, h / 2f, p);
            p.Color = SKColors.Blue; canvas.DrawRect(0, h / 2f, w / 2f, h / 2f, p);
            p.Color = SKColors.White; canvas.DrawRect(w / 2f, h / 2f, w / 2f, h / 2f, p);
        }
        using var font = new SKFont(typeface, 34);
        using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText("TOP", w / 2f, 40, SKTextAlign.Center, font, text);
        canvas.DrawText("R", w / 4f, h / 4f + 12, SKTextAlign.Center, font, text);
        canvas.DrawText("G", 3 * w / 4f, h / 4f + 12, SKTextAlign.Center, font, text);
        canvas.DrawText("B", w / 4f, 3 * h / 4f + 12, SKTextAlign.Center, font, text);
        canvas.DrawText("W", 3 * w / 4f, 3 * h / 4f + 12, SKTextAlign.Center, font, text);
        canvas.Flush();
        var pixels = bitmap.GetPixelSpan().ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var frames = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            screen.Send(pixels);
            frames++;
            Thread.Sleep(100);
        }
        Console.WriteLine($"sent {frames} frames in {clock.Elapsed.TotalSeconds:0.0} s");
        return 0;
    }
}
