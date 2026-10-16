using Unbounded.Panorama.Lighting;
using Xunit;

namespace Unbounded.Panorama.Tests;

/// <summary>
/// The planner against the capabilities OpenRGB 1.0 reported on real hardware (read-only probe):
/// ENE RAM / ASUS GPU (per-LED colours, speed 4..0), the ASUS Aura board (one mode colour, can save,
/// three ARGB headers 0..120 LEDs) and a Wooting keyboard (Direct only).
/// </summary>
public class LightingPlannerTests
{
    private static RgbDevice Ene(int index, int leds) => new(index, $"ENE|{index}", "ENE DRAM", "Dram", "ENE",
    [
        new(0, "Direct", true, false, 0, 0, false, 0, 0),
        new(1, "Off", false, false, 0, 0, false, 0, 0),
        new(2, "Static", true, false, 0, 0, false, 0, 0),
        new(3, "Breathing", true, false, 0, 0, true, 4, 0),
        new(5, "Spectrum Cycle", false, false, 0, 0, true, 4, 0),
        new(6, "Rainbow", false, false, 0, 0, true, 4, 0),
    ],
    [new(0, "DRAM", leds, leds, leds)], 0);

    private static RgbDevice Aura(int headerLeds = 0) => new(3, "Aura|usb", "ASUS ROG STRIX X870-A GAMING WIFI", "Motherboard", "ASUS",
    [
        new(0, "Direct", true, false, 0, 0, false, 0, 0),
        new(1, "Off", false, false, 0, 0, false, 0, 0, CanSave: true),
        new(2, "Static", false, true, 1, 1, false, 0, 0, CanSave: true),
        new(3, "Breathing", false, true, 1, 1, false, 0, 0, CanSave: true),
        new(5, "Spectrum Cycle", false, false, 0, 0, false, 0, 0, CanSave: true),
        new(6, "Rainbow", false, false, 0, 0, false, 0, 0, CanSave: true),
    ],
    [
        new(0, "Aura Mainboard", 3, 3, 3),
        new(1, "Addressable RGB Header 1", headerLeds, 0, 120),
        new(2, "Addressable RGB Header 2", 0, 0, 120),
        new(3, "Addressable RGB Header 3", 0, 0, 120),
    ], 0);

    private static RgbDevice Keyboard() => new(4, "Wooting|hid", "Wooting Two HE", "Keyboard", "Wooting",
        [new(0, "Direct", true, false, 0, 0, false, 0, 0)], [new(0, "Keys", 110, 110, 110)], 0);

    private static DeviceLighting Look(string effect, string color = "#00E5FF", int speed = 50, int brightness = 100) =>
        new() { Touched = true, Setting = new LightSetting { Effect = effect, Color = color, Speed = speed, Brightness = brightness } };

    [Fact]
    public void Static_on_ram_sets_static_then_every_led()
    {
        var commands = LightingPlanner.Plan(Ene(0, 8), Look("Static", "#FF0000"), null);
        Assert.Collection(commands,
            c => Assert.Equal(new SetModeCommand(0, 2, null, []).Mode, Assert.IsType<SetModeCommand>(c).Mode),
            c =>
            {
                var leds = Assert.IsType<SetLedsCommand>(c).Colors;
                Assert.Equal(8, leds.Length);
                Assert.All(leds, l => Assert.Equal(new RgbColor(255, 0, 0), l));
            });
    }

    [Fact]
    public void Static_on_the_board_sends_one_mode_colour()
    {
        var command = Assert.Single(LightingPlanner.Plan(Aura(), Look("Static", "#00FF80"), null));
        var mode = Assert.IsType<SetModeCommand>(command);
        Assert.Equal(2, mode.Mode);
        Assert.Equal([new RgbColor(0, 255, 128)], mode.Colors);
    }

    [Fact]
    public void Speed_maps_onto_a_downward_counting_range()
    {
        // ENE counts 4 (slowest) .. 0 (fastest).
        Assert.Equal(4u, ((SetModeCommand)LightingPlanner.Plan(Ene(0, 8), Look("Rainbow", speed: 0), null)[0]).Speed);
        Assert.Equal(0u, ((SetModeCommand)LightingPlanner.Plan(Ene(0, 8), Look("Rainbow", speed: 100), null)[0]).Speed);
        Assert.Equal(2u, ((SetModeCommand)LightingPlanner.Plan(Ene(0, 8), Look("Rainbow", speed: 50), null)[0]).Speed);
    }

    [Fact]
    public void Brightness_dims_the_colour()
    {
        var mode = (SetModeCommand)LightingPlanner.Plan(Aura(), Look("Static", "#C86432", brightness: 50), null)[0];
        Assert.Equal(new RgbColor(100, 50, 25), mode.Colors[0]);
    }

