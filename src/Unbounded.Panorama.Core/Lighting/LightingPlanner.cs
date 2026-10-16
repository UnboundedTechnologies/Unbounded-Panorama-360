namespace Unbounded.Panorama.Lighting;

/// <summary>
/// Turns lighting settings into the commands for one device, using only what that device reports
/// (its effects, colour and speed support, zones). No hardware access: tested on its own.
/// </summary>
public static class LightingPlanner
{
    /// <param name="sync">The sync look when sync is on (devices in sync follow it), otherwise null.</param>
    public static IReadOnlyList<RgbCommand> Plan(RgbDevice device, DeviceLighting? settings, LightSetting? sync)
    {
        settings ??= new DeviceLighting { Key = device.Key, Name = device.Name };
        var synced = sync is not null && settings.InSync;
        // Left out, or never set up by the user (and not following sync): leave it exactly as it is.
        if (!settings.Enabled || (!synced && !settings.Touched))
            return [];

        var commands = new List<RgbCommand>();

        // ARGB headers first: the LED count decides how many colours the device takes.
        var counts = new int[device.Zones.Count];
        for (var i = 0; i < device.Zones.Count; i++)
        {
            var zone = device.Zones[i];
            counts[i] = zone.LedCount;
            var wanted = settings.Zones.FirstOrDefault(z => z.Zone == zone.Name)?.LedCount;
            if (zone.Resizable && wanted is { } size)
            {
                size = Math.Clamp(size, zone.LedsMin, zone.LedsMax);
                if (size != zone.LedCount)
                    commands.Add(new ResizeZoneCommand(device.Index, zone.Index, size));
                counts[i] = size;
            }
        }

        var look = synced ? sync! : settings.Setting;
        var color = RgbColor.Parse(look.Color).Dim(look.Brightness);

        // Per-zone colours apply to Static when the device has its own look (not while synced).
        RgbColor ZoneColor(int zone)
        {
            if (synced)
                return color;
            var own = settings.Zones.FirstOrDefault(z => z.Zone == device.Zones[zone].Name)?.Color;
            return own is null ? color : RgbColor.Parse(own).Dim(look.Brightness);
        }

        RgbColor[] Leds(Func<int, RgbColor> colourOfZone)
        {
            var leds = new RgbColor[counts.Sum()];
            var at = 0;
            for (var z = 0; z < counts.Length; z++)
            {
                var c = colourOfZone(z);
                for (var n = 0; n < counts[z]; n++)
                    leds[at++] = c;
            }
            return leds;
        }

        void Direct(Func<int, RgbColor> colourOfZone)
        {
            // Direct (or another per-LED mode): every LED gets its colour.
            var direct = device.Mode(LightEffects.Direct) ?? device.Modes.FirstOrDefault(m => m.PerLedColor && !m.HasSpeed);
            if (direct is not null)
                commands.Add(new SetModeCommand(device.Index, direct.Index, null, []));
            if (counts.Sum() > 0)
                commands.Add(new SetLedsCommand(device.Index, Leds(colourOfZone)));
        }

        if (look.Effect.Equals(LightEffects.Off, StringComparison.OrdinalIgnoreCase))
        {
            if (device.Mode(LightEffects.Off) is { } off)
                commands.Add(new SetModeCommand(device.Index, off.Index, null, []));
            else
                Direct(_ => RgbColor.Black);
            return commands;
        }

        var perZone = !synced && device.Zones.Count > 1 && Enumerable.Range(0, device.Zones.Count).Select(ZoneColor).Distinct().Count() > 1;
        var mode = look.Effect.Equals(LightEffects.Static, StringComparison.OrdinalIgnoreCase) && perZone
            ? null // different colours per zone: only per-LED colour can show them
            : device.Mode(look.Effect);

        if (mode is null || (!mode.ModeColor && !mode.PerLedColor && look.Effect.Equals(LightEffects.Static, StringComparison.OrdinalIgnoreCase)))
        {
            // The device lacks this effect (or Static would ignore the colour): show the colour itself.
            Direct(ZoneColor);
            return commands;
        }

        uint? speed = null;
        if (mode.HasSpeed)
        {
            // Some devices count speed downwards (min > max); both map 0 % to min and 100 % to max.
            var p = Math.Clamp(look.Speed, 0, 100) / 100.0;
            speed = (uint)Math.Round(mode.SpeedMin + (mode.SpeedMax - (double)mode.SpeedMin) * p);
        }
        var colours = mode.ModeColor && mode.ColorMax > 0
            ? Enumerable.Repeat(color, Math.Max(1, mode.ColorMin)).ToArray()
            : [];
        commands.Add(new SetModeCommand(device.Index, mode.Index, speed, colours));
        if (mode.PerLedColor && counts.Sum() > 0)
            commands.Add(new SetLedsCommand(device.Index, Leds(ZoneColor)));
        return commands;
    }
}
