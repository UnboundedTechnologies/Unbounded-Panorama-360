using Unbounded.Panorama.Lighting;
using Xunit;

namespace Unbounded.Panorama.Tests;

public class LightingCheckTests
{
    private static readonly RgbColor White = new(255, 255, 255);
    private static readonly RgbColor Black = new(0, 0, 0);

    [Fact]
    public void The_last_mode_and_colours_sent_to_each_device_are_expected()
    {
        var expected = LightingCheck.Expected(
        [
            new ResizeZoneCommand(3, 1, 36),
            new SetModeCommand(3, 1, null, [White]),
            new SetModeCommand(4, 0, null, []),
            new SetLedsCommand(4, [Black, Black]),
            new SetLedsCommand(4, [White, White]),
        ]);

        Assert.Equal(2, expected.Count);
        var board = expected.Single(e => e.Device == 3);
        Assert.Equal(1, board.Mode);
        Assert.Null(board.Leds);
        var keyboard = expected.Single(e => e.Device == 4);
        Assert.Equal(0, keyboard.Mode);
        Assert.Equal([White, White], keyboard.Leds!);
    }

    [Fact]
    public void A_resize_alone_expects_nothing()
    {
        Assert.Empty(LightingCheck.Expected([new ResizeZoneCommand(3, 1, 36)]));
    }

    [Fact]
    public void A_device_that_kept_its_old_state_does_not_match()
    {
        // What OpenRGB 1.0 reported for the keyboard plugged back in: still black after white was sent.
        var expected = new DeviceExpectation(4, 0, [White, White]);
        Assert.False(LightingCheck.Matches(expected, 0, [Black, Black]));
        Assert.True(LightingCheck.Matches(expected, 0, [White, White]));
    }

    [Fact]
    public void The_mode_must_match()
    {
        // The board after a rescan: left in Direct although Static (index 1) was sent.
        var expected = new DeviceExpectation(3, 1, null);
        Assert.False(LightingCheck.Matches(expected, 0, [Black]));
        Assert.True(LightingCheck.Matches(expected, 1, [Black]));
    }
}
