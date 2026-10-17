using System.Globalization;
using Unbounded.Panorama.Sensors;

namespace Unbounded.Panorama.Server.Sensors;

/// <summary>
/// Live values for widgets on Linux: the host's hardware sensors (hwmon: CPU, NVMe, RAM, network
/// card…), which a container can read, plus whole-server figures from the Proxmox API when a token
/// is configured (a container only sees its own CPU and memory use).
/// </summary>
public sealed class LinuxSensors : ISensorReadings, IDisposable
{
    private readonly ProxmoxStats? _proxmox;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private Dictionary<string, double> _values = new();
    private List<SensorInfo> _catalog = [];

    public LinuxSensors(ProxmoxStats? proxmox)
    {
        _proxmox = proxmox;
        CpuName = ReadCpuName();
        Refresh();
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public string CpuName { get; }
    public string GpuName => "GPU";
    public IReadOnlyList<SensorInfo> Catalog { get { lock (_gate) return _catalog; } }

    public double? Value(string? id)
    {
        if (id is null)
            return null;
        lock (_gate)
            return _values.TryGetValue(id, out var v) ? v : null;
    }

    public string Format(string? sensorId, int decimals, bool showUnit, bool fahrenheit)
    {
        SensorInfo? info;
        lock (_gate)
            info = _catalog.FirstOrDefault(s => s.Id == sensorId);
        if (info is null || Value(sensorId) is not { } value || double.IsNaN(value))
            return "--";
        var unit = info.Unit;
        if (info.Type == "Temperature" && fahrenheit)
        {
            value = value * 9 / 5 + 32;
            unit = "°F";
        }
        var text = value.ToString("F" + Math.Clamp(decimals, 0, 3), CultureInfo.InvariantCulture);
        return showUnit && unit.Length > 0 ? text + unit : text;
    }

    private void Refresh()
    {
        var values = new Dictionary<string, double>();
        var catalog = new List<SensorInfo>();
        try
        {
            ReadHwmon(values, catalog);
        }
        catch (IOException)
        {
            // A sensor that disappears between listing and reading: keep going with the rest.
        }
        _proxmox?.Collect(values, catalog);
        catalog.Sort((a, b) => (a.CategoryOrder, a.Hardware, a.Name).CompareTo((b.CategoryOrder, b.Hardware, b.Name)));
        lock (_gate)
        {
            _values = values;
            _catalog = catalog;
        }
    }

    /// <summary>hwmon's temperatures and fans; ids use the chip name and device, not the hwmonN number (which can change).</summary>
    private static void ReadHwmon(Dictionary<string, double> values, List<SensorInfo> catalog)
    {
        const string root = "/sys/class/hwmon";
        if (!Directory.Exists(root))
            return;
        var used = new HashSet<string>();
        foreach (var dir in Directory.EnumerateDirectories(root).Order())
        {
            var chip = ReadText(Path.Combine(dir, "name")) ?? Path.GetFileName(dir);
            var device = DeviceName(dir);
            var (category, hardware) = Describe(chip, device);
            foreach (var input in Directory.EnumerateFiles(dir, "*_input").Order())
            {
                var sensor = Path.GetFileName(input)[..^"_input".Length];
                var kind = sensor.TrimEnd("0123456789".ToCharArray());
                if (kind is not ("temp" or "fan"))
                    continue;
                var raw = ReadText(input);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    continue;
                var label = ReadText(Path.Combine(dir, sensor + "_label"));
                var id = $"hwmon:{chip}:{device}:{sensor}";
                if (!used.Add(id))
                    continue;
                if (kind == "temp")
                {
                    values[id] = number / 1000.0;
                    catalog.Add(new SensorInfo(id, category, hardware, label ?? (sensor == "temp1" ? "Temperature" : sensor), "Temperature", "°C"));
                }
                else
                {
                    values[id] = number;
                    catalog.Add(new SensorInfo(id, category, hardware, label ?? sensor.Replace("fan", "Fan "), "Fan", " RPM"));
                }
            }
        }
    }

    private static (string Category, string Hardware) Describe(string chip, string device) => chip switch
    {
        "coretemp" or "k10temp" or "zenpower" => ("CPU", ReadCpuName()),
        "nvme" => ("Storage", ReadText($"/sys/class/nvme/{device}/model") ?? "NVMe"),
        "drivetemp" => ("Storage", "Drive " + device),
        "jc42" or "spd5118" => ("Memory", "RAM " + device),
        "amdgpu" or "nouveau" or "i915" or "xe" => ("GPU", chip),
        _ when chip.StartsWith("nct", StringComparison.Ordinal) || chip.StartsWith("it87", StringComparison.Ordinal) || chip == "acpitz" => ("Motherboard", chip == "acpitz" ? "ACPI" : chip),
        _ when chip.StartsWith("r8169", StringComparison.Ordinal) || chip.StartsWith("iwlwifi", StringComparison.Ordinal) || chip.StartsWith("igc", StringComparison.Ordinal) => ("Network", chip.Split('_')[0]),
        _ => ("Other", chip),
    };

    private static string DeviceName(string hwmonDir)
    {
        try
        {
            var link = new FileInfo(Path.Combine(hwmonDir, "device")).LinkTarget;
            return link is null ? Path.GetFileName(hwmonDir) : Path.GetFileName(link);
        }
        catch (IOException)
        {
            return Path.GetFileName(hwmonDir);
        }
    }

    private static string ReadCpuName()
    {
        foreach (var line in ReadText("/proc/cpuinfo")?.Split('\n') ?? [])
            if (line.StartsWith("model name", StringComparison.Ordinal) && line.IndexOf(':') is var i and > 0)
                return line[(i + 1)..].Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();
        return "CPU";
    }

    private static string? ReadText(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _timer.Dispose();
}
