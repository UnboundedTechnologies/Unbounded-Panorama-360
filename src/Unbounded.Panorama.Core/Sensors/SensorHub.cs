using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace Unbounded.Panorama.Sensors;

public sealed record SensorInfo(string Id, string Category, string Hardware, string Name, string Type, string Unit)
{
    public string Display => $"{Category} {Hardware} › {Name} ({Type})";

    /// <summary>Group header in the picker, e.g. "CPU · AMD Ryzen 7 9800X3D".</summary>
    public string Group => $"{Category} · {Hardware}";

    public int CategoryOrder => Array.IndexOf(SensorHub.CategoryOrder, Category) is var i and >= 0 ? i : 99;
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

    /// <summary>How long opening the hardware and the first full reading took (startup diagnostics).</summary>
    public (TimeSpan Open, TimeSpan FirstRead) StartTimings { get; private set; }

    public void Start()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _computer.Open();
        var open = sw.Elapsed;
        Refresh();
        StartTimings = (open, sw.Elapsed - open);
        lock (_gate)
        {
            if (_disposed)
                return;
            _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Opens the hardware on a worker thread (it takes about a second, more with the CPU driver),
    /// so the window can appear immediately. <see cref="Updated"/> fires once values exist.
    /// </summary>
    public Task StartInBackground() => Task.Run(Start);

    private bool _disposed;

    public float? Value(string? sensorId)
    {
        sensorId = Resolve(sensorId);
        if (sensorId is null)
            return null;
        lock (_gate)
            return _values.TryGetValue(sensorId, out var v) ? v : null;
    }

    public SensorInfo? Info(string? sensorId)
    {
        sensorId = Resolve(sensorId);
        lock (_gate)
            return _catalog.FirstOrDefault(s => s.Id == sensorId);
    }

    public const string RolePrefix = "role:";

    /// <summary>
    /// Portable sensor references for templates ("role:cpu-temp") are matched to this machine's
    /// sensors; plain ids pass through.
    /// </summary>
    public string? Resolve(string? sensorId)
    {
        if (sensorId is null || !sensorId.StartsWith(RolePrefix, StringComparison.Ordinal))
            return sensorId;
        var role = sensorId[RolePrefix.Length..];
        List<SensorInfo> catalog;
        lock (_gate)
            catalog = _catalog;

        SensorInfo? Pick(string category, string type, params string[] preferredNames) =>
            preferredNames.Select(n => catalog.FirstOrDefault(s => s.Category == category && s.Type == type && s.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(s => s is not null)
            ?? catalog.FirstOrDefault(s => s.Category == category && s.Type == type);

        var hit = role switch
        {
            "cpu-temp" => Pick("CPU", "Temperature", "Tctl", "Package", "CPU", "Core"),
            "cpu-load" => Pick("CPU", "Load", "CPU Total", "Total"),
            "cpu-power" => Pick("CPU", "Power", "Package", "CPU"),
            "cpu-clock" => Pick("CPU", "Clock", "Cores (Average)", "Average", "Core #1"),
            "gpu-temp" => Pick("GPU", "Temperature", "GPU Core", "Core", "Hot Spot"),
            "gpu-load" => Pick("GPU", "Load", "GPU Core", "Core", "D3D 3D"),
            "gpu-power" => Pick("GPU", "Power", "GPU Package", "Package", "Board"),
            "gpu-clock" => Pick("GPU", "Clock", "GPU Core", "Core"),
            // Physical RAM ("Total Memory"), not the page-file-inclusive "Virtual Memory".
            "ram-load" => catalog.FirstOrDefault(s => s.Category == "Memory" && s.Type == "Load" && s.Hardware.Contains("Total", StringComparison.OrdinalIgnoreCase))
                          ?? Pick("Memory", "Load", "Memory"),
            "ram-used" => catalog.FirstOrDefault(s => s.Category == "Memory" && s.Type == "Data" && s.Name == "Memory Used" && s.Hardware.Contains("Total", StringComparison.OrdinalIgnoreCase))
                          ?? Pick("Memory", "Data", "Memory Used", "Used"),
            _ => null,
        };
        return hit?.Id;
    }

    /// <summary>Formats a sensor value; "--" when unavailable.</summary>
    public string Format(string? sensorId, int decimals, bool showUnit, bool fahrenheit)
    {
        var info = Info(sensorId);
        var value = Value(sensorId);
        if (info is null || value is null || float.IsNaN(value.Value))
            return "--";
        // LibreHardwareMonitor reports exactly 0 when it cannot read a temperature (e.g. CPU sensors
        // without administrator rights); no real CPU, GPU or board sensor sits at 0.0 °C.
        if (info.Type == nameof(SensorType.Temperature) && value.Value == 0f)
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
                Collect(hardware, CategoryOf(hardware.HardwareType), values, catalog);

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

    public static readonly string[] CategoryOrder = ["CPU", "GPU", "Memory", "Motherboard", "Storage", "Network", "Other"];

    private static string CategoryOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "Memory",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Motherboard",
        HardwareType.Storage => "Storage",
        HardwareType.Network => "Network",
        _ => "Other",
    };

    /// <summary>Sub-hardware (e.g. the Super I/O chip) is filed under its parent's category.</summary>
    private static void Collect(IHardware hardware, string category, Dictionary<string, float?> values, List<SensorInfo> catalog)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (!Useful.Contains(sensor.SensorType))
                continue;
            var id = sensor.Identifier.ToString();
            values[id] = sensor.Value;
            catalog.Add(new SensorInfo(id, category, hardware.Name.Trim(), sensor.Name, sensor.SensorType.ToString(), UnitOf(sensor.SensorType)));
        }
        foreach (var sub in hardware.SubHardware)
            Collect(sub, category, values, catalog);
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
        lock (_gate)
            _disposed = true;
        _timer?.Dispose();
        _computer.Close();
    }
}
