using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Unbounded.Panorama.App.Services;
using Unbounded.Panorama.Engine;
using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Media;
using Unbounded.Panorama.Sensors;
using LayoutOrientation = Unbounded.Panorama.Layout.Orientation;

namespace Unbounded.Panorama.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SensorHub _sensors;
    private readonly DisplayEngine _engine;
    private readonly MediaPreparer _preparer;
    private readonly WidgetText _text;
    private readonly AppStorage _storage;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _liveDebounce;
    private bool _liveNeedsConfig;
    private bool _loading;
    private CancellationTokenSource? _previewCts;

    public ObservableCollection<WidgetViewModel> Widgets { get; } = [];
    public ObservableCollection<SensorInfo> FilteredSensors { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    public IReadOnlyList<WidgetContent> ContentKinds { get; } = Enum.GetValues<WidgetContent>();
    public IReadOnlyList<FontWeightName> Weights { get; } = Enum.GetValues<FontWeightName>();
    public IReadOnlyList<Layout.TextAlign> Aligns { get; } = Enum.GetValues<Layout.TextAlign>();
    public IReadOnlyList<LayoutOrientation> Orientations { get; } = Enum.GetValues<LayoutOrientation>();
    public IReadOnlyList<BackgroundFit> Fits { get; } = Enum.GetValues<BackgroundFit>();
    public IReadOnlyList<string> ClockFormats { get; } = ["HH:mm", "HH:mm:ss", "h:mm tt", "h:mm"];
    public IReadOnlyList<string> DateFormats { get; } = ["dddd d MMMM", "ddd d MMM", "dd/MM/yyyy", "MMMM yyyy", "dddd"];
    public IReadOnlyList<string> Swatches { get; } = ["#FFFFFF", "#BFC7D5", "#7C8799", "#FF5A5F", "#FF7A59", "#FFC247", "#76B900", "#3DDC97", "#22D3EE", "#4F8CFF", "#A78BFA", "#F472B6"];
    public IReadOnlyList<(string From, string To)> BadgePresets { get; } = [("#A92F2C", "#CB6236"), ("#629A00", "#79AB51"), ("#0068B5", "#566D98"), ("#4A4A4A", "#707070"), ("#7C3AED", "#DB2777"), ("#0EA5E9", "#22D3EE")];

    [ObservableProperty] public partial WidgetViewModel? SelectedWidget { get; set; }
    [ObservableProperty] public partial LayoutOrientation Orientation { get; set; } = LayoutOrientation.Portrait;
    [ObservableProperty] public partial int Brightness { get; set; } = 80;
    [ObservableProperty] public partial bool Fahrenheit { get; set; }
    [ObservableProperty] public partial string? BackgroundPath { get; set; }
    [ObservableProperty] public partial BackgroundFit BackgroundFit { get; set; } = BackgroundFit.Fill;
    [ObservableProperty] public partial double CropX { get; set; } = 0.5;
    [ObservableProperty] public partial double CropY { get; set; } = 0.5;
    [ObservableProperty] public partial double StartSeconds { get; set; }
    [ObservableProperty] public partial double? DurationSeconds { get; set; }
    [ObservableProperty] public partial ImageSource? BackgroundPreview { get; set; }
    [ObservableProperty] public partial bool BackgroundDirty { get; set; }

    [ObservableProperty] public partial EngineState EngineState { get; set; } = EngineState.Starting;
    [ObservableProperty] public partial string EngineDetail { get; set; } = "Starting…";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasApplied { get; set; }
    [ObservableProperty] public partial string SensorFilter { get; set; } = "";
    [ObservableProperty] public partial bool StartWithWindows { get; set; }

    public int CanvasWidth => Orientation == LayoutOrientation.Portrait ? ScreenLayout.PanelHeight : ScreenLayout.PanelWidth;
    public int CanvasHeight => Orientation == LayoutOrientation.Portrait ? ScreenLayout.PanelWidth : ScreenLayout.PanelHeight;
    public bool IsPortrait => Orientation == LayoutOrientation.Portrait;
    public bool IsConnected => EngineState == EngineState.Connected;
    public bool IsKanaliBlocking => EngineState == EngineState.BlockedByKanali;
    public bool HasBackground => !string.IsNullOrEmpty(BackgroundPath);
    public bool IsVideoBackground => HasBackground && !MediaPreparer.IsImage(BackgroundPath!);
    public string BackgroundName => HasBackground ? Path.GetFileName(BackgroundPath!) : "No background — black screen";
    public string WidgetCount => $"{Widgets.Count} / {LayoutCompiler.MaxWidgets}";

    public MainViewModel(SensorHub sensors, DisplayEngine engine, MediaPreparer preparer, AppStorage storage)
    {
        _sensors = sensors;
        _engine = engine;
        _preparer = preparer;
        _storage = storage;
        _text = new WidgetText(sensors);
        _ui = Dispatcher.CurrentDispatcher;

        _engine.StateChanged += (state, detail) => _ui.BeginInvoke(() => OnEngineState(state, detail));
        _engine.Log += line => _ui.BeginInvoke(() => AddLog(line));

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshPreviews(), _ui);
        _saveDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => { _saveDebounce!.Stop(); Save(); }, _ui) { IsEnabled = false };
        _liveDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (_, _) => { _liveDebounce!.Stop(); await PushLiveAsync(); }, _ui) { IsEnabled = false };

        StartWithWindows = Autostart.IsEnabled();
        Load(storage.LoadLayout());
        _tick.Start();
        RefreshSensorList();
    }

    // ------------------------------------------------------------------ state

    private ScreenLayout BuildLayout() => new()
    {
        Orientation = Orientation,
        Brightness = Brightness,
        Fahrenheit = Fahrenheit,
        Background = new BackgroundMedia
        {
            SourcePath = BackgroundPath,
            Fit = BackgroundFit,
            CropX = CropX,
            CropY = CropY,
            StartSeconds = StartSeconds,
            DurationSeconds = DurationSeconds,
        },
        Widgets = Widgets.Select(w => w.ToModel()).ToList(),
    };

    private void Load(ScreenLayout layout)
    {
        _loading = true;
        try
        {
            Orientation = layout.Orientation;
            Brightness = layout.Brightness;
            Fahrenheit = layout.Fahrenheit;
            BackgroundPath = layout.Background.SourcePath;
            BackgroundFit = layout.Background.Fit;
            CropX = layout.Background.CropX;
            CropY = layout.Background.CropY;
            StartSeconds = layout.Background.StartSeconds;
            DurationSeconds = layout.Background.DurationSeconds;
            Widgets.Clear();
            foreach (var w in layout.Widgets)
                AddWidgetVm(WidgetViewModel.FromModel(w));
            SelectedWidget = Widgets.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
        RefreshPreviews();
        _ = RenderBackgroundPreviewAsync();
    }

    private void AddWidgetVm(WidgetViewModel vm)
    {
        vm.Changed += _ => OnLayoutChanged(needsConfig: false);
        Widgets.Add(vm);
        OnPropertyChanged(nameof(WidgetCount));
    }

    private void Save()
    {
        try
        {
            _storage.SaveLayout(BuildLayout());
        }
        catch (Exception ex)
        {
            AddLog($"save failed: {ex.Message}");
        }
    }

    private void OnLayoutChanged(bool needsConfig)
    {
        if (_loading)
            return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
        if (HasApplied && IsConnected)
        {
            _liveNeedsConfig |= needsConfig;
            _liveDebounce.Stop();
            _liveDebounce.Start();
        }
    }

    /// <summary>Live editing: overlay changes go straight to the cooler; orientation/brightness re-apply settings.</summary>
    private async Task PushLiveAsync()
    {
        if (IsBusy || !IsConnected)
            return;
        var layout = BuildLayout();
        var needsConfig = _liveNeedsConfig;
        _liveNeedsConfig = false;
        try
        {
            if (needsConfig)
                await _engine.ApplyAsync(layout, includeBackground: false);
            else
                await _engine.PushOverlayAsync(layout);
        }
        catch (Exception ex)
        {
            Status = "Live update failed: " + ex.Message;
            AddLog(Status);
        }
    }

    partial void OnOrientationChanged(LayoutOrientation value)
    {
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(IsPortrait));
        if (_loading)
            return;
        // Keep widgets on the canvas when it turns.
        foreach (var w in Widgets)
        {
            w.Width = Math.Min(w.Width, CanvasWidth);
            w.X = Math.Clamp(w.X, 0, Math.Max(0, CanvasWidth - w.Width));
            w.Y = Math.Clamp(w.Y, 0, Math.Max(0, CanvasHeight - w.Height));
        }
        BackgroundDirty = HasBackground;
        _ = RenderBackgroundPreviewAsync();
        OnLayoutChanged(needsConfig: true);
    }

    partial void OnBrightnessChanged(int value) => OnLayoutChanged(needsConfig: true);

    partial void OnFahrenheitChanged(bool value)
    {
        RefreshPreviews();
        OnLayoutChanged(needsConfig: false);
    }

    partial void OnBackgroundPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasBackground));
        OnPropertyChanged(nameof(IsVideoBackground));
        OnPropertyChanged(nameof(BackgroundName));
        BackgroundEdited();
    }

    partial void OnBackgroundFitChanged(BackgroundFit value) => BackgroundEdited();
    partial void OnCropXChanged(double value) => BackgroundEdited();
    partial void OnCropYChanged(double value) => BackgroundEdited();
    partial void OnStartSecondsChanged(double value) => BackgroundEdited();
    partial void OnDurationSecondsChanged(double? value) => BackgroundEdited();

    private void BackgroundEdited()
    {
        if (_loading)
            return;
        BackgroundDirty = true;
        _saveDebounce.Stop();
        _saveDebounce.Start();
        _ = RenderBackgroundPreviewAsync();
    }

    partial void OnSensorFilterChanged(string value) => RefreshSensorList();

    partial void OnStartWithWindowsChanged(bool value)
    {
        try
        {
            if (value == Autostart.IsEnabled())
                return;
            if (value)
                Autostart.Enable();
            else
                Autostart.Disable();
            Status = value ? "Will start with Windows (in the tray, no UAC prompt)." : "Autostart removed.";
        }
        catch (Exception ex)
        {
            Status = "Autostart: " + ex.Message;
        }
    }

    private void OnEngineState(EngineState state, string detail)
    {
        var wasConnected = IsConnected;
        EngineState = state;
        EngineDetail = detail;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsKanaliBlocking));
        AddLog(detail);
        if (state == EngineState.Connected && !IsBusy)
            Status = "";

        // Bring the saved design back on the cooler after a (re)connect.
        if (!wasConnected && state == EngineState.Connected && _storage.AutoApply && !IsBusy)
            _ = ApplyCoreAsync(includeBackground: true, quiet: true);
    }

    // ------------------------------------------------------------------ previews

    private void RefreshPreviews()
    {
        var fahrenheit = Fahrenheit;
        foreach (var w in Widgets)
            w.PreviewText = _text.Resolve(w.ToModel(), fahrenheit);
    }

    public void RefreshSensorList()
    {
        var filter = SensorFilter.Trim();
        var list = _sensors.Catalog
            .Where(s => filter.Length == 0
                || s.Display.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Hardware).ThenBy(s => s.Type).ThenBy(s => s.Name)
            .ToList();
        // Keep the selected widget's sensor visible in the list.
        if (SelectedWidget?.SensorId is { } current && list.All(s => s.Id != current) && _sensors.Info(current) is { } info)
            list.Insert(0, info);
        FilteredSensors.Clear();
        foreach (var s in list)
            FilteredSensors.Add(s);
    }

    public SensorInfo? SelectedSensor
    {
        get => SelectedWidget?.SensorId is { } id ? FilteredSensors.FirstOrDefault(s => s.Id == id) : null;
        set
        {
            if (SelectedWidget is null || value is null)
                return;
            SelectedWidget.SensorId = value.Id;
            OnPropertyChanged();
            RefreshPreviews();
        }
    }

    partial void OnSelectedWidgetChanged(WidgetViewModel? oldValue, WidgetViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
        RefreshSensorList();
        OnPropertyChanged(nameof(SelectedSensor));
    }

    private async Task RenderBackgroundPreviewAsync()
    {
        _previewCts?.Cancel();
        if (!HasBackground)
        {
            BackgroundPreview = null;
            return;
        }
        var cts = _previewCts = new CancellationTokenSource();
        var media = BuildLayout().Background;
        var orientation = Orientation;
        var png = Path.Combine(_storage.PreviewDir, $"preview-{Guid.NewGuid():N}.png");
        try
        {
            await Task.Run(() => _preparer.RenderPreviewAsync(media, orientation, png, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(png);
            image.EndInit();
            image.Freeze();
            BackgroundPreview = image;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "Preview failed: " + ex.Message;
        }
        finally
        {
            try { File.Delete(png); } catch { }
        }
    }

    private void AddLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (LogLines.Count > 300)
            LogLines.RemoveAt(0);
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void AddWidget(WidgetContent content)
    {
        if (Widgets.Count >= LayoutCompiler.MaxWidgets)
        {
            Status = $"The cooler shows at most {LayoutCompiler.MaxWidgets} widgets.";
            return;
        }
        var width = Math.Min(800, CanvasWidth - 80);
        var vm = new WidgetViewModel
        {
            Content = content,
            X = (CanvasWidth - width) / 2,
            Y = Math.Min(CanvasHeight - 200, 100 + Widgets.Count * 60),
            Width = width,
            Align = Layout.TextAlign.Center,
            Size = content switch { WidgetContent.Clock => 160, WidgetContent.CpuName or WidgetContent.GpuName => 44, _ => 90 },
            Format = content == WidgetContent.Date ? "dddd d MMMM" : "HH:mm",
            Text = "Your text",
        };
        if (content is WidgetContent.CpuName or WidgetContent.GpuName)
        {
            vm.Badge = true;
            (vm.BadgeFrom, vm.BadgeTo) = content == WidgetContent.GpuName ? ("#629A00", "#79AB51") : ("#A92F2C", "#CB6236");
        }
        if (content == WidgetContent.Sensor)
        {
            var cpuTemp = _sensors.Catalog.FirstOrDefault(s => s.Type == "Temperature" && s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                ?? _sensors.Catalog.FirstOrDefault(s => s.Type == "Temperature");
            vm.SensorId = cpuTemp?.Id;
        }
        AddWidgetVm(vm);
        SelectedWidget = vm;
        RefreshPreviews();
        OnLayoutChanged(needsConfig: false);
    }

    [RelayCommand]
    private void DeleteWidget()
    {
        if (SelectedWidget is not { } w)
            return;
        var index = Widgets.IndexOf(w);
        Widgets.Remove(w);
        OnPropertyChanged(nameof(WidgetCount));
        SelectedWidget = Widgets.Count == 0 ? null : Widgets[Math.Min(index, Widgets.Count - 1)];
        OnLayoutChanged(needsConfig: false);
    }

    [RelayCommand]
    private void DuplicateWidget()
    {
        if (SelectedWidget is not { } w || Widgets.Count >= LayoutCompiler.MaxWidgets)
            return;
        var copy = w.Duplicate();
        AddWidgetVm(copy);
        SelectedWidget = copy;
        RefreshPreviews();
        OnLayoutChanged(needsConfig: false);
    }

    [RelayCommand]
    private void SetColor(string hex)
    {
        if (SelectedWidget is not null)
            SelectedWidget.Color = hex;
    }

    [RelayCommand]
    private void SetBadgePreset((string From, string To) preset)
    {
        if (SelectedWidget is null)
            return;
        SelectedWidget.BadgeFrom = preset.From;
        SelectedWidget.BadgeTo = preset.To;
        SelectedWidget.Badge = true;
    }

    [RelayCommand]
    private void ChooseBackground()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a background",
            Filter = "Images, GIFs and videos|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.mp4;*.mkv;*.webm;*.mov;*.avi|All files|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        SetBackground(dialog.FileName);
    }

    public void SetBackground(string path)
    {
        CropX = 0.5;
        CropY = 0.5;
        StartSeconds = 0;
        DurationSeconds = null;
        BackgroundPath = path;
    }

    [RelayCommand]
    private void ClearBackground() => BackgroundPath = null;

    [RelayCommand]
    private void CloseKanali()
    {
        DisplayEngine.CloseKanali();
        Status = "KANALI closed. Connecting…";
    }

    [RelayCommand]
    private Task Apply() => ApplyCoreAsync(includeBackground: true, quiet: false);

    private async Task ApplyCoreAsync(bool includeBackground, bool quiet)
    {
        if (IsBusy)
            return;
        if (!IsConnected)
        {
            Status = IsKanaliBlocking ? "Close KANALI first — it owns the cooler." : "The cooler is not connected.";
            return;
        }
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => Status = s);
            await _engine.ApplyAsync(BuildLayout(), includeBackground, progress);
            HasApplied = true;
            BackgroundDirty = false;
            _storage.AutoApply = true;
            Save();
            Status = quiet ? "Restored your design on the cooler." : "Applied to the cooler.";
        }
        catch (Exception ex)
        {
            Status = "Apply failed: " + ex.Message;
            AddLog(Status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Shutdown()
    {
        _tick.Stop();
        Save();
    }

    /// <summary>Moves a widget during a drag, clamped to the canvas.</summary>
    public void MoveWidget(WidgetViewModel w, int x, int y)
    {
        w.X = Math.Clamp(x, 0, Math.Max(0, CanvasWidth - w.Width));
        w.Y = Math.Clamp(y, 0, Math.Max(0, CanvasHeight - w.Height));
    }
}
