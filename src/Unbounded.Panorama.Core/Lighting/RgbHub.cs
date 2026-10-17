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
                _devices = [.. _client.GetAllControllerData().Select(Map)];
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
                _devices = [.. client.GetAllControllerData().Select(Map)];
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
