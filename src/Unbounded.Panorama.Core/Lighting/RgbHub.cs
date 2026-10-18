using OpenRGB.NET;

namespace Unbounded.Panorama.Lighting;

/// <summary>
/// The link to OpenRGB's background service (127.0.0.1:6742), which detects and drives RGB devices
/// of every brand it supports (motherboards, RAM, GPUs, fans, keyboards…). OpenRGB runs as a
/// Windows service with the rights SMBus devices need, so this app only talks to it locally.
/// All calls are serialised: the client is not thread-safe.
/// </summary>
public sealed class RgbHub : IDisposable
{
    public const int Port = 6742;

    private readonly object _gate = new();
    private OpenRgbClient? _client;
    private readonly OpenRgbModeWriter _modes = new();
    private IReadOnlyList<RgbDevice> _devices = [];

    public IReadOnlyList<RgbDevice> Devices
    {
        get
        {
            lock (_gate)
                return _devices;
        }
    }

    public bool Connected
    {
        get
        {
            lock (_gate)
                return _client?.Connected == true;
        }
    }

    public string? LastError { get; private set; }

    /// <summary>Connects (if needed) and reads every device. False when the service is not reachable.</summary>
    public bool Refresh()
    {
        lock (_gate)
        {
            try
            {
                if (_client is not { Connected: true })
                {
                    _client?.Dispose();
                    _client = new OpenRgbClient(name: "Unbounded Panorama 360", autoConnect: true, timeoutMs: 2000);
                }
                _devices = Read(_client);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                _client?.Dispose();
                _client = null;
                _devices = [];
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Sends commands in order. LED counts changed: devices are read again afterwards.</summary>
    public void Execute(IEnumerable<RgbCommand> commands)
    {
        lock (_gate)
        {
            if (_client is not { Connected: true } client)
                throw new InvalidOperationException("OpenRGB is not connected.");
            var resized = false;
            foreach (var command in commands)
            {
                switch (command)
                {
                    case ResizeZoneCommand r:
                        client.ResizeZone(r.Device, r.Zone, r.Size);
                        resized = true;
                        break;
                    case SetModeCommand m:
                        // Not client.UpdateMode: OpenRGB 1.0 ignores most of those (see OpenRgbModeWriter).
                        _modes.Update(m.Device, client.GetControllerData(m.Device).Modes[m.Mode], m.Speed, m.Colors);
                        break;
                    case SetLedsCommand l:
                        client.UpdateLeds(l.Device, l.Colors.Select(ToColor).ToArray());
                        break;
                }
            }
            if (resized)
                _devices = Read(client);
        }
    }

    /// <summary>
    /// Reads the devices back until each reports what was sent (OpenRGB applies commands on its own
    /// threads, a little later). Returns the names of the devices that never did within <paramref name="wait"/>.
    /// </summary>
    public IReadOnlyList<string> Ignored(IReadOnlyList<DeviceExpectation> expected, TimeSpan wait)
    {
        var pending = expected.ToList();
        var names = new Dictionary<int, string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if (_client is not { Connected: true } client)
                    throw new InvalidOperationException("OpenRGB is not connected.");
                pending.RemoveAll(e =>
                {
                    var device = client.GetControllerData(e.Device);
                    names[e.Device] = device.Name;
                    return LightingCheck.Matches(e, device.ActiveModeIndex, [.. device.Colors.Select(c => new RgbColor(c.R, c.G, c.B))]);
                });
            }
            if (pending.Count == 0 || clock.Elapsed >= wait)
                return [.. pending.Select(e => names[e.Device])];
            Thread.Sleep(200);
        }
    }

    /// <summary>Drops both connections (the service is being restarted); the next Refresh connects again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
            _devices = [];
            _modes.Reset();
        }
    }

    /// <summary>
    /// Stores each device's current effect in its own memory, so it shows even before the app starts
    /// (per-LED colours and devices without storage keep their previous saved effect). Writes
    /// device flash: only on request, never per edit.
    /// </summary>
    public int SaveToDevices(IEnumerable<int> deviceIndexes)
    {
        lock (_gate)
        {
            if (_client is not { Connected: true } client)
                throw new InvalidOperationException("OpenRGB is not connected.");
            var saved = 0;
            foreach (var index in deviceIndexes)
            {
                var device = client.GetControllerData(index);
                var active = device.Modes[device.ActiveModeIndex];
                // Only devices that declare it can store an effect (verified: the Aura board does, ENE RAM and GPUs do not).
                if (((int)active.Flags & ManualSaveFlag) == 0 || active.Name.Equals(LightEffects.Direct, StringComparison.OrdinalIgnoreCase))
                    continue;
                _modes.Save(index, active);
                saved++;
            }
            return saved;
        }
    }

    /// <summary>How many duplicate SMBus paths to an already listed device were left out at the last read.</summary>
    public int AliasesIgnored { get; private set; }

    /// <summary>Every device once: second SMBus paths to the same chip are left out (see <see cref="SmbusAliases"/>).</summary>
    private List<RgbDevice> Read(OpenRgbClient client)
    {
        var all = client.GetAllControllerData();
        var aliases = SmbusAliases([.. all.Select(d => new SmbusDevice(d.Index, d.Name, d.Location, d.Version ?? "", d.Leds.Length))]);
        AliasesIgnored = aliases.Count;
        return [.. all.Where(d => !aliases.Contains(d.Index)).Select(Map)];
    }

    public sealed record SmbusDevice(int Index, string Name, string Location, string Version, int Leds);

    private static readonly System.Text.RegularExpressions.Regex SmbusLocation =
        new(@"^I2C: (?<bus>.+?) (?<port>\d+), address (?<address>0x[0-9A-Fa-f]+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Devices reached a second time through another port of the same SMBus controller. AMD's PIIX4
    /// SMBus has several ports; OpenRGB 1.0 can list the same RAM sticks on two of them (verified on
    /// an X870 board: 4 "sticks" for 2, and a colour sent only to port 1 lit both real sticks). Writing
    /// both at once collides on the bus and leaves the RAM dark, so only the lowest port is kept:
    /// same chip, same address, same firmware and LED count on another port is the same device.
    /// </summary>
    public static IReadOnlySet<int> SmbusAliases(IReadOnlyList<SmbusDevice> devices)
    {
        var aliases = new HashSet<int>();
        var seen = new Dictionary<string, (int Port, int Index)>();
        foreach (var d in devices
            .Select(d => (Device: d, Match: SmbusLocation.Match(d.Location)))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture)))
        {
            var port = int.Parse(d.Match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var key = $"{d.Device.Name}|{d.Match.Groups["bus"].Value}|{d.Match.Groups["address"].Value.ToLowerInvariant()}|{d.Device.Version}|{d.Device.Leds}";
            if (seen.TryGetValue(key, out var first))
            {
                if (first.Port != port)
                    aliases.Add(d.Device.Index);
            }
            else
            {
                seen[key] = (port, d.Device.Index);
            }
        }
        return aliases;
    }

    /// <summary>OpenRGB MODE_FLAG_MANUAL_SAVE: the mode can be stored on the device (not named by OpenRGB.NET 3.1.1).</summary>
    private const int ManualSaveFlag = 1 << 8;

    private static Color ToColor(RgbColor c) => new(c.R, c.G, c.B);

    private static RgbDevice Map(Device d) => new(
        d.Index,
        $"{d.Name}|{d.Location}",
        d.Name,
        d.Type.ToString(),
        d.Vendor ?? "",
        [.. d.Modes.Select(m => new RgbMode(m.Index, m.Name,
            m.Flags.HasFlag(ModeFlags.HasPerLedColor), m.Flags.HasFlag(ModeFlags.HasModeSpecificColor),
            (int)m.ColorMin, (int)m.ColorMax, m.Flags.HasFlag(ModeFlags.HasSpeed), m.SpeedMin, m.SpeedMax,
            ((int)m.Flags & ManualSaveFlag) != 0))],
        [.. d.Zones.Select(z => new RgbZone(z.Index, z.Name, (int)z.LedCount, (int)z.LedsMin, (int)z.LedsMax))],
        d.ActiveModeIndex);

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
            _modes.Dispose();
        }
    }
}
