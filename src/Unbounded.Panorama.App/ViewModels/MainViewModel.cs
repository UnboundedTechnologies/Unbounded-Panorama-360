using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
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

public enum AlignMode { Left, CenterH, Right, Top, Middle, Bottom }

public enum DrawTool { Pen, Line, Rectangle, Ellipse, Eraser }

public sealed partial class MainViewModel : ObservableObject
{
    public static readonly string[] MediaExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".mp4", ".mkv", ".webm", ".mov", ".avi"];
    public static readonly string[] SensorTypes = ["All", "Temperature", "Load", "Clock", "Power", "Fan", "Data"];

    private readonly SensorHub _sensors;
    private readonly DisplayEngine _engine;
    private readonly MediaPreparer _preparer;
    private readonly WidgetText _text;
    private readonly AppStorage _storage;
    private readonly PresetStore _presets;
    private readonly MediaLibrary _library;
    private readonly UndoHistory _history = new();
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _commitDebounce;
    private readonly DispatcherTimer _liveDebounce;
    private readonly ObservableCollection<SensorRow> _sensorRows = [];
    private bool _liveNeedsConfig;
    private bool _loading;
    private string _backgroundKey = "";
    private CancellationTokenSource? _previewCts;
    private YouTubeImporter? _youTube;

    public ObservableCollection<WidgetViewModel> Widgets { get; } = [];
    public ObservableCollection<PresetItem> Presets { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ListCollectionView SensorView { get; }

    public IReadOnlyList<WidgetContent> ContentKinds { get; } = Enum.GetValues<WidgetContent>();
    public IReadOnlyList<FontWeightName> Weights { get; } = Enum.GetValues<FontWeightName>();
    public IReadOnlyList<Layout.TextAlign> Aligns { get; } = Enum.GetValues<Layout.TextAlign>();
    public IReadOnlyList<BackgroundFit> Fits { get; } = Enum.GetValues<BackgroundFit>();
    public IReadOnlyList<string> SensorTypeChoices => SensorTypes;
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
    [ObservableProperty] public partial string SensorTypeFilter { get; set; } = "All";
    [ObservableProperty] public partial SensorRow? SelectedSensorRow { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }

    [ObservableProperty] public partial bool SnapEnabled { get; set; } = true;
    [ObservableProperty] public partial bool ShowCurveGuide { get; set; } = true;
    [ObservableProperty] public partial string DragInfo { get; set; } = "";
    [ObservableProperty] public partial PresetItem? SelectedPreset { get; set; }
    [ObservableProperty] public partial string PresetName { get; set; } = "";

    // Shell
    [ObservableProperty] public partial string CurrentPage { get; set; } = "Studio";
    [ObservableProperty] public partial string CpuTempText { get; set; } = "--";
    [ObservableProperty] public partial string GpuTempText { get; set; } = "--";
    public string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    // Library
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
    public ObservableCollection<DeviceMediaViewModel> DeviceItems { get; } = [];
    [ObservableProperty] public partial string LibraryTab { get; set; } = "Mine";
    [ObservableProperty] public partial bool DeviceListBusy { get; set; }
    [ObservableProperty] public partial string DeviceListNote { get; set; } = "";

    // Background from the cooler's own storage
    [ObservableProperty] public partial string? BackgroundDeviceFile { get; set; }

    // Drawing
    [ObservableProperty] public partial string? Drawing { get; set; }
    [ObservableProperty] public partial string EditorMode { get; set; } = "Select";
    [ObservableProperty] public partial DrawTool DrawTool { get; set; } = DrawTool.Pen;
    [ObservableProperty] public partial string DrawColor { get; set; } = "#00E5FF";
    [ObservableProperty] public partial double DrawSize { get; set; } = 12;
    public bool IsDrawMode => EditorMode == "Draw";
    public bool HasDrawing => !string.IsNullOrEmpty(Drawing);

    /// <summary>Raised when the drawing is replaced from outside the ink canvas (load, undo, clear).</summary>
    public event Action<string?>? DrawingReplaced;

    public int CanvasWidth => Orientation == LayoutOrientation.Portrait ? ScreenLayout.PanelHeight : ScreenLayout.PanelWidth;
    public int CanvasHeight => Orientation == LayoutOrientation.Portrait ? ScreenLayout.PanelWidth : ScreenLayout.PanelHeight;
    public bool IsPortrait
    {
        get => Orientation == LayoutOrientation.Portrait;
        set { if (value) Orientation = LayoutOrientation.Portrait; }
    }
    public bool IsLandscape
    {
        get => Orientation == LayoutOrientation.Landscape;
        set { if (value) Orientation = LayoutOrientation.Landscape; }
    }
    public bool IsConnected => EngineState == EngineState.Connected;
    public bool IsKanaliBlocking => EngineState == EngineState.BlockedByKanali;
    public bool HasBackground => !string.IsNullOrEmpty(BackgroundPath);
    public bool IsVideoBackground => HasBackground && !MediaPreparer.IsImage(BackgroundPath!);
    public string BackgroundName => BackgroundDeviceFile is { } f
        ? $"From the cooler: {f}"
        : HasBackground ? Path.GetFileName(BackgroundPath!) : "No background — black screen";
    public int VisibleCount => Widgets.Count(w => !w.Hidden);
    public string WidgetCount => $"{VisibleCount} / {LayoutCompiler.MaxWidgets} shown";
    public IEnumerable<WidgetViewModel> Selection => Widgets.Where(w => w.IsSelected);
    public int SelectionCount => Widgets.Count(w => w.IsSelected);
    public bool HasMultiSelection => SelectionCount > 1;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool CanDeletePreset => SelectedPreset is { IsBuiltIn: false };

    public MainViewModel(SensorHub sensors, DisplayEngine engine, MediaPreparer preparer, AppStorage storage)
    {
        _sensors = sensors;
        _engine = engine;
        _preparer = preparer;
        _storage = storage;
        _presets = new PresetStore(storage.RoamingDir);
        _library = new MediaLibrary(storage.RoamingDir, storage.LocalDir);
        _text = new WidgetText(sensors);
        _ui = Dispatcher.CurrentDispatcher;

        SensorView = new ListCollectionView(_sensorRows);
        SensorView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Group)));

        _engine.StateChanged += (state, detail) => _ui.BeginInvoke(() => OnEngineState(state, detail));
        _engine.Log += line => _ui.BeginInvoke(() => AddLog(line));

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshPreviews(), _ui);
        _commitDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { _commitDebounce!.Stop(); Commit(); }, _ui) { IsEnabled = false };
        _liveDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (_, _) => { _liveDebounce!.Stop(); await PushLiveAsync(); }, _ui) { IsEnabled = false };

        StartWithWindows = Autostart.IsEnabled();
        ReloadPresets();
        Load(storage.LoadLayout(), keepSelection: false);
        _history.Record(BuildLayout().ToJson());
        _tick.Start();
        RebuildSensorRows();
        ReloadLibrary();
    }

    // ================================================================== layout state

    public ScreenLayout BuildLayout() => new()
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
            DeviceFile = BackgroundDeviceFile,
        },
        Widgets = Widgets.Select(w => w.ToModel()).ToList(),
        Drawing = Drawing,
    };

    private void Load(ScreenLayout layout, bool keepSelection)
    {
        var selectedIds = keepSelection ? Selection.Select(w => w.Id).ToHashSet() : [];
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
            BackgroundDeviceFile = layout.Background.DeviceFile;
            if (Drawing != layout.Drawing)
            {
                Drawing = layout.Drawing;
                DrawingReplaced?.Invoke(Drawing);
            }
            Widgets.Clear();
            foreach (var w in layout.Widgets)
            {
                var vm = WidgetViewModel.FromModel(w);
                vm.IsSelected = selectedIds.Contains(vm.Id);
                Attach(vm);
                Widgets.Add(vm);
            }
            SelectedWidget = Widgets.FirstOrDefault(w => w.IsSelected) ?? (keepSelection ? null : Widgets.FirstOrDefault());
            if (SelectedWidget is not null)
                SelectedWidget.IsSelected = true;
        }
        finally
        {
            _loading = false;
        }
        NotifyCounts();
        RefreshPreviews();
        var key = BackgroundKey();
        if (key != _backgroundKey)
        {
            _backgroundKey = key;
            _ = RenderBackgroundPreviewAsync();
        }
    }

    private string BackgroundKey() => $"{BackgroundDeviceFile}|{BackgroundPath}|{BackgroundFit}|{CropX}|{CropY}|{StartSeconds}|{Orientation}";

    private void Attach(WidgetViewModel vm)
    {
        vm.Changed += _ => OnLayoutChanged(needsConfig: false);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WidgetViewModel.Hidden))
                NotifyCounts();
        };
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(WidgetCount));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultiSelection));
    }

    /// <summary>Saves, records an undo step, and pushes live to the cooler.</summary>
    private void OnLayoutChanged(bool needsConfig)
    {
        if (_loading)
            return;
        _commitDebounce.Stop();
        _commitDebounce.Start();
        if (HasApplied && IsConnected)
        {
            _liveNeedsConfig |= needsConfig;
            _liveDebounce.Stop();
            _liveDebounce.Start();
        }
    }

    private void Commit()
    {
        var layout = BuildLayout();
        _history.Record(layout.ToJson());
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        try
        {
            _storage.SaveLayout(layout);
        }
        catch (Exception ex)
        {
            AddLog($"save failed: {ex.Message}");
        }
    }

    /// <summary>Flushes a pending commit right away (end of a drag, before undo).</summary>
    public void CommitNow()
    {
        if (_commitDebounce.IsEnabled)
        {
            _commitDebounce.Stop();
            Commit();
        }
    }

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
        OnPropertyChanged(nameof(IsLandscape));
        if (_loading)
            return;
        foreach (var w in Widgets)
        {
            w.Width = Math.Min(w.Width, CanvasWidth);
            w.X = Math.Clamp(w.X, 0, Math.Max(0, CanvasWidth - w.Width));
            w.Y = Math.Clamp(w.Y, 0, Math.Max(0, CanvasHeight - w.Height));
        }
        BackgroundDirty = true;
        _backgroundKey = BackgroundKey();
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

    partial void OnBackgroundDeviceFileChanged(string? value)
    {
        OnPropertyChanged(nameof(BackgroundName));
        BackgroundEdited();
    }

    partial void OnDrawingChanged(string? value) => OnPropertyChanged(nameof(HasDrawing));

    partial void OnEditorModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsDrawMode));
        if (value == "Draw")
            ClearSelection();
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page;
        if (page == "Library" && IsConnected && DeviceItems.Count == 0)
            _ = RefreshDeviceAsync();
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
        OnLayoutChanged(needsConfig: false);
        var key = BackgroundKey();
        if (key != _backgroundKey)
        {
            _backgroundKey = key;
            _ = RenderBackgroundPreviewAsync();
        }
    }

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

        if (!wasConnected && state == EngineState.Connected && _storage.AutoApply && !IsBusy)
            _ = ApplyCoreAsync(includeBackground: true, quiet: true);
    }

    // ================================================================== selection

    partial void OnSelectedWidgetChanged(WidgetViewModel? oldValue, WidgetViewModel? newValue)
    {
        if (newValue is not null && !newValue.IsSelected)
            newValue.IsSelected = true;
        NotifyCounts();
        SyncSelectedSensorRow();
    }

    /// <summary>Plain click selects one; Ctrl/Shift-click toggles.</summary>
    public void Select(WidgetViewModel w, bool additive)
    {
        if (additive)
        {
            w.IsSelected = !w.IsSelected;
            SelectedWidget = w.IsSelected ? w : Selection.LastOrDefault();
        }
        else if (!w.IsSelected)
        {
            foreach (var other in Widgets)
                other.IsSelected = other == w;
            SelectedWidget = w;
        }
        else
        {
            SelectedWidget = w;
        }
        NotifyCounts();
    }

    public void SelectOnly(IEnumerable<WidgetViewModel> widgets, bool additive)
    {
        var set = widgets.ToHashSet();
        foreach (var w in Widgets)
            w.IsSelected = set.Contains(w) || (additive && w.IsSelected);
        SelectedWidget = Selection.FirstOrDefault();
        NotifyCounts();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var w in Widgets)
            w.IsSelected = false;
        SelectedWidget = null;
        NotifyCounts();
    }

    [RelayCommand]
    private void SelectAll() => SelectOnly(Widgets.Where(w => !w.Locked), additive: false);

    // ================================================================== sensors

    partial void OnSensorFilterChanged(string value) => RebuildSensorRows();
    partial void OnSensorTypeFilterChanged(string value) => RebuildSensorRows();

    private void RebuildSensorRows()
    {
        var filter = SensorFilter.Trim();
        var type = SensorTypeFilter;
        var list = _sensors.Catalog
            .Where(s => type == "All" || s.Type == type || (type == "Data" && s.Type == "SmallData"))
            .Where(s => filter.Length == 0 || s.Display.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.CategoryOrder).ThenBy(s => s.Hardware).ThenBy(s => s.Type).ThenBy(s => s.Name)
            .ToList();
        _sensorRows.Clear();
        foreach (var s in list)
            _sensorRows.Add(new SensorRow(s));
        UpdateSensorValues();
        SyncSelectedSensorRow();
    }

    private void UpdateSensorValues()
    {
        foreach (var row in _sensorRows)
            row.ValueText = _sensors.Format(row.Id, row.Type is "Temperature" or "Load" ? 0 : 1, showUnit: true, Fahrenheit);
    }

    private bool _syncingSensor;

    private void SyncSelectedSensorRow()
    {
        _syncingSensor = true;
        var id = _sensors.Resolve(SelectedWidget?.SensorId);
        SelectedSensorRow = id is null ? null : _sensorRows.FirstOrDefault(r => r.Id == id);
        _syncingSensor = false;
    }

    partial void OnSelectedSensorRowChanged(SensorRow? value)
    {
        if (_syncingSensor || value is null || SelectedWidget is not { IsSensorContent: true } w)
            return;
        w.SensorId = value.Id;
        RefreshPreviews();
    }

    // ================================================================== previews

    private int _sensorRefreshCountdown;

    private void RefreshPreviews()
    {
        var fahrenheit = Fahrenheit;
        foreach (var w in Widgets)
            w.PreviewText = _text.Resolve(w.ToModel(), fahrenheit);
        UpdateSensorValues();
        CpuTempText = _sensors.Format(SensorHub.RolePrefix + "cpu-temp", 0, true, fahrenheit);
        GpuTempText = _sensors.Format(SensorHub.RolePrefix + "gpu-temp", 0, true, fahrenheit);
        // The sensor list can grow after start-up (hardware discovered late); refresh it now and then.
        if (--_sensorRefreshCountdown <= 0)
        {
            _sensorRefreshCountdown = 30;
            if (_sensors.Catalog.Count != _knownSensorCount)
            {
                _knownSensorCount = _sensors.Catalog.Count;
                RebuildSensorRows();
            }
        }
    }

    private int _knownSensorCount = -1;

    private async Task RenderBackgroundPreviewAsync()
    {
        _previewCts?.Cancel();
        if (BackgroundDeviceFile is { } deviceFile)
        {
            // Our own uploads have a cached still; device presets have none.
            var still = DeviceThumbnailPath(deviceFile);
            BackgroundPreview = still is null ? null : LoadImage(still);
            return;
        }
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

    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Still of an earlier upload: "upn360-&lt;key&gt;.…" maps to the media cache "&lt;key&gt;.png".</summary>
    private string? DeviceThumbnailPath(string deviceName)
    {
        if (!deviceName.StartsWith(DisplayEngine.RemotePrefix, StringComparison.Ordinal))
            return null;
        var key = deviceName[DisplayEngine.RemotePrefix.Length..].Split('.')[0];
        var png = Path.Combine(_storage.MediaCacheDir, key + ".png");
        return File.Exists(png) ? png : null;
    }

    private void AddLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (LogLines.Count > 300)
            LogLines.RemoveAt(0);
    }

    // ================================================================== widgets

    [RelayCommand]
    private void AddWidget(WidgetContent content)
    {
        if (VisibleCount >= LayoutCompiler.MaxWidgets)
        {
            Status = $"The cooler shows at most {LayoutCompiler.MaxWidgets} widgets at once. Hide or delete one first.";
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
            vm.SensorId = _sensors.Resolve(SensorHub.RolePrefix + "cpu-temp");
        Attach(vm);
        Widgets.Add(vm);
        Select(vm, additive: false);
        RefreshPreviews();
        OnLayoutChanged(needsConfig: false);
    }

    [RelayCommand]
    private void DeleteWidget()
    {
        var doomed = Selection.ToList();
        if (doomed.Count == 0)
            return;
        var index = Widgets.IndexOf(doomed[0]);
        foreach (var w in doomed)
            Widgets.Remove(w);
        SelectedWidget = null;
        if (Widgets.Count > 0)
            Select(Widgets[Math.Min(index, Widgets.Count - 1)], additive: false);
        NotifyCounts();
        OnLayoutChanged(needsConfig: false);
    }

    [RelayCommand]
    private void DuplicateWidget()
    {
        var copies = Selection.Select(w => w.Duplicate()).ToList();
        if (copies.Count == 0)
            return;
        if (VisibleCount + copies.Count(c => !c.Hidden) > LayoutCompiler.MaxWidgets)
        {
            Status = $"The cooler shows at most {LayoutCompiler.MaxWidgets} widgets at once.";
            return;
        }
        foreach (var c in copies)
        {
            Attach(c);
            Widgets.Add(c);
        }
        SelectOnly(copies, additive: false);
        RefreshPreviews();
        OnLayoutChanged(needsConfig: false);
    }

    [RelayCommand]
    private void ToggleHidden(WidgetViewModel w)
    {
        if (w.Hidden && VisibleCount >= LayoutCompiler.MaxWidgets)
        {
            Status = $"The cooler shows at most {LayoutCompiler.MaxWidgets} widgets at once.";
            return;
        }
        w.Hidden = !w.Hidden;
    }

    [RelayCommand]
    private void ToggleLocked(WidgetViewModel w) => w.Locked = !w.Locked;

    [RelayCommand]
    private void SetColor(string hex)
    {
        foreach (var w in Selection)
            w.Color = hex;
    }

    [RelayCommand]
    private void SetBadgePreset((string From, string To) preset)
    {
        foreach (var w in Selection)
        {
            w.BadgeFrom = preset.From;
            w.BadgeTo = preset.To;
            w.Badge = true;
        }
    }

    /// <summary>Moves a widget, clamped to the canvas.</summary>
    public void MoveWidget(WidgetViewModel w, int x, int y)
    {
        if (w.Locked)
            return;
        w.X = Math.Clamp(x, 0, Math.Max(0, CanvasWidth - w.Width));
        w.Y = Math.Clamp(y, 0, Math.Max(0, CanvasHeight - w.Height));
    }

    public void NudgeSelection(int dx, int dy)
    {
        var movable = Selection.Where(w => !w.Locked).ToList();
        if (movable.Count == 0)
            return;
        var box = Box.Union(movable.Select(w => w.Bounds));
        dx = (int)Math.Clamp(dx, -box.X, CanvasWidth - box.Right);
        dy = (int)Math.Clamp(dy, -box.Y, CanvasHeight - box.Bottom);
        foreach (var w in movable)
        {
            w.X += dx;
            w.Y += dy;
        }
    }

    // ================================================================== align / distribute

    [RelayCommand]
    private void Align(AlignMode mode)
    {
        var items = Selection.Where(w => !w.Locked).ToList();
        if (items.Count == 0)
            return;
        // One widget aligns to the screen; several align to their combined box.
        var reference = items.Count == 1 ? new Box(0, 0, CanvasWidth, CanvasHeight) : Box.Union(items.Select(w => w.Bounds));
        foreach (var w in items)
        {
            switch (mode)
            {
                case AlignMode.Left: w.X = (int)reference.X; break;
                case AlignMode.CenterH: w.X = (int)Math.Round(reference.CenterX - w.Width / 2.0); break;
                case AlignMode.Right: w.X = (int)(reference.Right - w.Width); break;
                case AlignMode.Top: w.Y = (int)reference.Y; break;
                case AlignMode.Middle: w.Y = (int)Math.Round(reference.CenterY - w.Height / 2.0); break;
                case AlignMode.Bottom: w.Y = (int)(reference.Bottom - w.Height); break;
            }
        }
    }

    /// <summary>Centres the whole selection on the screen, keeping relative positions.</summary>
    [RelayCommand]
    private void CenterOnScreen(string axis)
    {
        var items = Selection.Where(w => !w.Locked).ToList();
        if (items.Count == 0)
            return;
        var box = Box.Union(items.Select(w => w.Bounds));
        var dx = axis is "H" or "Both" ? (int)Math.Round(CanvasWidth / 2.0 - box.CenterX) : 0;
        var dy = axis is "V" or "Both" ? (int)Math.Round(CanvasHeight / 2.0 - box.CenterY) : 0;
        NudgeSelection(dx, dy);
    }

    /// <summary>Equal gaps between three or more widgets.</summary>
    [RelayCommand]
    private void Distribute(string axis)
    {
        var items = Selection.Where(w => !w.Locked).ToList();
        if (items.Count < 3)
        {
            Status = "Select three or more widgets to distribute them evenly.";
            return;
        }
        if (axis == "V")
        {
            var sorted = items.OrderBy(w => w.Y).ToList();
            var span = sorted[^1].Y + sorted[^1].Height - sorted[0].Y;
            var gap = (span - sorted.Sum(w => w.Height)) / (double)(sorted.Count - 1);
            double y = sorted[0].Y;
            foreach (var w in sorted)
            {
                w.Y = (int)Math.Round(y);
                y += w.Height + gap;
            }
        }
        else
        {
            var sorted = items.OrderBy(w => w.X).ToList();
            var span = sorted[^1].X + sorted[^1].Width - sorted[0].X;
            var gap = (span - sorted.Sum(w => w.Width)) / (double)(sorted.Count - 1);
            double x = sorted[0].X;
            foreach (var w in sorted)
            {
                w.X = (int)Math.Round(x);
                x += w.Width + gap;
            }
        }
    }

    /// <summary>Gives every selected widget the width of the primary selection.</summary>
    [RelayCommand]
    private void MatchWidth()
    {
        if (SelectedWidget is not { } primary)
            return;
        foreach (var w in Selection.Where(w => w != primary && !w.Locked))
        {
            w.Width = Math.Min(primary.Width, CanvasWidth);
            w.X = Math.Clamp(w.X, 0, CanvasWidth - w.Width);
        }
    }

    // ================================================================== undo / redo

    [RelayCommand]
    private void Undo()
    {
        CommitNow();
        if (_history.Undo() is { } state)
            Restore(state);
    }

    [RelayCommand]
    private void Redo()
    {
        if (_history.Redo() is { } state)
            Restore(state);
    }

    private void Restore(string state)
    {
        Load(ScreenLayout.FromJson(state), keepSelection: true);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        try
        {
            _storage.SaveLayout(BuildLayout());
        }
        catch
        {
        }
        if (HasApplied && IsConnected)
        {
            _liveNeedsConfig = true;
            _liveDebounce.Stop();
            _liveDebounce.Start();
        }
    }

    // ================================================================== presets

    private void ReloadPresets(string? select = null)
    {
        Presets.Clear();
        foreach (var p in _presets.List())
            Presets.Add(p);
        if (select is not null)
            SelectedPreset = Presets.FirstOrDefault(p => !p.IsBuiltIn && p.Name == select);
    }

    partial void OnSelectedPresetChanged(PresetItem? value)
    {
        OnPropertyChanged(nameof(CanDeletePreset));
        if (value is { IsBuiltIn: false })
            PresetName = value.Name;
    }

    [RelayCommand]
    private void LoadPreset()
    {
        if (SelectedPreset is not { } preset)
            return;
        try
        {
            var layout = _presets.Load(preset);
            if (preset.IsBuiltIn)
            {
                // Templates bring widgets only; keep this screen's orientation, brightness and background.
                var current = BuildLayout();
                layout.Orientation = current.Orientation;
                layout.Brightness = current.Brightness;
                layout.Fahrenheit = current.Fahrenheit;
                layout.Background = current.Background;
            }
            // Template sensor roles become this PC's sensors.
            foreach (var w in layout.Widgets.Where(w => w.SensorId?.StartsWith(SensorHub.RolePrefix, StringComparison.Ordinal) == true))
                w.SensorId = _sensors.Resolve(w.SensorId) ?? w.SensorId;
            Load(layout, keepSelection: false);
            Commit();
            if (HasApplied && IsConnected)
                _ = ApplyCoreAsync(includeBackground: !preset.IsBuiltIn, quiet: false);
            Status = $"Loaded “{preset.Name}”. Ctrl+Z to go back.";
        }
        catch (Exception ex)
        {
            Status = "Could not load preset: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetStore.Clean(PresetName);
        if (name.Length == 0)
        {
            Status = "Type a name for the preset first.";
            return;
        }
        var existed = _presets.Exists(name);
        _presets.Save(name, BuildLayout());
        ReloadPresets(select: name);
        Status = existed ? $"Preset “{name}” updated." : $"Preset “{name}” saved.";
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset)
            return;
        _presets.Delete(preset.Name);
        ReloadPresets();
        Status = $"Preset “{preset.Name}” deleted.";
    }

    // ================================================================== background

    [RelayCommand]
    private void ChooseBackground()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a background",
            Filter = "Images, GIFs and videos|" + string.Join(";", MediaExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        if (dialog.ShowDialog() == true)
            SetBackground(dialog.FileName);
    }

    public static bool IsMediaFile(string path) => MediaExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void SetBackground(string path)
    {
        _loading = true;
        try
        {
            BackgroundDeviceFile = null;
            CropX = 0.5;
            CropY = 0.5;
            StartSeconds = 0;
            DurationSeconds = null;
        }
        finally
        {
            _loading = false;
        }
        BackgroundPath = path;
        _library.Remember(LibraryKind.File, path, null);
        ReloadLibrary();
        Status = $"Background set to {Path.GetFileName(path)}. Apply to send it to the cooler.";
    }

    public YouTubeImporter YouTube => _youTube ??= new YouTubeImporter(_storage.DownloadDir);

    /// <summary>Uses a downloaded YouTube section; the start is cut frame-exactly at conversion.</summary>
    public void UseImportedClip(ImportedClip clip, string? title)
    {
        _loading = true;
        try
        {
            BackgroundDeviceFile = null;
            BackgroundFit = BackgroundFit.Fill;
            CropX = 0.5;
            CropY = 0.5;
            StartSeconds = Math.Round(clip.OffsetSeconds, 3);
            DurationSeconds = Math.Round(clip.LengthSeconds, 3);
        }
        finally
        {
            _loading = false;
        }
        BackgroundPath = clip.FilePath;
        _library.Remember(LibraryKind.YouTube, clip.FilePath, title, StartSeconds, DurationSeconds);
        ReloadLibrary();
        Status = $"Imported “{title}”. Drag the framing sliders to choose the part that fills the screen, then Apply.";
    }

    [RelayCommand]
    private void ClearBackground()
    {
        BackgroundDeviceFile = null;
        BackgroundPath = null;
    }

    // ================================================================== library

    private CancellationTokenSource? _thumbCts;

    private void ReloadLibrary()
    {
        _thumbCts?.Cancel();
        var cts = _thumbCts = new CancellationTokenSource();
        LibraryItems.Clear();
        foreach (var e in _library.Entries)
        {
            var item = new LibraryItemViewModel(e)
            {
                IsCurrent = BackgroundDeviceFile is null && string.Equals(e.Path, BackgroundPath, StringComparison.OrdinalIgnoreCase),
            };
            LibraryItems.Add(item);
        }
        _ = LoadLibraryThumbnailsAsync(LibraryItems.ToList(), cts.Token);
    }

    private async Task LoadLibraryThumbnailsAsync(List<LibraryItemViewModel> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            if (token.IsCancellationRequested)
                return;
            try
            {
                var entry = item.Entry;
                var jpg = await Task.Run(() => _library.ThumbnailAsync(entry, token), token);
                if (jpg is not null)
                    item.Thumbnail = LoadImage(jpg);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A file that cannot be decoded just keeps the placeholder.
            }
        }
    }

    [RelayCommand]
    private void UseLibraryItem(LibraryItemViewModel item)
    {
        var e = item.Entry;
        if (!File.Exists(e.Path))
        {
            Status = "That file is no longer on this PC.";
            return;
        }
        _loading = true;
        try
        {
            BackgroundDeviceFile = null;
            BackgroundFit = BackgroundFit.Fill;
            CropX = 0.5;
            CropY = 0.5;
            StartSeconds = e.StartSeconds;
            DurationSeconds = e.LengthSeconds;
        }
        finally
        {
            _loading = false;
        }
        BackgroundPath = e.Path;
        _library.Remember(e.Kind, e.Path, e.Title, e.StartSeconds, e.LengthSeconds);
        ReloadLibrary();
        CurrentPage = "Studio";
        Status = $"Background: {e.Title}. Apply to send it to the cooler.";
    }

    [RelayCommand]
    private void ForgetLibraryItem(LibraryItemViewModel item)
    {
        _library.Forget(item.Entry, _storage.DownloadDir);
        ReloadLibrary();
        Status = $"Removed “{item.Title}” from the library.";
    }

    [RelayCommand]
    private void RevealLibraryItem(LibraryItemViewModel item)
    {
        if (File.Exists(item.Entry.Path))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Entry.Path}\"");
    }

    [RelayCommand]
    private Task RefreshDevice() => RefreshDeviceAsync();

    private async Task RefreshDeviceAsync()
    {
        if (!IsConnected)
        {
            DeviceListNote = IsKanaliBlocking ? "Close KANALI to see what is stored on the cooler." : "Connect the cooler to see what is stored on it.";
            return;
        }
        DeviceListBusy = true;
        try
        {
            var media = await _engine.ListDeviceMediaAsync();
            DeviceItems.Clear();
            foreach (var m in media.OrderBy(m => m.IsPreset).ThenBy(m => m.Name, StringComparer.Ordinal))
            {
                var item = new DeviceMediaViewModel(m.Name, m.Size, m.IsPreset, m.ReadOnly, m.InUse);
                if (DeviceThumbnailPath(m.Name) is { } png)
                    item.Thumbnail = LoadImage(png);
                DeviceItems.Add(item);
            }
            DeviceListNote = $"{media.Count} files on the cooler · {media.Count(m => m.IsPreset)} TRYX presets";
        }
        catch (Exception ex)
        {
            DeviceListNote = "Could not read the cooler: " + ex.Message;
        }
        finally
        {
            DeviceListBusy = false;
        }
    }

    [RelayCommand]
    private async Task PlayDeviceItem(DeviceMediaViewModel item)
    {
        BackgroundDeviceFile = item.Name;
        CurrentPage = "Studio";
        if (HasDrawing)
            Status = "Note: a drawing can only be baked into your own background, not into a file already on the cooler.";
        await ApplyCoreAsync(includeBackground: true, quiet: false);
        await RefreshDeviceAsync();
    }

    [RelayCommand]
    private async Task DeleteDeviceItem(DeviceMediaViewModel item)
    {
        try
        {
            await _engine.DeleteDeviceMediaAsync(item.Name);
            Status = $"Deleted {item.Name} from the cooler.";
        }
        catch (Exception ex)
        {
            Status = "Delete failed: " + ex.Message;
        }
        await RefreshDeviceAsync();
    }

    // ================================================================== drawing

    /// <summary>Called by the ink canvas after each stroke change.</summary>
    public void SetDrawingFromCanvas(string? isfBase64)
    {
        if (Drawing == isfBase64)
            return;
        Drawing = isfBase64;
        BackgroundEdited();
    }

    [RelayCommand]
    private void ClearDrawing()
    {
        if (!HasDrawing)
            return;
        Drawing = null;
        DrawingReplaced?.Invoke(null);
        BackgroundEdited();
        Status = "Drawing cleared. Ctrl+Z to bring it back.";
    }

    [RelayCommand]
    private void SetDrawColor(string hex) => DrawColor = hex;

    /// <summary>Renders the drawing to a transparent canvas-size PNG for baking into the background.</summary>
    private string? RenderDrawingPng(ScreenLayout layout)
    {
        if (string.IsNullOrEmpty(layout.Drawing))
            return null;
        var strokes = new System.Windows.Ink.StrokeCollection(new MemoryStream(Convert.FromBase64String(layout.Drawing)));
        if (strokes.Count == 0)
            return null;
        var (w, h) = MediaPreparer.CanvasSize(layout.Orientation);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            strokes.Draw(dc);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        var bytes = buffer.ToArray();
        var name = "ink-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 8) + ".png";
        var path = Path.Combine(_storage.MediaCacheDir, name);
        if (!File.Exists(path))
            File.WriteAllBytes(path, bytes);
        return path;
    }

    [RelayCommand]
    private void CloseKanali()
    {
        DisplayEngine.CloseKanali();
        Status = "KANALI closed. Connecting…";
    }

    // ================================================================== apply

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
            var layout = BuildLayout();
            var overlay = includeBackground && layout.Background.DeviceFile is null ? RenderDrawingPng(layout) : null;
            await _engine.ApplyAsync(layout, includeBackground, overlay, progress);
            HasApplied = true;
            BackgroundDirty = false;
            _storage.AutoApply = true;
            Commit();
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
        CommitNow();
        _storage.SaveLayout(BuildLayout());
    }
}
