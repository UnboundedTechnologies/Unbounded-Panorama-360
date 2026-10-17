using System.Diagnostics;
using Unbounded.Panorama.Lighting;
using Unbounded.Panorama.Server.Storage;

namespace Unbounded.Panorama.Server.Lighting;

/// <summary>
/// The ARGB page's engine, the same behaviour as the Windows app: every RGB device through the
/// OpenRGB service, sync or a look per device and zone, ARGB header LED counts, profiles, effects
/// drawn by the app on devices without them, and a read-back after (re)connecting, waking or a
/// device change, restarting OpenRGB when a device ignored what was sent.
/// </summary>
public sealed class LightingService : BackgroundService
{
    private const string OpenRgbUnit = "openrgb";

    private readonly DataStore _store;
    private readonly ILogger<LightingService> _log;
    private readonly RgbHub _hub = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly object _profileGate = new();
    private readonly object _animationGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private LightingProfile _profile = new();
    private bool _configured;
    private OpenRgbWatcher? _watcher;
    private Timer? _animation;
    private List<(RgbDevice Device, LightSetting Look)> _animated = [];
    private int _drawing;
    private CancellationTokenSource? _pendingApply;
    private bool _checkNext;
    private bool _recovering;
    private DateTime _lastRestart = DateTime.MinValue;
    private readonly List<DateTime> _restarts = [];
    private bool _listChangedSinceRestart;
    private CancellationTokenSource? _listSettled;

    public LightingService(DataStore store, ILogger<LightingService> log)
    {
        _store = store;
        _log = log;
        if (File.Exists(store.LightingFile))
        {
            try
            {
                _profile = LightingProfile.FromJson(File.ReadAllText(store.LightingFile));
                _configured = true;
            }
            catch (Exception)
            {
                _profile = new LightingProfile();
            }
        }
    }

    public bool Connected => _hub.Connected;
    public string Status { get; private set; } = "Looking for OpenRGB…";
    public IReadOnlyList<RgbDevice> Devices => _hub.Devices;

    public LightingProfile Profile
    {
        get
        {
            lock (_profileGate)
                return _profile.Clone();
        }
    }

