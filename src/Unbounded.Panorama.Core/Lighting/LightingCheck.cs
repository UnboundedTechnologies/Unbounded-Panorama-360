namespace Unbounded.Panorama.Lighting;

/// <summary>What one device should report once OpenRGB has applied the commands sent to it.</summary>
public sealed record DeviceExpectation(int Device, int? Mode, RgbColor[]? Leds);

/// <summary>
/// Checks that OpenRGB really applied what was sent. OpenRGB 1.0 silently drops every command for
/// a device it detected again while running (a keyboard plugged back in, a rescan): verified in its
/// source (NetworkServer::ControllerListenThread passes the internal controller ID where an index is
/// expected, fixed in 1.1) and on hardware. Reading the device back is the only way to notice.
/// </summary>
public static class LightingCheck
{
    /// <summary>The last mode and the last LED colours sent to each device.</summary>
    public static IReadOnlyList<DeviceExpectation> Expected(IEnumerable<RgbCommand> commands)
    {
        var expected = new Dictionary<int, DeviceExpectation>();
        foreach (var command in commands)
        {
            var current = expected.GetValueOrDefault(command.Device) ?? new DeviceExpectation(command.Device, null, null);
            expected[command.Device] = command switch
            {
                SetModeCommand m => current with { Mode = m.Mode },
                SetLedsCommand l => current with { Leds = l.Colors },
                _ => current,
            };
        }
        return [.. expected.Values.Where(e => e.Mode is not null || e.Leds is not null)];
    }

    /// <summary>True when the device reports the expected mode and LED colours.</summary>
    public static bool Matches(DeviceExpectation expected, int activeMode, IReadOnlyList<RgbColor> colors)
    {
        if (expected.Mode is { } mode && mode != activeMode)
            return false;
        return expected.Leds is not { } leds || leds.SequenceEqual(colors);
    }
}
