using Unbounded.Panorama.Lighting;
using Xunit;

namespace Unbounded.Panorama.Tests;

public class SoftwareEffectsTests
{
    private static RgbDevice Keyboard() => new(4, "Wooting|hid", "Wooting Two HE", "Keyboard", "Wooting",
        [new(0, "Direct", true, false, 0, 0, false, 0, 0)], [new(0, "Keys", 110, 110, 110)], 0);

    private static RgbDevice Ram() => new(0, "ENE|0x71", "ENE DRAM", "Dram", "ENE",
        [new(0, "Direct", true, false, 0, 0, false, 0, 0), new(6, "Rainbow", false, false, 0, 0, true, 4, 0)], [new(0, "DRAM", 8, 8, 8)], 0);

    [Fact]
    public void Only_devices_without_the_effect_are_animated_by_the_app()
    {
        Assert.True(SoftwareEffects.Needed(Keyboard(), "Rainbow"));
        Assert.False(SoftwareEffects.Needed(Ram(), "Rainbow"));      // the RAM does Rainbow itself
        Assert.False(SoftwareEffects.Needed(Keyboard(), "Static"));  // nothing to animate
        Assert.False(SoftwareEffects.Needed(Keyboard(), "Chase"));   // not drawn by the app: shows the colour
    }

    [Fact]
    public void Rainbow_spreads_the_spectrum_across_the_leds_and_moves()
    {
        var a = SoftwareEffects.Frame("Rainbow", RgbColor.Black, 50, 100, 110, 0);
        Assert.Equal(110, a.Length);
        Assert.True(a.Distinct().Count() > 50, "a wave of many colours");
        var b = SoftwareEffects.Frame("Rainbow", RgbColor.Black, 50, 100, 110, 1.0);
        Assert.NotEqual(a[0], b[0]);
    }

    [Fact]
    public void Spectrum_is_one_colour_at_a_time_and_breathing_fades_the_chosen_colour()
    {
        Assert.Single(SoftwareEffects.Frame("Spectrum Cycle", RgbColor.Black, 50, 100, 20, 2.3).Distinct());
        var period = SoftwareEffects.Period(50);
        var white = new RgbColor(255, 255, 255);
        Assert.Equal(RgbColor.Black, SoftwareEffects.Frame("Breathing", white, 50, 100, 4, 0)[0]);
        Assert.Equal(white, SoftwareEffects.Frame("Breathing", white, 50, 100, 4, period / 2)[0]);
    }

    [Fact]
    public void Speed_sets_the_period_and_brightness_dims_every_frame()
    {
        Assert.Equal(10.0, SoftwareEffects.Period(0));
        Assert.Equal(1.0, SoftwareEffects.Period(100));
        var frame = SoftwareEffects.Frame("Spectrum Cycle", RgbColor.Black, 50, 50, 1, 0); // hue 0 = red, at half brightness
        Assert.Equal(new RgbColor(128, 0, 0), frame[0]);
    }
}