    /// <summary>The page sent new settings: store them and apply shortly (drags send at most every 100 ms).</summary>
    public void Update(LightingProfile profile)
    {
        lock (_profileGate)
        {
            _profile = profile;
            _configured = true;
        }
        Save();
        ScheduleApply(TimeSpan.FromMilliseconds(100));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher = new OpenRgbWatcher();
        _watcher.DeviceListChanged += OnDeviceListChanged;
        await RefreshAsync(apply: true);
        // Reconnect while OpenRGB is away.
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            if (!_hub.Connected && !_recovering)
                await RefreshAsync(apply: true);
        }
    }

    public async Task RefreshAsync(bool apply)
    {
        var ok = await Task.Run(_hub.Refresh);
        if (!ok)
        {
            StopAnimation();
            Status = $"Waiting for the OpenRGB service… ({_hub.LastError})";
            return;
        }
        Status = $"{_hub.Devices.Count} RGB device{(_hub.Devices.Count == 1 ? "" : "s")} found, {_hub.Devices.Sum(d => d.LedCount)} LEDs";
        if (apply && _configured)
        {
            _checkNext = true;
            await ApplyAsync();
        }
    }

    private void ScheduleApply(TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pendingApply, cts)?.Cancel();
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                _ = ApplyAsync();
        }, TaskScheduler.Default);
    }

    private async Task ApplyAsync()
    {
        if (!_hub.Connected || _recovering)
            return;
        await _applyGate.WaitAsync();
        IReadOnlyList<string> ignored = [];
        try
        {
            var check = _checkNext;
            _checkNext = false;
            if (check)
                await StopAnimationAndWaitAsync(); // frames would overwrite the colours being checked
            var profile = Profile;
            var sync = profile.Sync ? profile.SyncSetting : null;
            var sent = new List<RgbCommand>();
            string? failure = null;
            await Task.Run(() =>
            {
                foreach (var device in _hub.Devices)
                {
                    var commands = LightingPlanner.Plan(device, profile.Devices.FirstOrDefault(d => d.Key == device.Key), sync);
                    try
                    {
                        _hub.Execute(commands);
                        sent.AddRange(commands);
                    }
                    catch (Exception ex)
                    {
                        failure = $"{device.Name}: {ex.Message}";
                        return;
                    }
                }
            });
            if (failure is not null)
            {
                _log.LogWarning("Lighting: {Failure}", failure);
            }
            else if (check)
            {
                var expected = LightingCheck.Expected(sent);
                ignored = await Task.Run(() => _hub.Ignored(expected, TimeSpan.FromSeconds(2)));
                if (ignored.Count == 0)
                    _log.LogInformation("Lighting: checked {Count} devices, all show the settings", expected.Count);
            }
            UpdateAnimation(profile);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Lighting: {Message}", ex.Message);
        }
        finally
        {
            _applyGate.Release();
        }
        if (ignored.Count > 0)
            await RecoverAsync(ignored);
    }

    /// <summary>
    /// Devices ignore what is sent (OpenRGB detected them again while running): restart its service.
    /// Again right away after a device change, otherwise at most every 10 minutes and 3 times an hour.
    /// </summary>
    private async Task RecoverAsync(IReadOnlyList<string> ignored)
    {
        var names = string.Join(", ", ignored.Distinct());
        if (_recovering)
            return;
        var now = DateTime.UtcNow;
        _restarts.RemoveAll(t => now - t > TimeSpan.FromHours(1));
        if (!((_listChangedSinceRestart || now - _lastRestart >= TimeSpan.FromMinutes(10)) && _restarts.Count < 3))
        {
            Status = $"OpenRGB is not applying changes to {names}.";
            _log.LogWarning("Lighting: {Names} ignored the changes; OpenRGB was restarted recently, not again", names);
            return;
        }
        _recovering = true;
        _lastRestart = now;
        _restarts.Add(now);
        _listChangedSinceRestart = false;
        Status = $"{names} stopped taking changes. Restarting OpenRGB…";
        _log.LogWarning("Lighting: {Names} ignored the changes; restarting the OpenRGB service", names);
        try
        {
            StopAnimation();
            _hub.Reset();
            var (code, output) = await RunAsync("systemctl", "restart", OpenRgbUnit);
            if (code != 0)
            {
                Status = $"OpenRGB could not be restarted ({output.Trim()}).";
                return;
            }
            // Detection takes a few seconds: wait until the same devices are listed twice in a row.
            string? previous = null;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(45))
            {
                await Task.Delay(1500);
                if (!await Task.Run(_hub.Refresh))
                    continue;
                var current = string.Join("\n", _hub.Devices.Select(d => d.Key));
                if (current.Length > 0 && current == previous)
                    break;
                previous = current;
            }
            _log.LogInformation("Lighting: OpenRGB restarted, devices detected again");
        }
        finally
        {
            _recovering = false;
        }
        await RefreshAsync(apply: true);
    }

    private void OnDeviceListChanged()
    {
        if (_recovering)
            return;
        _listChangedSinceRestart = true;
        // OpenRGB sends several notices while it detects: act once they stop.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _listSettled, cts)?.Cancel();
        _ = Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled || _recovering)
                return;
            _log.LogInformation("Lighting: OpenRGB's device list changed; checking every device");
            await RefreshAsync(apply: true);
        }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ effects drawn by the app

    private void UpdateAnimation(LightingProfile profile)
    {
        var sync = profile.Sync ? profile.SyncSetting : null;
        var list = new List<(RgbDevice, LightSetting)>();
        foreach (var device in _hub.Devices)
        {
            var settings = profile.Devices.FirstOrDefault(d => d.Key == device.Key);
            if (settings is not { Enabled: true })
                continue;
            var synced = sync is not null && settings.InSync;
            if (!synced && !settings.Touched)
                continue;
            var look = synced ? sync! : settings.Setting;
            if (SoftwareEffects.Needed(device, look.Effect))
                list.Add((device, look.Clone()));
        }
        lock (_animationGate)
        {
            _animated = list;
            if (list.Count > 0 && _hub.Connected)
                _animation ??= new Timer(_ => DrawFrame(), null, 0, 1000 / SoftwareEffects.FramesPerSecond);
            else
                StopAnimation();
        }
    }

    private void StopAnimation()
    {
        lock (_animationGate)
        {
            _animation?.Dispose();
            _animation = null;
        }
    }

    private async Task StopAnimationAndWaitAsync()
    {
        StopAnimation();
        for (var i = 0; i < 50 && Volatile.Read(ref _drawing) == 1; i++)
            await Task.Delay(10);
    }

    private void DrawFrame()
    {
        if (Interlocked.Exchange(ref _drawing, 1) == 1)
            return;
        try
        {
            List<(RgbDevice Device, LightSetting Look)> devices;
            lock (_animationGate)
                devices = _animated;
            var seconds = _clock.Elapsed.TotalSeconds;
            foreach (var (device, look) in devices)
                _hub.Execute([new SetLedsCommand(device.Index, SoftwareEffects.Frame(look.Effect, RgbColor.Parse(look.Color), look.Speed, look.Brightness, device.LedCount, seconds))]);
        }
        catch (Exception)
        {
            StopAnimation();
        }
        finally
        {
            Interlocked.Exchange(ref _drawing, 0);
        }
    }

    // ------------------------------------------------------------------ profiles and storage

    public IReadOnlyList<string> ProfileNames() =>
        [.. Directory.EnumerateFiles(_store.ProfileDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];

    private string ProfilePath(string name) =>
        Path.Combine(_store.ProfileDir, string.Concat(name.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim() + ".json");

    public bool SaveProfile(string name)
    {
        var path = ProfilePath(name);
        if (Path.GetFileNameWithoutExtension(path).Length == 0)
            return false;
        File.WriteAllText(path, Profile.ToJson());
        return true;
    }

    public bool LoadProfile(string name)
    {
        var path = ProfilePath(name);
        if (!File.Exists(path))
            return false;
        Update(LightingProfile.FromJson(File.ReadAllText(path)));
        return true;
    }

    public bool DeleteProfile(string name)
    {
        var path = ProfilePath(name);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Stores the current effects in devices that have memory for it (the motherboard), for power-on.</summary>
    public int SaveToDevices()
    {
        var profile = Profile;
        var indexes = _hub.Devices.Where(d => profile.Devices.FirstOrDefault(p => p.Key == d.Key)?.Enabled != false).Select(d => d.Index).ToList();
        return _hub.SaveToDevices(indexes);
    }

    private void Save()
    {
        try
        {
            DataStore.WriteAtomic(_store.LightingFile, Profile.ToJson());
        }
        catch (IOException)
        {
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output);
    }

    public override void Dispose()
    {
        StopAnimation();
        _watcher?.Dispose();
        _hub.Dispose();
        base.Dispose();
    }
}
