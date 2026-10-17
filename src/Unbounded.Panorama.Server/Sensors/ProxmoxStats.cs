using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Unbounded.Panorama.Sensors;

namespace Unbounded.Panorama.Server.Sensors;

/// <summary>
/// Whole-server figures from the Proxmox API with a read-only token (PVEAuditor): host CPU and
/// memory, load, uptime, running guests, live network speed, storage use, and each guest's CPU,
/// memory and network. Configured by /etc/unbounded-panorama/proxmox.env:
/// PVE_API_URL, PVE_TOKEN_ID, PVE_TOKEN_SECRET (and optionally PVE_NODE).
/// </summary>
public sealed class ProxmoxStats : IDisposable
{
    private const string Category = "Server";

    private readonly HttpClient _http;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private Dictionary<string, double> _values = new();
    private List<SensorInfo> _catalog = [];
    private string? _node;
    private readonly Dictionary<long, (long In, long Out, long Ticks)> _lastTraffic = new();
    private DateTime _storageRead = DateTime.MinValue;
    private List<(string Id, double Used, double Total)> _storage = [];
    private int _busy;

    public string? LastError { get; private set; }
    public bool Working { get; private set; }

    private ProxmoxStats(Uri api, string tokenId, string secret, string? node)
    {
        // Proxmox serves its API with its own self-signed certificate; this client only talks to it.
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        _http = new HttpClient(handler) { BaseAddress = new Uri(api.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("PVEAPIToken", $"{tokenId}={secret}");
        _node = node;
        _timer = new Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>Reads the env file; null when it is absent or incomplete (widgets then use hwmon only).</summary>
    public static ProxmoxStats? FromEnvFile(string file)
    {
        if (!File.Exists(file))
            return null;
        var env = File.ReadAllLines(file)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .ToDictionary(l => l[..l.IndexOf('=')].Trim(), l => l[(l.IndexOf('=') + 1)..].Trim().Trim('"'));
        if (!env.TryGetValue("PVE_API_URL", out var url) || !env.TryGetValue("PVE_TOKEN_ID", out var id) || !env.TryGetValue("PVE_TOKEN_SECRET", out var secret)
            || !Uri.TryCreate(url, UriKind.Absolute, out var api))
            return null;
        return new ProxmoxStats(api, id, secret, env.GetValueOrDefault("PVE_NODE"));
    }

    /// <summary>Adds the latest figures to a sensor reading.</summary>
    public void Collect(Dictionary<string, double> values, List<SensorInfo> catalog)
    {
        lock (_gate)
        {
            foreach (var (k, v) in _values)
                values[k] = v;
            catalog.AddRange(_catalog);
        }
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            _node ??= (await GetAsync("nodes").ConfigureAwait(false)).EnumerateArray().First().GetProperty("node").GetString();
            var status = await GetAsync($"nodes/{_node}/status").ConfigureAwait(false);
            var guests = await GetAsync("cluster/resources?type=vm").ConfigureAwait(false);
            if (DateTime.UtcNow - _storageRead > TimeSpan.FromSeconds(30))
            {
                var storage = await GetAsync($"nodes/{_node}/storage").ConfigureAwait(false);
                _storage = [.. storage.EnumerateArray()
                    .Where(s => Number(s, "active") == 1 && Number(s, "total") > 0)
                    .Select(s => (s.GetProperty("storage").GetString()!, Number(s, "used"), Number(s, "total")))];
                _storageRead = DateTime.UtcNow;
            }
            Publish(status, guests);
            Working = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            Working = false;
            LastError = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Publish(JsonElement status, JsonElement guests)
    {
        var values = new Dictionary<string, double>();
        var catalog = new List<SensorInfo>();
        var host = "Proxmox " + _node;
        void Add(string id, string hardware, string name, string type, string unit, double value)
        {
            values[id] = value;
            catalog.Add(new SensorInfo(id, Category, hardware, name, type, unit));
        }

        Add("pve:cpu", host, "CPU load", "Load", "%", Number(status, "cpu") * 100);
        var memory = status.GetProperty("memory");
        var used = Number(memory, "used");
        var total = Number(memory, "total");
        Add("pve:mem_used", host, "Memory used", "Data", " GB", used / 1e9);
        Add("pve:mem_pct", host, "Memory used %", "Load", "%", total > 0 ? used / total * 100 : 0);
        if (status.TryGetProperty("loadavg", out var load) && load.GetArrayLength() > 0
            && double.TryParse(load[0].GetString(), System.Globalization.CultureInfo.InvariantCulture, out var load1))
            Add("pve:load1", host, "Load average (1 min)", "Load", "", load1);
        Add("pve:uptime_days", host, "Uptime", "Time", " days", Number(status, "uptime") / 86400);

        var now = Stopwatch.GetTimestamp();
        double totalIn = 0, totalOut = 0;
        var running = 0;
        foreach (var g in guests.EnumerateArray())
        {
            if (g.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == 1)
                continue;
            var vmid = (long)Number(g, "vmid");
            var name = $"#{vmid} {(g.TryGetProperty("name", out var n) ? n.GetString() : "")}";
            var isRunning = g.GetProperty("status").GetString() == "running";
            if (isRunning)
                running++;
            Add($"pve:guest:{vmid}:cpu", name, "CPU load", "Load", "%", Number(g, "cpu") * 100);
            Add($"pve:guest:{vmid}:mem", name, "Memory used", "Data", " GB", Number(g, "mem") / 1e9);
            var netIn = (long)Number(g, "netin");
            var netOut = (long)Number(g, "netout");
            double rateIn = 0, rateOut = 0;
            if (_lastTraffic.TryGetValue(vmid, out var last) && netIn >= last.In && netOut >= last.Out)
            {
                var seconds = Stopwatch.GetElapsedTime(last.Ticks, now).TotalSeconds;
                if (seconds > 0)
                {
                    rateIn = (netIn - last.In) / seconds;
                    rateOut = (netOut - last.Out) / seconds;
                }
            }
            _lastTraffic[vmid] = (netIn, netOut, now);
            totalIn += rateIn;
            totalOut += rateOut;
            Add($"pve:guest:{vmid}:net_in", name, "Network in", "Throughput", " MB/s", rateIn / 1e6);
            Add($"pve:guest:{vmid}:net_out", name, "Network out", "Throughput", " MB/s", rateOut / 1e6);
        }
        Add("pve:guests_running", host, "Guests running", "Count", "", running);
        Add("pve:net_in", host, "Network in (all guests)", "Throughput", " MB/s", totalIn / 1e6);
        Add("pve:net_out", host, "Network out (all guests)", "Throughput", " MB/s", totalOut / 1e6);
        foreach (var (id, storageUsed, storageTotal) in _storage)
        {
            Add($"pve:storage:{id}:pct", host, $"Storage {id} used %", "Load", "%", storageUsed / storageTotal * 100);
            Add($"pve:storage:{id}:free", host, $"Storage {id} free", "Data", " TB", (storageTotal - storageUsed) / 1e12);
        }

        lock (_gate)
        {
            _values = values;
            _catalog = catalog;
        }
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await _http.GetAsync(path).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        } : 0;

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
