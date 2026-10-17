namespace Unbounded.Panorama.Sensors;

public sealed record SensorInfo(string Id, string Category, string Hardware, string Name, string Type, string Unit)
{
    public string Display => $"{Category} {Hardware} › {Name} ({Type})";

    /// <summary>Group header in the picker, e.g. "CPU · AMD Ryzen 7 9800X3D".</summary>
    public string Group => $"{Category} · {Hardware}";

    public int CategoryOrder => Array.IndexOf(SensorCategories.Order, Category) is var i and >= 0 ? i : 99;
}

public static class SensorCategories
{
    public static readonly string[] Order = ["CPU", "GPU", "Memory", "Motherboard", "Storage", "Network", "Server", "Other"];
}

/// <summary>Where widget text gets its live values: LibreHardwareMonitor on Windows, hwmon and Proxmox on Linux.</summary>
public interface ISensorReadings
{
    string CpuName { get; }
    string GpuName { get; }
    IReadOnlyList<SensorInfo> Catalog { get; }

    /// <summary>Formats a sensor value; "--" when unavailable.</summary>
    string Format(string? sensorId, int decimals, bool showUnit, bool fahrenheit);
}
