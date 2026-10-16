using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unbounded.Panorama.Lighting;

/// <summary>One look: an effect with a colour, speed and brightness.</summary>
public sealed class LightSetting
{
    /// <summary>"Static", "Off", or a device effect name such as "Breathing" or "Rainbow".</summary>
    public string Effect { get; set; } = LightEffects.Static;

    public string Color { get; set; } = "#00E5FF";

    /// <summary>0..100 %, mapped onto each device's own speed range.</summary>
    public int Speed { get; set; } = 50;

    /// <summary>0..100 %, applied by dimming the colour (works on every device).</summary>
    public int Brightness { get; set; } = 100;

    public LightSetting Clone() => new() { Effect = Effect, Color = Color, Speed = Speed, Brightness = Brightness };
}

/// <summary>A zone of a device (a RAM stick, an ARGB header, the board's own LEDs).</summary>
public sealed class ZoneLighting
{
    public string Zone { get; set; } = "";

    /// <summary>Its own colour for Static; null follows the device colour.</summary>
    public string? Color { get; set; }

    /// <summary>LEDs connected (resizable zones such as ARGB headers only); null keeps the current count.</summary>
    public int? LedCount { get; set; }

    /// <summary>The user's name for it, e.g. "Fan hub, 8 fans" on an ARGB header; null shows OpenRGB's name.</summary>
    public string? Label { get; set; }
}

/// <summary>What one device should show.</summary>
public sealed class DeviceLighting
{
    /// <summary>Stable identity: name + hardware location (two identical RAM sticks differ by address).</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>False leaves the device alone (for example a keyboard set up in its own app).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Follows the sync look while sync is on.</summary>
    public bool InSync { get; set; } = true;

    /// <summary>
    /// Its own look was set by the user. Until then the device is left as it is (unless it follows sync),
    /// so changing one device never recolours the others with defaults.
    /// </summary>
    public bool Touched { get; set; }

    public LightSetting Setting { get; set; } = new();
    public List<ZoneLighting> Zones { get; set; } = [];

    public ZoneLighting Zone(string name)
    {
        var zone = Zones.FirstOrDefault(z => z.Zone == name);
        if (zone is null)
        {
            zone = new ZoneLighting { Zone = name };
            Zones.Add(zone);
        }
        return zone;
    }
}

/// <summary>Every device's lighting; saved as the current state and as named profiles.</summary>
public sealed class LightingProfile
{
    /// <summary>One look for every device that is in sync.</summary>
    public bool Sync { get; set; }
    public LightSetting SyncSetting { get; set; } = new();
    public List<DeviceLighting> Devices { get; set; } = [];

    public DeviceLighting Device(string key, string name)
    {
        var device = Devices.FirstOrDefault(d => d.Key == key);
        if (device is null)
        {
            device = new DeviceLighting { Key = key, Name = name };
            Devices.Add(device);
        }
        return device;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static LightingProfile FromJson(string json) => JsonSerializer.Deserialize<LightingProfile>(json, Json) ?? new LightingProfile();

    public LightingProfile Clone() => FromJson(ToJson());
}

public static class LightEffects
{
    public const string Static = "Static";
    public const string Off = "Off";

    /// <summary>The per-LED mode OpenRGB uses for custom colours; an implementation detail, not shown.</summary>
    public const string Direct = "Direct";

    /// <summary>Usual order in menus; device-specific effects follow.</summary>
    public static readonly string[] Preferred = ["Static", "Breathing", "Flashing", "Spectrum Cycle", "Rainbow", "Chase", "Chase Fade", "Random Flicker", "Off"];

    /// <summary>Effects to offer for a set of devices: Static and Off always, then what at least one device has.</summary>
    public static IReadOnlyList<string> For(IEnumerable<RgbDevice> devices)
    {
        var names = devices.SelectMany(d => d.Modes).Select(m => m.Name)
            .Where(n => !n.Equals(Direct, StringComparison.OrdinalIgnoreCase) && !n.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            .Append(Static).Append(Off)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return [.. names.OrderBy(n => Array.FindIndex(Preferred, p => p.Equals(n, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : 100).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }
}

// ------------------------------------------------------------------ what OpenRGB reports

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);

    /// <summary>"#RRGGBB" (or "RRGGBB"); anything else is black.</summary>
    public static RgbColor Parse(string? hex)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        return s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Black;
    }

    public RgbColor Dim(int percent)
    {
        var f = Math.Clamp(percent, 0, 100) / 100.0;
        return new RgbColor((byte)Math.Round(R * f), (byte)Math.Round(G * f), (byte)Math.Round(B * f));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <param name="CanSave">The device can store this effect in its own memory (OpenRGB MODE_FLAG_MANUAL_SAVE, 1 &lt;&lt; 8).</param>
public sealed record RgbMode(int Index, string Name, bool PerLedColor, bool ModeColor, int ColorMin, int ColorMax, bool HasSpeed, uint SpeedMin, uint SpeedMax, bool CanSave = false);

public sealed record RgbZone(int Index, string Name, int LedCount, int LedsMin, int LedsMax)
{
    /// <summary>An ARGB header: the number of LEDs connected must be told.</summary>
    public bool Resizable => LedsMin != LedsMax;
}

public sealed record RgbDevice(int Index, string Key, string Name, string Type, string Vendor, IReadOnlyList<RgbMode> Modes, IReadOnlyList<RgbZone> Zones, int ActiveMode)
{
    public int LedCount => Zones.Sum(z => z.LedCount);

    public RgbMode? Mode(string name) => Modes.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

// ------------------------------------------------------------------ what to send

public abstract record RgbCommand(int Device);

/// <summary>Sets how many LEDs a resizable zone (ARGB header) drives.</summary>
public sealed record ResizeZoneCommand(int Device, int Zone, int Size) : RgbCommand(Device);

public sealed record SetModeCommand(int Device, int Mode, uint? Speed, RgbColor[] Colors) : RgbCommand(Device);

/// <summary>Every LED of the device, in order (zones one after another).</summary>
public sealed record SetLedsCommand(int Device, RgbColor[] Colors) : RgbCommand(Device);
