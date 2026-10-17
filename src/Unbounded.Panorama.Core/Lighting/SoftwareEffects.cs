namespace Unbounded.Panorama.Lighting;

/// <summary>
/// Effects drawn by the app, frame by frame, for devices that only take per-LED colours (for example
/// a Wooting keyboard through OpenRGB): the same effect names as the hardware ones, so sync looks alike.
/// </summary>
public static class SoftwareEffects
{
    /// <summary>Frames per second the app sends while animating.</summary>
    public const int FramesPerSecond = 25;

    private static readonly string[] Animated = ["Rainbow", "Spectrum Cycle", "Breathing", "Flashing"];

    /// <summary>Whether the app can draw this effect itself.</summary>
    public static bool CanAnimate(string effect) => Animated.Contains(effect, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A device needs the app to animate when it shows an animated effect it does not have itself,
    /// but can take per-LED colours.
    /// </summary>
    public static bool Needed(RgbDevice device, string effect) =>
        CanAnimate(effect) && device.Mode(effect) is null && device.Modes.Any(m => m.PerLedColor) && device.LedCount > 0;

    /// <summary>One cycle of the effect: 10 s at 0 % speed down to 1 s at 100 %.</summary>
    public static double Period(int speedPercent) => 10.0 - 9.0 * Math.Clamp(speedPercent, 0, 100) / 100.0;

    /// <summary>The colours of every LED at <paramref name="seconds"/>.</summary>
    public static RgbColor[] Frame(string effect, RgbColor color, int speedPercent, int brightnessPercent, int leds, double seconds)
    {
        var phase = seconds / Period(speedPercent) % 1.0;
        var frame = new RgbColor[leds];
        for (var i = 0; i < leds; i++)
        {
            RgbColor c;
            if (effect.Equals("Rainbow", StringComparison.OrdinalIgnoreCase))
            {
                // A wave of the whole spectrum across the LEDs, moving with time.
                c = Hue((phase + (double)i / Math.Max(1, leds)) % 1.0);
            }
            else if (effect.Equals("Spectrum Cycle", StringComparison.OrdinalIgnoreCase))
            {
                c = Hue(phase);
            }
            else if (effect.Equals("Breathing", StringComparison.OrdinalIgnoreCase))
            {
                var level = (int)Math.Round((1 - Math.Cos(phase * 2 * Math.PI)) / 2 * 100);
                c = color.Dim(level);
            }
            else if (effect.Equals("Flashing", StringComparison.OrdinalIgnoreCase))
            {
                c = phase < 0.5 ? color : RgbColor.Black;
            }
            else
            {
                c = color;
            }
            frame[i] = c.Dim(brightnessPercent);
        }
        return frame;
    }

    /// <summary>Fully saturated colour at a hue (0..1).</summary>
    public static RgbColor Hue(double hue)
    {
        var h = (hue % 1.0 + 1.0) % 1.0 * 6.0;
        var x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        return new RgbColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}
