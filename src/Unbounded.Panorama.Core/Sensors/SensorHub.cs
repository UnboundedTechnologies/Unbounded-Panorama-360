using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace Unbounded.Panorama.Sensors;

public sealed record SensorInfo(string Id, string Hardware, string Name, string Type, string Unit)
{
    public string Display => $"{Hardware} › {Name} ({Type})";
}

/// <summary>
/// LibreHardwareMonitor wrapper. CPU temperature, clocks and power need administrator
/// rights (PawnIO driver); GPU, load and memory work without.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private static readonly SensorType[] Useful =
    [
        SensorType.Temperature, SensorType.Load, SensorType.Clock, SensorType.Power,
        SensorType.Fan, SensorType.Data, SensorType.SmallData, SensorType.Voltage, SensorType.Throughput,
    ];

    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsNetworkEnabled = true,
        IsControllerEnabled = true,
    };

    private readonly object _gate = new();
    private Dictionary<string, float?> _values = new();
    private List<SensorInfo> _catalog = [];
    private Timer? _timer;

    public string CpuName { get; private set; } = "CPU";
    public string GpuName { get; private set; } = "GPU";
    public IReadOnlyList<SensorInfo> Catalog { get { lock (_gate) return _catalog; } }

    public event Action? Updated;

    public void Start()
    {
        _computer.Open();
        Refresh();
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public float? Value(string? sensorId)
    {
        if (sensorId is null)
            return null;
        lock (_gate)
            return _values.TryGetValue(sensorId, out var v) ? v : null;
    }

    public SensorInfo? Info(string? sensorId)
    {
        lock (_gate)
            return _catalog.FirstOrDefault(s => s.Id == sensorId);
    }

    /// <summary>Formats a sensor value; "--" when unavailable.</summary>
    public string Format(string? sensorId, int decimals, bool showUnit, bool fahrenheit)
    {
        var info = Info(sensorId);
        var value = Value(sensorId);
        if (info is null || value is null || float.IsNaN(value.Value))
            return "--";
        var v = value.Value;
        var unit = info.Unit;
        if (info.Type == nameof(SensorType.Temperature) && fahrenheit)
        {
            v = v * 9 / 5 + 32;
            unit = "°F";
        }
        var text = v.ToString("F" + Math.Clamp(decimals, 0, 3), CultureInfo.InvariantCulture);
        return showUnit && unit.Length > 0 ? text + unit : text;
    }

    private bool _refreshing;

    private void Refresh()
    {
        lock (_gate)
        {
            if (_refreshing)
                return;
            _refreshing = true;
        }
        try
        {
            var values = new Dictionary<string, float?>();
            var catalog = new List<SensorInfo>();
            foreach (var hardware in _computer.Hardware)
                Collect(hardware, values, catalog);

            var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            var gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
            lock (_gate)
            {
                _values = values;
                _catalog = catalog;
                if (cpu is not null)
                    CpuName = cpu.Name.Trim();
                if (gpu is not null)
                    GpuName = gpu.Name.Trim();
            }
        }
        catch
        {
            // A sensor read can fail transiently (driver busy, device removed); keep the last values.
        }
        finally
        {
            lock (_gate)
                _refreshing = false;
        }
        Updated?.Invoke();
    }

    private static void Collect(IHardware hardware, Dictionary<string, float?> values, List<SensorInfo> catalog)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (!Useful.Contains(sensor.SensorType))
                continue;
            var id = sensor.Identifier.ToString();
            values[id] = sensor.Value;
            catalog.Add(new SensorInfo(id, hardware.Name, sensor.Name, sensor.SensorType.ToString(), UnitOf(sensor.SensorType)));
        }
        foreach (var sub in hardware.SubHardware)
            Collect(sub, values, catalog);
    }

    private static string UnitOf(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Clock => " MHz",
        SensorType.Power => " W",
        SensorType.Fan => " RPM",
        SensorType.Data => " GB",
        SensorType.SmallData => " MB",
        SensorType.Voltage => " V",
        SensorType.Throughput => " B/s",
        _ => "",
    };

    public void Dispose()
    {
        _timer?.Dispose();
        _computer.Close();
    }
}