    [Fact]
    public void An_effect_the_device_lacks_shows_the_colour_instead()
    {
        // The keyboard only has Direct: Breathing becomes its colour on every key.
        var commands = LightingPlanner.Plan(Keyboard(), Look("Breathing", "#FFFFFF"), null);
        Assert.Equal(0, Assert.IsType<SetModeCommand>(commands[0]).Mode);
        var leds = Assert.IsType<SetLedsCommand>(commands[1]).Colors;
        Assert.Equal(110, leds.Length);
        Assert.All(leds, l => Assert.Equal(new RgbColor(255, 255, 255), l));
    }

    [Fact]
    public void Off_uses_the_off_mode_or_black()
    {
        Assert.Equal(1, ((SetModeCommand)Assert.Single(LightingPlanner.Plan(Aura(), Look("Off"), null))).Mode);
        var keyboard = LightingPlanner.Plan(Keyboard(), Look("Off"), null);
        Assert.All(((SetLedsCommand)keyboard[1]).Colors, l => Assert.Equal(RgbColor.Black, l));
    }

    [Fact]
    public void Argb_header_is_resized_first_and_lit_with_its_zone_colour()
    {
        var look = Look("Static", "#0000FF");
        look.Zone("Addressable RGB Header 1").LedCount = 24;
        look.Zone("Addressable RGB Header 1").Color = "#FF0000";
        var commands = LightingPlanner.Plan(Aura(), look, null);

        Assert.Equal(new ResizeZoneCommand(3, 1, 24), commands[0]);
        // Two colours on one device: per-LED (Direct), board LEDs blue then 24 header LEDs red.
        Assert.Equal(0, Assert.IsType<SetModeCommand>(commands[1]).Mode);
        var leds = Assert.IsType<SetLedsCommand>(commands[2]).Colors;
        Assert.Equal(3 + 24, leds.Length);
        Assert.All(leds[..3], l => Assert.Equal(new RgbColor(0, 0, 255), l));
        Assert.All(leds[3..], l => Assert.Equal(new RgbColor(255, 0, 0), l));
    }

    [Fact]
    public void Led_counts_are_kept_within_the_header_limits_and_unchanged_counts_are_not_resent()
    {
        var look = Look("Static");
        look.Zone("Addressable RGB Header 1").LedCount = 500;
        Assert.Contains(new ResizeZoneCommand(3, 1, 120), LightingPlanner.Plan(Aura(), look, null));
        look.Zone("Addressable RGB Header 1").LedCount = 24;
        Assert.DoesNotContain(LightingPlanner.Plan(Aura(headerLeds: 24), look, null), c => c is ResizeZoneCommand);
    }

    [Fact]
    public void Sync_overrides_each_device_look_but_not_devices_left_out()
    {
        var sync = new LightSetting { Effect = "Static", Color = "#FFFFFF" };
        var own = Look("Static", "#FF0000");
        var synced = (SetLedsCommand)LightingPlanner.Plan(Ene(0, 8), own, sync)[1];
        Assert.All(synced.Colors, l => Assert.Equal(new RgbColor(255, 255, 255), l));

        own.InSync = false;
        var separate = (SetLedsCommand)LightingPlanner.Plan(Ene(0, 8), own, sync)[1];
        Assert.All(separate.Colors, l => Assert.Equal(new RgbColor(255, 0, 0), l));

        own.Enabled = false;
        Assert.Empty(LightingPlanner.Plan(Ene(0, 8), own, sync));
    }

    [Fact]
    public void A_device_never_set_up_is_left_alone_unless_it_follows_sync()
    {
        // Changing one device must not recolour the others with defaults.
        var untouched = new DeviceLighting();
        Assert.Empty(LightingPlanner.Plan(Keyboard(), untouched, null));
        Assert.Empty(LightingPlanner.Plan(Keyboard(), null, null));
        // Sync, when on, covers every device that follows it.
        Assert.NotEmpty(LightingPlanner.Plan(Keyboard(), untouched, new LightSetting { Color = "#FFFFFF" }));
        untouched.InSync = false;
        Assert.Empty(LightingPlanner.Plan(Keyboard(), untouched, new LightSetting()));
    }

    [Fact]
    public void Effects_offered_are_static_and_off_plus_what_devices_have_without_direct()
    {
        var effects = LightEffects.For([Ene(0, 8), Aura(), Keyboard()]);
        Assert.Equal("Static", effects[0]);
        Assert.Contains("Rainbow", effects);
        Assert.Contains("Off", effects);
        Assert.DoesNotContain("Direct", effects);
    }

    [Fact]
    public void Profiles_survive_a_save_and_load()
    {
        var profile = new LightingProfile { Sync = true, SyncSetting = new LightSetting { Effect = "Rainbow", Speed = 80 } };
        var device = profile.Device("Aura|usb", "Board");
        device.InSync = false;
        device.Zone("Addressable RGB Header 2").LedCount = 16;
        var back = LightingProfile.FromJson(profile.ToJson());
        Assert.True(back.Sync);
        Assert.Equal("Rainbow", back.SyncSetting.Effect);
        Assert.Equal(80, back.SyncSetting.Speed);
        Assert.False(back.Devices[0].InSync);
        Assert.Equal(16, back.Devices[0].Zones[0].LedCount);
    }
}
