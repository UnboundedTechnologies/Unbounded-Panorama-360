using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Unbounded.Panorama.Lighting;

namespace Unbounded.Panorama.App.ViewModels;

/// <summary>
/// The ARGB page: every RGB device OpenRGB's service detects (any brand it supports), each with its
/// own effect, colour, speed, brightness and zones, or all in sync; named profiles; restored at
/// start-up and after sleep. Nothing is sent to the devices until the page has been used once.
/// </summary>
public sealed partial class LightingViewModel : ObservableObject, IDisposable
{
    /// <summary>ASUS Armoury Crate's lighting service; it rewrites the Aura devices and fights other software.</summary>
    public const string ArmouryLightingService = "LightingService";

    private const string OpenRgbService = "OpenRGB";

    private readonly RgbHub _hub = new();
    private readonly Dispatcher _ui;
    private readonly Action<string> _log;
    private readonly string _stateFile;
    private readonly string _profileDir;
    private readonly DispatcherTimer _applyDebounce;
    private readonly DispatcherTimer _reconnect;
    private readonly DispatcherTimer _listSettled;
    private OpenRgbWatcher? _watcher;
    private LightingProfile _profile = new();
    private bool _configured;
    private bool _loading;
    private bool _applying;
    private bool _applyAgain;
    // Read the devices back after the next apply (after connecting, waking up or a device change).
    private bool _checkNext;
    private bool _recovering;
    private DateTime _lastRestart = DateTime.MinValue;
    private readonly List<DateTime> _restarts = [];
    // A device was plugged in or out since OpenRGB was last restarted: new evidence, a restart may help again.
    private bool _listChangedSinceRestart;

    public ObservableCollection<RgbDeviceViewModel> Devices { get; } = [];
    public ObservableCollection<string> ProfileNames { get; } = [];

    [ObservableProperty] public partial bool Connected { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Looking for OpenRGB…";
    [ObservableProperty] public partial bool OpenRgbMissing { get; set; }
    [ObservableProperty] public partial bool ArmouryLightingRunning { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string? SelectedProfile { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";

    // Sync: one look for every device that is in sync.
    [ObservableProperty] public partial bool SyncEnabled { get; set; }
    [ObservableProperty] public partial string SyncEffect { get; set; } = LightEffects.Static;
    [ObservableProperty] public partial string SyncColor { get; set; } = "#00E5FF";
    [ObservableProperty] public partial int SyncSpeed { get; set; } = 50;
    [ObservableProperty] public partial int SyncBrightness { get; set; } = 100;
    [ObservableProperty] public partial IReadOnlyList<string> SyncEffects { get; set; } = [LightEffects.Static, LightEffects.Off];

    public bool SyncUsesColor => EffectUsesColor(SyncEffect, Devices.Select(d => d.Info));
    public bool SyncUsesSpeed => EffectUsesSpeed(SyncEffect, Devices.Select(d => d.Info));
    public bool HasDevices => Devices.Count > 0;

    public LightingViewModel(string roamingDir, Dispatcher ui, Action<string> log)
    {
        _ui = ui;
        _log = log;
        _stateFile = Path.Combine(roamingDir, "lighting.json");
        _profileDir = Directory.CreateDirectory(Path.Combine(roamingDir, "lighting-profiles")).FullName;
        _applyDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => { _applyDebounce!.Stop(); _ = ApplyAsync(); }, ui) { IsEnabled = false };
        _reconnect = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => { if (!Connected && !_recovering) _ = RefreshAsync(apply: true); }, ui);
        // A device plugged in or out: OpenRGB sends several notices while it detects, act once they stop.
        _listSettled = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _listSettled!.Stop();
            if (_recovering)
                return;
            _log("Lighting: OpenRGB's device list changed (a device was plugged in or out); checking every device");
            _ = RefreshAsync(apply: true);
        }, ui) { IsEnabled = false };
        if (File.Exists(_stateFile))
        {
            try
            {
                _profile = LightingProfile.FromJson(File.ReadAllText(_stateFile));
                _configured = true;
            }
            catch (Exception)
            {
                _profile = new LightingProfile();
            }
        }
        LoadSyncFromProfile();
        ReloadProfileNames();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Connects in the background; the saved lighting is put back if the page was used before.</summary>
    public void Start()
    {
        CheckServices();
        _reconnect.Start();
        _watcher ??= new OpenRgbWatcher();
        _watcher.DeviceListChanged += () => _ui.BeginInvoke(() =>
        {
            if (_recovering)
                return;
            _listChangedSinceRestart = true;
            _listSettled.Stop();
            _listSettled.Start();
        });
        _ = RefreshAsync(apply: true);
    }

    // ================================================================== devices

    [RelayCommand]
    private Task Refresh() => RefreshAsync(apply: false);

    private async Task RefreshAsync(bool apply)
    {
        CheckServices();
        var ok = await Task.Run(_hub.Refresh);
        Connected = ok;
        if (!ok)
        {
            StopAnimation();
            Devices.Clear();
            OnPropertyChanged(nameof(HasDevices));
            StatusText = OpenRgbMissing
                ? "OpenRGB is not installed: it is what detects and drives RGB devices of every brand."
                : $"Waiting for the OpenRGB service… ({_hub.LastError})";
            return;
        }
        Rebuild();
        StatusText = $"{Devices.Count} RGB device{(Devices.Count == 1 ? "" : "s")} found, {Devices.Sum(d => d.Info.LedCount)} LEDs";
        if (apply && _configured)
        {
            _checkNext = true;
            await ApplyAsync();
        }
    }

    private void Rebuild()
    {
        _loading = true;
        try
        {
            Devices.Clear();
            var ramIndex = 0;
            foreach (var info in _hub.Devices)
            {
                var settings = _profile.Devices.FirstOrDefault(d => d.Key == info.Key);
                if (settings is null)
                {
                    settings = _profile.Device(info.Key, info.Name);
                    settings.Setting.Effect = LightEffects.Static;
                }
                var label = info.Type == "Dram" ? $"Memory stick {++ramIndex}" : info.Name;
                Devices.Add(new RgbDeviceViewModel(this, info, settings, label));
            }
            SyncEffects = LightEffects.For(_hub.Devices);
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(SyncUsesColor));
        OnPropertyChanged(nameof(SyncUsesSpeed));
    }

    /// <summary>A device or zone was edited: send it shortly (drags send at most every 100 ms).</summary>
    public void Changed()
    {
        if (_loading)
            return;
        _configured = true;
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private async Task ApplyAsync()
    {
        // While OpenRGB restarts, edits wait: the refresh after it sends the newest settings.
        if (!Connected || _recovering)
            return;
        if (_applying)
        {
            _applyAgain = true; // the newest settings go out right after this run
            return;
        }
        _applying = true;
        IReadOnlyList<string> ignored = [];
        try
        {
            do
            {
                _applyAgain = false;
                var check = _checkNext;
                _checkNext = false;
                if (check)
                    await StopAnimationAndWaitAsync(); // frames would overwrite the colours being checked
                var profile = _profile.Clone();
                var sync = profile.Sync ? profile.SyncSetting : null;
                var devices = _hub.Devices;
                var resized = false;
                var sent = new List<RgbCommand>();
                var failure = await Task.Run(() =>
                {
                    foreach (var device in devices)
                    {
                        var commands = LightingPlanner.Plan(device, profile.Devices.FirstOrDefault(d => d.Key == device.Key), sync);
                        resized |= commands.Any(c => c is ResizeZoneCommand);
                        try
                        {
                            _hub.Execute(commands);
                            sent.AddRange(commands);
                        }
                        catch (Exception ex)
                        {
                            return $"{device.Name}: {ex.Message}";
                        }
                    }
                    return null;
                });
                Save();
                if (failure is not null)
                {
                    _log("Lighting: " + failure);
                    Connected = _hub.Connected;
                }
                else if (check)
                {
                    try
                    {
                        var expected = LightingCheck.Expected(sent);
                        ignored = await Task.Run(() => _hub.Ignored(expected, TimeSpan.FromSeconds(2)));
                        if (ignored.Count == 0)
                            _log($"Lighting: checked {expected.Count} device{(expected.Count == 1 ? "" : "s")}, all show the settings");
                    }
                    catch (Exception ex)
                    {
                        _log("Lighting: could not read the devices back: " + ex.Message);
                    }
                }
                if (resized)
                {
                    // LED counts changed: show the new numbers.
                    foreach (var vm in Devices)
                        vm.UpdateInfo(_hub.Devices.FirstOrDefault(d => d.Key == vm.Info.Key) ?? vm.Info);
                }
            }
            while (_applyAgain);
            UpdateAnimation();
        }
        finally
        {
            _applying = false;
        }
        if (ignored.Count > 0)
            await RecoverAsync(ignored);
    }

    /// <summary>
    /// Devices ignore what is sent: OpenRGB 1.0 does this for any device it detected again while
    /// running (see <see cref="LightingCheck"/>). Restarting its service makes it detect everything
    /// afresh. Again right away after a device was plugged in or out (each replug breaks it anew),
    /// otherwise at most once every 10 minutes, and never more than 3 times an hour, so a device
    /// that refuses for another reason cannot cause a restart loop.
    /// </summary>
    private async Task RecoverAsync(IReadOnlyList<string> ignored)
    {
        var names = string.Join(", ", ignored.Distinct());
        if (_recovering)
            return;
        var now = DateTime.UtcNow;
        _restarts.RemoveAll(t => now - t > TimeSpan.FromHours(1));
        var allowed = (_listChangedSinceRestart || now - _lastRestart >= TimeSpan.FromMinutes(10)) && _restarts.Count < 3;
        if (!allowed)
        {
            StatusText = $"OpenRGB is not applying changes to {names}. Restarting the PC fixes it.";
            _log($"Lighting: {names} ignored the changes; OpenRGB was already restarted {(_restarts.Count >= 3 ? "3 times this hour" : "recently")}, not again");
            return;
        }
        _recovering = true;
        _lastRestart = now;
        _restarts.Add(now);
        _listChangedSinceRestart = false;
        Busy = true;
        StatusText = $"{names} stopped taking changes (OpenRGB loses devices that are plugged back in). Restarting OpenRGB…";
        _log($"Lighting: {names} ignored the changes; restarting the OpenRGB service");
        try
        {
            StopAnimation();
            var restarted = await Task.Run(RestartOpenRgb);
            if (restarted is not null)
            {
                StatusText = $"OpenRGB is not applying changes to {names}, and it could not be restarted ({restarted}). Restarting the PC fixes it.";
                _log("Lighting: OpenRGB restart failed: " + restarted);
                return;
            }
            _log("Lighting: OpenRGB restarted, devices detected again");
        }
        finally
        {
            _recovering = false;
            Busy = false;
        }
        await RefreshAsync(apply: true);
    }

    /// <summary>Restarts the OpenRGB service and waits until it has detected the devices; null when it worked.</summary>
    private string? RestartOpenRgb()
    {
        try
        {
            _hub.Reset();
            using (var service = new ServiceController(OpenRgbService))
            {
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            // Detection takes a few seconds: wait until the same devices are listed twice in a row.
            string? previous = null;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(45))
            {
                Thread.Sleep(1500);
                if (!_hub.Refresh())
                    continue;
                var now = string.Join("\n", _hub.Devices.Select(d => d.Key));
                if (now.Length > 0 && now == previous)
                    return null;
                previous = now;
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ================================================================== effects drawn by the app

    private readonly object _animationGate = new();
    private System.Threading.Timer? _animation;
    private List<(RgbDevice Device, LightSetting Look)> _animated = [];
    private int _drawing;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// Devices showing an animated effect they lack (the keyboard's Rainbow, for example) get it drawn
    /// by the app, frame by frame; the timer runs only while at least one device needs it.
    /// </summary>
    private void UpdateAnimation()
    {
        var sync = _profile.Sync ? _profile.SyncSetting : null;
        var list = new List<(RgbDevice, LightSetting)>();
        foreach (var device in _hub.Devices)
        {
            var settings = _profile.Devices.FirstOrDefault(d => d.Key == device.Key);
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
            if (list.Count > 0 && Connected)
                _animation ??= new System.Threading.Timer(_ => DrawFrame(), null, 0, 1000 / SoftwareEffects.FramesPerSecond);
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

    /// <summary>Stops the animation and lets a frame that is being sent finish.</summary>
    private async Task StopAnimationAndWaitAsync()
    {
        StopAnimation();
        for (var i = 0; i < 50 && Volatile.Read(ref _drawing) == 1; i++)
            await Task.Delay(10);
    }

    private void DrawFrame()
    {
        // A frame still being sent: skip this one rather than queueing them up.
        if (Interlocked.Exchange(ref _drawing, 1) == 1)
            return;
        try
        {
            List<(RgbDevice Device, LightSetting Look)> devices;
            lock (_animationGate)
                devices = _animated;
            var seconds = _clock.Elapsed.TotalSeconds;
            foreach (var (device, look) in devices)
            {
                var frame = SoftwareEffects.Frame(look.Effect, RgbColor.Parse(look.Color), look.Speed, look.Brightness, device.LedCount, seconds);
                _hub.Execute([new SetLedsCommand(device.Index, frame)]);
            }
        }
        catch (Exception)
        {
            // Disconnected: the reconnect timer restores everything, animation included.
            StopAnimation();
        }
        finally
        {
            Interlocked.Exchange(ref _drawing, 0);
        }
    }

    /// <summary>Stores the settings without sending anything (a zone was renamed).</summary>
    public void SaveOnly()
    {
        if (_configured)
            Save();
    }

    private void Save()
    {
        try
        {
            var temp = _stateFile + ".tmp";
            File.WriteAllText(temp, _profile.ToJson());
            File.Move(temp, _stateFile, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Stores the current effects in the devices themselves (those that support it, such as the
    /// motherboard), so they show before Windows starts and when the app is closed.
    /// </summary>
    [RelayCommand]
    private async Task SaveToDevices()
    {
        if (!Connected)
            return;
        Busy = true;
        try
        {
            var indexes = Devices.Where(d => d.Enabled).Select(d => d.Info.Index).ToList();
            var saved = await Task.Run(() => _hub.SaveToDevices(indexes));
            StatusText = saved > 0
                ? $"Saved in {saved} device{(saved == 1 ? "" : "s")}: they keep this lighting even when the app is closed. Devices without memory (and per-zone colours) are restored by the app at start-up."
                : "None of these devices can store their lighting; the app restores it at start-up.";
        }
        catch (Exception ex)
        {
            StatusText = "Could not save to the devices: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    // ================================================================== sync

    private void LoadSyncFromProfile()
    {
        _loading = true;
        try
        {
            SyncEnabled = _profile.Sync;
            SyncEffect = _profile.SyncSetting.Effect;
            SyncColor = _profile.SyncSetting.Color;
            SyncSpeed = _profile.SyncSetting.Speed;
            SyncBrightness = _profile.SyncSetting.Brightness;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSyncEnabledChanged(bool value)
    {
        _profile.Sync = value;
        foreach (var d in Devices)
            d.NotifySync();
        Changed();
    }

    partial void OnSyncEffectChanged(string value)
    {
        _profile.SyncSetting.Effect = value;
        OnPropertyChanged(nameof(SyncUsesColor));
        OnPropertyChanged(nameof(SyncUsesSpeed));
        Changed();
    }

    partial void OnSyncColorChanged(string value)
    {
        _profile.SyncSetting.Color = value;
        Changed();
    }

    partial void OnSyncSpeedChanged(int value)
    {
        _profile.SyncSetting.Speed = value;
        Changed();
    }

    partial void OnSyncBrightnessChanged(int value)
    {
        _profile.SyncSetting.Brightness = value;
        Changed();
    }

    /// <summary>Turns sync on with this colour (the quick swatches).</summary>
    [RelayCommand]
    private void SyncTo(string hex)
    {
        SyncColor = hex;
        if (!SyncEnabled)
            SyncEnabled = true;
    }

    public bool IsSynced(DeviceLighting settings) => _profile.Sync && settings.InSync;

    public static bool EffectUsesColor(string effect, IEnumerable<RgbDevice> devices) =>
        effect.Equals(LightEffects.Static, StringComparison.OrdinalIgnoreCase)
        || devices.Select(d => d.Mode(effect)).Any(m => m is null || m.ModeColor || m.PerLedColor);

    public static bool EffectUsesSpeed(string effect, IEnumerable<RgbDevice> devices) =>
        devices.Select(d => d.Mode(effect)).Any(m => m is { HasSpeed: true });

    // ================================================================== profiles

    private void ReloadProfileNames(string? select = null)
    {
        ProfileNames.Clear();
        foreach (var file in Directory.EnumerateFiles(_profileDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            ProfileNames.Add(Path.GetFileNameWithoutExtension(file));
        SelectedProfile = select is not null && ProfileNames.Contains(select) ? select : ProfileNames.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (value is not null)
            ProfileName = value;
    }

    private string ProfilePath(string name) => Path.Combine(_profileDir, name + ".json");

    [RelayCommand]
    private void SaveProfile()
    {
        var name = string.Concat(ProfileName.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (name.Length == 0)
        {
            StatusText = "Type a name for the profile first.";
            return;
        }
        var existed = File.Exists(ProfilePath(name));
        File.WriteAllText(ProfilePath(name), _profile.ToJson());
        ReloadProfileNames(select: name);
        StatusText = existed ? $"Profile “{name}” updated." : $"Profile “{name}” saved.";
    }

    [RelayCommand]
    private void LoadProfile()
    {
        if (SelectedProfile is not { } name || !File.Exists(ProfilePath(name)))
            return;
        try
        {
            _profile = LightingProfile.FromJson(File.ReadAllText(ProfilePath(name)));
        }
        catch (Exception ex)
        {
            StatusText = "Could not read the profile: " + ex.Message;
            return;
        }
        LoadSyncFromProfile();
        Rebuild();
        _configured = true;
        _ = ApplyAsync();
        StatusText = $"Profile “{name}” applied.";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } name)
            return;
        try
        {
            File.Delete(ProfilePath(name));
        }
        catch (IOException)
        {
        }
        ReloadProfileNames();
        StatusText = $"Profile “{name}” deleted.";
    }

    // ================================================================== services

    private void CheckServices()
    {
        ArmouryLightingRunning = ServiceRunning(ArmouryLightingService) == true;
        OpenRgbMissing = ServiceRunning(OpenRgbService) is null;
    }

    /// <summary>True running, false installed but stopped, null not installed.</summary>
    private static bool? ServiceRunning(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stops Armoury Crate's lighting service and keeps it from starting again; the rest of Armoury
    /// Crate (fans, updates) is untouched. Reversible with <see cref="EnableArmouryLighting"/>.
    /// </summary>
    [RelayCommand]
    private async Task DisableArmouryLighting()
    {
        Busy = true;
        try
        {
            var result = await Task.Run(() =>
            {
                var config = RunSc("config", ArmouryLightingService, "start=", "disabled");
                var stop = RunSc("stop", ArmouryLightingService);
                return (config, stop);
            });
            await Task.Delay(1500);
            CheckServices();
            StatusText = ArmouryLightingRunning
                ? $"Armoury Crate's lighting service is still running ({result.stop.Output.Trim()})."
                : "Armoury Crate's lighting is off; this page now controls your RGB.";
            _log($"Armoury Crate lighting service: start disabled (sc {result.config.Code}), stopped (sc {result.stop.Code})");
            if (!ArmouryLightingRunning && _configured)
                await ApplyAsync();
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task EnableArmouryLighting()
    {
        Busy = true;
        try
        {
            await Task.Run(() =>
            {
                RunSc("config", ArmouryLightingService, "start=", "auto");
                RunSc("start", ArmouryLightingService);
            });
            await Task.Delay(1500);
            CheckServices();
            StatusText = "Armoury Crate's lighting service is back on (it will take over the ASUS lighting again).";
            _log("Armoury Crate lighting service: start automatic, started");
        }
        finally
        {
            Busy = false;
        }
    }

    private static (int Code, string Output) RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    /// <summary>Installs OpenRGB (with its background service) through winget, like FFmpeg.</summary>
    [RelayCommand]
    private async Task InstallOpenRgb()
    {
        if (Busy)
            return;
        Busy = true;
        StatusText = "Installing OpenRGB… (about a minute)";
        try
        {
            var code = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo("winget") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "install", "--id", "OpenRGB.OpenRGB", "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            });
            _log($"OpenRGB install: winget exit code {code}");
            await Task.Delay(3000); // the service starts and detects devices
            await RefreshAsync(apply: true);
        }
        catch (Exception ex)
        {
            StatusText = "Could not install OpenRGB: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    // ================================================================== sleep

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // RGB controllers lose their colours in sleep; put them back once Windows is awake.
        if (e.Mode == PowerModes.Resume && _configured)
            _ui.BeginInvoke(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await RefreshAsync(apply: true);
            });
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopAnimation();
        _reconnect.Stop();
        _applyDebounce.Stop();
        _listSettled.Stop();
        _watcher?.Dispose();
        _hub.Dispose();
    }
}

/// <summary>One RGB device on the page.</summary>
public sealed partial class RgbDeviceViewModel : ObservableObject
{
    private readonly LightingViewModel _owner;
    private readonly DeviceLighting _settings;

    public RgbDevice Info { get; private set; }
    public string Label { get; }
    public string TypeLabel => Info.Type switch
    {
        "Dram" => "RAM",
        "Gpu" => "GRAPHICS CARD",
        "Ledstrip" => "LED STRIP",
        _ => Info.Type.ToUpperInvariant(),
    };
    public string Summary => $"{Info.Vendor} · {Info.LedCount} LED{(Info.LedCount == 1 ? "" : "s")}";
    public IReadOnlyList<string> Effects { get; }
    public ObservableCollection<RgbZoneViewModel> Zones { get; } = [];

    public RgbDeviceViewModel(LightingViewModel owner, RgbDevice info, DeviceLighting settings, string label)
    {
        _owner = owner;
        _settings = settings;
        Info = info;
        Label = label;
        Effects = LightEffects.For([info]);
        foreach (var zone in info.Zones)
            Zones.Add(new RgbZoneViewModel(this, zone, settings.Zone(zone.Name)));
    }

    public void UpdateInfo(RgbDevice info)
    {
        Info = info;
        foreach (var z in Zones)
            z.UpdateInfo(info.Zones.FirstOrDefault(i => i.Name == z.Key));
        OnPropertyChanged(nameof(Summary));
    }

    public bool Enabled
    {
        get => _settings.Enabled;
        set { if (_settings.Enabled != value) { _settings.Enabled = value; _owner.Changed(); OnPropertyChanged(nameof(Editable)); NotifySync(); } }
    }

    public bool InSync
    {
        get => _settings.InSync;
        set { if (_settings.InSync != value) { _settings.InSync = value; _owner.Changed(); NotifySync(); } }
    }

    /// <summary>Its own controls are used (enabled and not following sync).</summary>
    public bool Editable => _settings.Enabled && !_owner.IsSynced(_settings);
    public bool Following => _settings.Enabled && _owner.IsSynced(_settings);

    public void NotifySync()
    {
        OnPropertyChanged(nameof(Editable));
        OnPropertyChanged(nameof(Following));
    }

    public string Effect
    {
        get => _settings.Setting.Effect;
        set
        {
            if (value is null || _settings.Setting.Effect == value)
                return;
            _settings.Setting.Effect = value;
            OnPropertyChanged(nameof(UsesColor));
            OnPropertyChanged(nameof(UsesSpeed));
            Changed();
        }
    }

    public string Color
    {
        get => _settings.Setting.Color;
        set { if (_settings.Setting.Color != value) { _settings.Setting.Color = value; Changed(); } }
    }

    public int Speed
    {
        get => _settings.Setting.Speed;
        set { if (_settings.Setting.Speed != value) { _settings.Setting.Speed = value; Changed(); } }
    }

    public int Brightness
    {
        get => _settings.Setting.Brightness;
        set { if (_settings.Setting.Brightness != value) { _settings.Setting.Brightness = value; Changed(); } }
    }

    public bool UsesColor => LightingViewModel.EffectUsesColor(Effect, [Info]);
    public bool UsesSpeed => LightingViewModel.EffectUsesSpeed(Effect, [Info]);

    /// <summary>Zone colours only show with Static, on a device with several zones.</summary>
    public bool ZoneColorsApply => Zones.Count > 1 && Effect.Equals(LightEffects.Static, StringComparison.OrdinalIgnoreCase);

    /// <summary>A name changed: save, without touching the lighting.</summary>
    public void Saved() => _owner.SaveOnly();

    /// <summary>The user set something on this device: from now on it is controlled (see DeviceLighting.Touched).</summary>
    public void Changed()
    {
        _settings.Touched = true;
        OnPropertyChanged(nameof(ZoneColorsApply));
        _owner.Changed();
    }
}

/// <summary>A zone: its own colour (Static) and, for ARGB headers, how many LEDs are connected.</summary>
public sealed partial class RgbZoneViewModel : ObservableObject
{
    private readonly RgbDeviceViewModel _device;
    private readonly ZoneLighting _settings;
    private RgbZone? _info;

    public RgbZoneViewModel(RgbDeviceViewModel device, RgbZone info, ZoneLighting settings)
    {
        _device = device;
        _info = info;
        _settings = settings;
    }

    /// <summary>The zone's name as OpenRGB reports it (settings are stored under it).</summary>
    public string Key => _settings.Zone;

    public string Name => (_info?.Name ?? _settings.Zone) is var n && n.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "All LEDs" : n;
    /// <summary>The user's name for the zone (saved only; no lighting change).</summary>
    public string Label
    {
        get => _settings.Label ?? "";
        set
        {
            var label = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_settings.Label == label)
                return;
            _settings.Label = label;
            OnPropertyChanged(nameof(DisplayName));
            _device.Saved();
        }
    }

    public string DisplayName => _settings.Label ?? Name;

    public bool Resizable => _info?.Resizable == true;
    public int MaxLeds => _info?.LedsMax ?? 0;
    public string LedText => _info is null ? "" : $"{_info.LedCount} LED{(_info.LedCount == 1 ? "" : "s")}";

    public void UpdateInfo(RgbZone? info)
    {
        _info = info;
        OnPropertyChanged(nameof(LedText));
        OnPropertyChanged(nameof(LedCount));
    }

    /// <summary>Its own colour; off = same as the device.</summary>
    public bool HasOwnColor
    {
        get => _settings.Color is not null;
        set
        {
            if (value == HasOwnColor)
                return;
            _settings.Color = value ? _device.Color : null;
            OnPropertyChanged(nameof(Color));
            _device.Changed();
        }
    }

    public string Color
    {
        get => _settings.Color ?? _device.Color;
        set
        {
            if (_settings.Color == value)
                return;
            _settings.Color = value;
            OnPropertyChanged(nameof(HasOwnColor));
            _device.Changed();
        }
    }

    /// <summary>LEDs on an ARGB header (0 = nothing connected).</summary>
    public int LedCount
    {
        get => _settings.LedCount ?? _info?.LedCount ?? 0;
        set
        {
            value = Math.Clamp(value, _info?.LedsMin ?? 0, _info?.LedsMax ?? 0);
            if (_settings.LedCount == value)
                return;
            _settings.LedCount = value;
            OnPropertyChanged();
            _device.Changed();
        }
    }
}
