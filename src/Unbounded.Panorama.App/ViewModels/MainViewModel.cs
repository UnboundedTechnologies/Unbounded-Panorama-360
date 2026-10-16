using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
    public IReadOnlyList<string> Swatches { get; } = ["#FFFFFF", "#000000", "#BFC7D5", "#7C8799", "#FF5A5F", "#FF7A59", "#FFC247", "#76B900", "#3DDC97", "#22D3EE", "#4F8CFF", "#A78BFA", "#F472B6"];

    /// <summary>The few one-click colours on the Draw toolbar; every other colour is in its picker.</summary>
    public IReadOnlyList<string> DrawSwatches { get; } = ["#FFFFFF", "#000000", "#FF5A5F", "#FFC247", "#00E5FF", "#A78BFA"];
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
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; } = true;
    [ObservableProperty] public partial string ProgressDetail { get; set; } = "";
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

    /// <summary>The ARGB page (all RGB devices through OpenRGB).</summary>
    public LightingViewModel Lighting { get; }

    // Library
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
    public ObservableCollection<DeviceMediaViewModel> DeviceItems { get; } = [];
    [ObservableProperty] public partial string LibraryTab { get; set; } = "Mine";
    [ObservableProperty] public partial bool DeviceListBusy { get; set; }
    [ObservableProperty] public partial string DeviceListNote { get; set; } = "";

    // Background from the cooler's own storage
    [ObservableProperty] public partial string? BackgroundDeviceFile { get; set; }

    // Exact background placement (cooler pixels on the canvas)
    [ObservableProperty] public partial int SourceWidth { get; set; }
    [ObservableProperty] public partial int SourceHeight { get; set; }
    [ObservableProperty] public partial double? MediaX { get; set; }
    [ObservableProperty] public partial double? MediaY { get; set; }
    [ObservableProperty] public partial double? MediaWidth { get; set; }
    [ObservableProperty] public partial ScreenArea Area { get; set; } = ScreenArea.Whole;
    [ObservableProperty] public partial ImageSource? SourceFrame { get; set; }
    private string _sourceFrameKey = "";

    /// <summary>The clip the cooler plays, as a light H.264 copy the editor loops (null for images).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasClip))] public partial Uri? SourceClip { get; set; }

    public bool HasClip => SourceClip is not null;

    /// <summary>"Preparing the video preview… 40 %" while the copy is made; empty otherwise.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasClipStatus))] public partial string ClipStatus { get; set; } = "";

    public bool HasClipStatus => ClipStatus.Length > 0;

    /// <summary>Play / pause of the video in the editor.</summary>
    [ObservableProperty] public partial bool PreviewPlaying { get; set; } = true;

    private string _clipKey = "";
    private CancellationTokenSource? _clipCts;

    public bool HasPlacement => HasBackground && BackgroundDeviceFile is null && MediaX is not null && MediaY is not null && MediaWidth is > 0 && SourceWidth > 0 && SourceHeight > 0;
    public double MediaHeight => SourceWidth > 0 ? (MediaWidth ?? 0) * SourceHeight / SourceWidth : 0;
    public System.Windows.Rect AreaRect { get { var a = Placement.AreaBox(Area, Orientation); return new System.Windows.Rect(a.X, a.Y, a.W, a.H); } }
    public IReadOnlyList<ScreenArea> AreaChoices { get; } = Enum.GetValues<ScreenArea>();

    /// <summary>Zoom relative to "fill the area" (100 % = exactly covers it).</summary>
    public double ZoomPercent
    {
        get
        {
            if (!HasPlacement)
                return 100;
            var fill = Placement.Fill(Placement.AreaBox(Area, Orientation), SourceWidth, SourceHeight);
            return Math.Round(MediaWidth!.Value / fill.W * 100, 1);
        }
        set
        {
            if (!HasPlacement || value <= 0)
                return;
            var fill = Placement.Fill(Placement.AreaBox(Area, Orientation), SourceWidth, SourceHeight);
            ResizeMedia(fill.W * Math.Clamp(value, 5, 800) / 100);
        }
    }

    /// <summary>Whole-pixel views of the placement for the number boxes.</summary>
    public int MediaXPx { get => (int)Math.Round(MediaX ?? 0); set { if (HasPlacement) MediaX = value; } }
    public int MediaYPx { get => (int)Math.Round(MediaY ?? 0); set { if (HasPlacement) MediaY = value; } }
    public int MediaWidthPx { get => (int)Math.Round(MediaWidth ?? 0); set { if (HasPlacement && value > 0) ResizeMedia(value, keepCenter: false); } }
    public int MediaHeightPx
    {
        get => (int)Math.Round(MediaHeight);
        set { if (HasPlacement && value > 0) ResizeMedia(value * (double)SourceWidth / SourceHeight, keepCenter: false); }
    }

    public bool IsAreaWhole { get => Area == ScreenArea.Whole; set { if (value) SetArea(ScreenArea.Whole); } }
    public bool IsAreaTop { get => Area == ScreenArea.TopFace; set { if (value) SetArea(ScreenArea.TopFace); } }
    public bool IsAreaFront { get => Area == ScreenArea.FrontFace; set { if (value) SetArea(ScreenArea.FrontFace); } }
    public bool IsBackgroundMode => EditorMode == "Background";

    /// <summary>True when the media's centre sits exactly on the area's centre (for the "centred" badges).</summary>
    public bool IsCenteredH => HasPlacement && Math.Abs(MediaX!.Value + MediaWidth!.Value / 2 - (AreaRect.X + AreaRect.Width / 2)) < 0.5;
    public bool IsCenteredV => HasPlacement && Math.Abs(MediaY!.Value + MediaHeight / 2 - (AreaRect.Y + AreaRect.Height / 2)) < 0.5;

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

    /// <summary>A media is shown (a file of yours or one played from the cooler): it can be removed.</summary>
    public bool HasAnyBackground => HasBackground || BackgroundDeviceFile is not null;
    public bool IsVideoBackground => HasBackground && !MediaPreparer.IsImage(BackgroundPath!);
    public string BackgroundName => BackgroundDeviceFile is { } f
        ? $"From the cooler: {f}"
        : HasBackground ? Path.GetFileName(BackgroundPath!) : "No background (black screen)";
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

        _sensors.Updated += () => _ui.BeginInvoke(OnSensorsUpdated);
        _engine.StateChanged += (state, detail) => _ui.BeginInvoke(() => OnEngineState(state, detail));
        _engine.Log += line => _ui.BeginInvoke(() => AddLog(line));

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshPreviews(), _ui);
        _commitDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { _commitDebounce!.Stop(); Commit(); }, _ui) { IsEnabled = false };
        _liveDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (_, _) => { _liveDebounce!.Stop(); await PushLiveAsync(); }, _ui) { IsEnabled = false };

        StartWithWindows = Autostart.IsEnabled();
        // Keep "Start with Windows" pointing at the installed (newest) app, not an older build folder.
        _ = Task.Run(() =>
        {
            try
            {
                if (Autostart.EnsureCurrent())
                    _ui.BeginInvoke(() => AddLog($"Start with Windows now opens {Autostart.TargetExe}"));
            }
            catch (Exception ex)
            {
                _ui.BeginInvoke(() => AddLog("Could not update Start with Windows: " + ex.Message));
            }
        });
        Lighting = new LightingViewModel(storage.RoamingDir, _ui, line => _ui.BeginInvoke(() => AddLog(line)));
        Lighting.Start();
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
        Background = BuildBackground(),
        SecondBackground = _other is null ? null : Copy(_other),
        Widgets = Widgets.Select(w => w.ToModel()).ToList(),
        Drawing = Drawing,
    };

    /// <summary>The media being edited (the Background panel and BACKGROUND mode work on this one).</summary>
    private BackgroundMedia BuildBackground() => new()
    {
        SourcePath = BackgroundPath,
        Fit = BackgroundFit,
        CropX = CropX,
        CropY = CropY,
        StartSeconds = StartSeconds,
        DurationSeconds = DurationSeconds,
        DeviceFile = BackgroundDeviceFile,
        SourceWidth = SourceWidth,
        SourceHeight = SourceHeight,
        MediaX = MediaX,
        MediaY = MediaY,
        MediaWidth = MediaWidth,
        Area = Area,
    };

    private static BackgroundMedia Copy(BackgroundMedia m) => new()
    {
        SourcePath = m.SourcePath,
        Fit = m.Fit,
        CropX = m.CropX,
        CropY = m.CropY,
        StartSeconds = m.StartSeconds,
        DurationSeconds = m.DurationSeconds,
        DeviceFile = m.DeviceFile,
        SourceWidth = m.SourceWidth,
        SourceHeight = m.SourceHeight,
        MediaX = m.MediaX,
        MediaY = m.MediaY,
        MediaWidth = m.MediaWidth,
        Area = m.Area,
    };

    /// <summary>Makes a media the one being edited. Call with <see cref="_loading"/> set.</summary>
    private void ApplyMedia(BackgroundMedia m)
    {
        BackgroundPath = m.SourcePath;
        BackgroundFit = m.Fit;
        CropX = m.CropX;
        CropY = m.CropY;
        StartSeconds = m.StartSeconds;
        DurationSeconds = m.DurationSeconds;
        BackgroundDeviceFile = m.DeviceFile;
        SourceWidth = m.SourceWidth;
        SourceHeight = m.SourceHeight;
        MediaX = m.MediaX;
        MediaY = m.MediaY;
        MediaWidth = m.MediaWidth;
        Area = m.Area;
    }

    private void Load(ScreenLayout layout, bool keepSelection)
    {
        var selectedIds = keepSelection ? Selection.Select(w => w.Id).ToHashSet() : [];
        _loading = true;
        try
        {
            Orientation = layout.Orientation;
            Brightness = layout.Brightness;
            Fahrenheit = layout.Fahrenheit;
            ApplyMedia(layout.Background);
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
        SetOther(layout.SecondBackground, edited: false);
        NotifyPlacement();
        SchedulePreconvert(); // undo, presets and start-up change the design too
        var key = BackgroundKey();
        if (key != _backgroundKey)
        {
            _backgroundKey = key;
            _ = RenderBackgroundPreviewAsync();
        }
        _ = EnsurePlacementAsync();
    }

    // ================================================================== placement

    private void NotifyPlacement()
    {
        foreach (var name in new[]
        {
            nameof(HasPlacement), nameof(MediaHeight), nameof(AreaRect), nameof(ZoomPercent), nameof(MediaXPx), nameof(MediaYPx),
            nameof(MediaWidthPx), nameof(MediaHeightPx), nameof(IsCenteredH), nameof(IsCenteredV),
            nameof(IsAreaWhole), nameof(IsAreaTop), nameof(IsAreaFront),
        })
            OnPropertyChanged(name);
        NotifyOther();
    }

    partial void OnMediaXChanged(double? value) => PlacementEdited();
    partial void OnMediaYChanged(double? value) => PlacementEdited();
    partial void OnMediaWidthChanged(double? value) => PlacementEdited();
    partial void OnSourceWidthChanged(int value) => NotifyPlacement();
    partial void OnSourceHeightChanged(int value) => NotifyPlacement();

    partial void OnAreaChanged(ScreenArea value)
    {
        NotifyPlacement();
        if (_loading)
            return;
        if (_other is { } other)
        {
            if (value == ScreenArea.Whole)
            {
                // The whole screen covers the other face's media.
                SetOther(null, edited: true);
            }
            else if (other.Area == value)
            {
                // Moving onto the other media's face swaps the two faces.
                other.Area = Placement.Opposite(value);
                Refill(other);
                NotifyOther();
            }
        }
        // Choosing an area fills it, so "show it only on the top face" is one click.
        FillArea();
    }

    // ================================================================== second media (the other face)

    /// <summary>
    /// Portrait: the media on the other face, kept while the one in the Background panel is edited
    /// and sent together with it (see <see cref="ScreenLayout.SecondBackground"/>).
    /// </summary>
    private BackgroundMedia? _other;

    [ObservableProperty] public partial ImageSource? OtherFrame { get; set; }
    [ObservableProperty] public partial Uri? OtherClip { get; set; }
    private string _otherFrameKey = "";
    private string _otherClipKey = "";
    private CancellationTokenSource? _otherCts;

    /// <summary>The other media when it is shown: portrait, on the face opposite the edited one.</summary>
    private BackgroundMedia? ShownOther => _other is null ? null : Placement.Pairable(BuildBackground(), _other, Orientation);

    public bool HasOther => ShownOther is not null;
    public System.Windows.Rect OtherRect => ShownOther is { } o
        ? new System.Windows.Rect(o.MediaX!.Value, o.MediaY!.Value, o.MediaWidth!.Value, o.MediaHeight)
        : new System.Windows.Rect(0, 0, 0, 0);
    public System.Windows.Rect OtherAreaRect => ShownOther is { } o
        ? ToRect(Placement.AreaBox(o.Area, Orientation))
        : new System.Windows.Rect(0, 0, 0, 0);
    public string OtherName => _other?.SourcePath is { } p ? Path.GetFileName(p) : "";
    public string OtherFaceName => FaceName(_other?.Area ?? ScreenArea.Whole);
    public string ActiveFaceName => FaceName(Area);

    public static string FaceName(ScreenArea area) => area switch
    {
        ScreenArea.TopFace => "top face",
        ScreenArea.FrontFace => "front face",
        _ => "whole screen",
    };

    private static System.Windows.Rect ToRect(Box b) => new(b.X, b.Y, b.W, b.H);

    private void NotifyOther()
    {
        foreach (var name in new[] { nameof(HasOther), nameof(OtherRect), nameof(OtherAreaRect), nameof(OtherName), nameof(OtherFaceName), nameof(ActiveFaceName) })
            OnPropertyChanged(name);
    }

    private void SetOther(BackgroundMedia? media, bool edited)
    {
        if (media is null && _other is null)
            return;
        _other = media;
        if (media is null)
        {
            // (A different media cancels the running preview itself, in EnsureOtherPreviewAsync; the
            // same media reloaded, e.g. by undo, must not lose a preview that is still being made.)
            _otherCts?.Cancel();
            OtherFrame = null;
            OtherClip = null;
            _otherFrameKey = "";
            _otherClipKey = "";
        }
        NotifyOther();
        if (media is not null)
            _ = EnsureOtherPreviewAsync();
        if (edited)
        {
            BackgroundDirty = true;
            OnLayoutChanged(needsConfig: false);
        }
    }

    /// <summary>Fills a media's own area again (after it moved to another face).</summary>
    private void Refill(BackgroundMedia m) => Placement.FillOwnArea(m, Orientation);

    /// <summary>Makes room for a new media on <paramref name="target"/> (rules: <see cref="Placement.MakeRoom"/>).</summary>
    private void PrepareSlot(ScreenArea target)
    {
        var current = HasBackground && BackgroundDeviceFile is null ? BuildBackground() : null;
        var stays = Placement.MakeRoom(current, _other, target, Orientation);
        if (stays is null)
        {
            SetOther(null, edited: false);
            return;
        }
        if (ReferenceEquals(stays, _other))
            return; // the other face keeps its media
        // The current media now lives on the other face (whatever was on the target face goes).
        _otherCts?.Cancel();
        _other = stays;
        OtherFrame = SourceFrame;
        _otherFrameKey = SourceFrame is null ? "" : _sourceFrameKey;
        OtherClip = SourceClip;
        _otherClipKey = SourceClip is null ? "" : _clipKey;
        NotifyOther();
        _ = EnsureOtherPreviewAsync();
    }

    /// <summary>Switches editing to the other face's media (the current one stays on its face).</summary>
    [RelayCommand]
    private void EditOtherMedia()
    {
        if (_other is not { } other)
            return;
        var current = BuildBackground();
        _clipCts?.Cancel();
        _otherCts?.Cancel();
        _other = current;
        _loading = true;
        try
        {
            ApplyMedia(other);
        }
        finally
        {
            _loading = false;
        }
        (SourceFrame, OtherFrame) = (OtherFrame, SourceFrame);
        (_sourceFrameKey, _otherFrameKey) = (_otherFrameKey, _sourceFrameKey);
        (SourceClip, OtherClip) = (OtherClip, SourceClip);
        (_clipKey, _otherClipKey) = (_otherClipKey, _clipKey);
        // Anything still being prepared is started again for its new place (finished copies are cached).
        if (SourceFrame is null) _sourceFrameKey = "";
        if (OtherFrame is null) _otherFrameKey = "";
        if (SourceClip is null) _clipKey = "";
        if (OtherClip is null) _otherClipKey = "";
        ClipStatus = "";
        OnPropertyChanged(nameof(HasBackground));
        OnPropertyChanged(nameof(IsVideoBackground));
        OnPropertyChanged(nameof(BackgroundName));
        NotifyPlacement();
        _backgroundKey = BackgroundKey();
        _ = RenderBackgroundPreviewAsync();
        _ = EnsurePlacementAsync();
        _ = EnsureOtherPreviewAsync();
        Status = $"Editing the {ActiveFaceName} media.";
    }

    [RelayCommand]
    private void RemoveOtherMedia()
    {
        var face = OtherFaceName;
        SetOther(null, edited: true);
        Status = $"Removed the {face} media. Ctrl+Z brings it back.";
    }

    /// <summary>Still and looping clip of the other face's media for the canvas.</summary>
    private async Task EnsureOtherPreviewAsync()
    {
        var other = _other;
        if (other?.SourcePath is not { } path || !File.Exists(path))
        {
            OtherFrame = null;
            OtherClip = null;
            return;
        }
        var frameKey = $"{path}|{other.StartSeconds}";
        if (frameKey != _otherFrameKey)
        {
            _otherFrameKey = frameKey;
            var png = Path.Combine(_storage.PreviewDir, $"source-{Guid.NewGuid():N}.png");
            try
            {
                var start = other.StartSeconds + 0.5;
                await Task.Run(() => MediaPreparer.RenderSourceFrameAsync(path, start, png, CancellationToken.None));
                if (frameKey == _otherFrameKey)
                    OtherFrame = LoadImage(png);
            }
            catch (Exception ex)
            {
                AddLog("Could not read the other face's media: " + ex.Message);
            }
            finally
            {
                try { File.Delete(png); } catch { }
            }
        }
        if (MediaPreparer.IsImage(path))
        {
            OtherClip = null;
            _otherClipKey = "";
            return;
        }
        var key = ClipKey(path, other.StartSeconds, other.DurationSeconds);
        if (key == _otherClipKey)
            return;
        _otherCts?.Cancel();
        _otherClipKey = key;
        var cts = _otherCts = new CancellationTokenSource();
        try
        {
            var file = await MakePreviewClipAsync(path, other.StartSeconds, other.DurationSeconds, key, null, cts.Token);
            if (key == _otherClipKey && !cts.IsCancellationRequested)
            {
                OtherClip = new Uri(file);
                PruneClips();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (key == _otherClipKey)
                _otherClipKey = "";
            AddLog("Video preview unavailable for the other face (its still is shown): " + ex.Message);
        }
    }

    private void PlacementEdited()
    {
        NotifyPlacement();
        if (_loading)
            return;
        BackgroundDirty = true;
        OnLayoutChanged(needsConfig: false);
    }

    private void SetPlacement(Box r)
    {
        _loading = true;
        try
        {
            MediaX = r.X;
            MediaY = r.Y;
            MediaWidth = r.W;
        }
        finally
        {
            _loading = false;
        }
        PlacementEdited();
    }

    /// <summary>Resizes keeping the shape; by default around the current centre.</summary>
    private void ResizeMedia(double width, bool keepCenter = true)
    {
        if (!HasPlacement)
            return;
        width = Math.Max(16, width);
        var height = width * SourceHeight / SourceWidth;
        var cx = MediaX!.Value + MediaWidth!.Value / 2;
        var cy = MediaY!.Value + MediaHeight / 2;
        SetPlacement(keepCenter
            ? new Box(cx - width / 2, cy - height / 2, width, height)
            : new Box(MediaX.Value, MediaY.Value, width, height));
    }

    /// <summary>Zooms around a point on the canvas (mouse wheel in background mode).</summary>
    public void ZoomMediaAt(double factor, double px, double py)
    {
        if (!HasPlacement)
            return;
        var w = Math.Max(16, MediaWidth!.Value * factor);
        var f = w / MediaWidth.Value;
        SetPlacement(new Box(px - (px - MediaX!.Value) * f, py - (py - MediaY!.Value) * f, w, w * SourceHeight / SourceWidth));
    }

    public void MoveMediaTo(double x, double y)
    {
        if (!HasPlacement)
            return;
        SetPlacement(new Box(x, y, MediaWidth!.Value, MediaHeight));
    }

    public Box MediaBox => new(MediaX ?? 0, MediaY ?? 0, MediaWidth ?? 0, MediaHeight);
    public Box AreaBox => Placement.AreaBox(Area, Orientation);

    [RelayCommand]
    private void FillArea()
    {
        if (SourceWidth > 0 && SourceHeight > 0)
            SetPlacement(Placement.Fill(AreaBox, SourceWidth, SourceHeight));
    }

    [RelayCommand]
    private void FitArea()
    {
        if (SourceWidth > 0 && SourceHeight > 0)
            SetPlacement(Placement.Fit(AreaBox, SourceWidth, SourceHeight));
    }

    [RelayCommand]
    private void CenterMedia(string axis)
    {
        if (!HasPlacement)
            return;
        var a = AreaBox;
        SetPlacement(new Box(
            axis is "H" or "Both" ? a.X + (a.W - MediaWidth!.Value) / 2 : MediaX!.Value,
            axis is "V" or "Both" ? a.Y + (a.H - MediaHeight) / 2 : MediaY!.Value,
            MediaWidth!.Value, MediaHeight));
    }

    [RelayCommand]
    private void SetArea(ScreenArea area)
    {
        if (Area == area)
            FillArea();
        else
            Area = area;
    }

    [RelayCommand]
    private void NudgeMedia(string direction)
    {
        if (!HasPlacement)
            return;
        var (dx, dy) = direction switch { "L" => (-1, 0), "R" => (1, 0), "U" => (0, -1), _ => (0, 1) };
        MoveMediaTo(MediaX!.Value + dx, MediaY!.Value + dy);
    }

    /// <summary>
    /// Makes sure the current background has its size measured, a placement (older layouts are
    /// converted from Fit/Crop exactly), and a still for the live preview.
    /// </summary>
    private async Task EnsurePlacementAsync()
    {
        var path = BackgroundPath;
        if (path is null || BackgroundDeviceFile is not null || !File.Exists(path))
        {
            SourceFrame = null;
            _sourceFrameKey = "";
            StopPreviewClip();
            return;
        }
        try
        {
            if (SourceWidth <= 0 || SourceHeight <= 0)
            {
                var (w, h) = await Task.Run(() => MediaPreparer.ProbeSizeAsync(path, CancellationToken.None));
                if (path != BackgroundPath)
                    return;
                _loading = true;
                try
                {
                    SourceWidth = w;
                    SourceHeight = h;
                }
                finally
                {
                    _loading = false;
                }
            }
            if (MediaX is null || MediaY is null || MediaWidth is not > 0)
            {
                // Convert the old Fill/Fit + crop sliders into the same placement.
                var area = AreaBox;
                var r = BackgroundFit == BackgroundFit.Fit
                    ? Placement.Fit(area, SourceWidth, SourceHeight)
                    : Placement.Fill(area, SourceWidth, SourceHeight);
                if (BackgroundFit == BackgroundFit.Fill)
                    r = r with { X = area.X - (r.W - area.W) * CropX, Y = area.Y - (r.H - area.H) * CropY };
                _loading = true;
                try
                {
                    MediaX = r.X;
                    MediaY = r.Y;
                    MediaWidth = r.W;
                }
                finally
                {
                    _loading = false;
                }
                NotifyPlacement();
                Commit();
            }

            var frameKey = $"{path}|{StartSeconds}";
            if (frameKey != _sourceFrameKey)
            {
                _sourceFrameKey = frameKey;
                var png = Path.Combine(_storage.PreviewDir, $"source-{Guid.NewGuid():N}.png");
                var start = StartSeconds + 0.5;
                await Task.Run(() => MediaPreparer.RenderSourceFrameAsync(path, start, png, CancellationToken.None));
                if (frameKey == _sourceFrameKey)
                    SourceFrame = LoadImage(png);
                try { File.Delete(png); } catch { }
            }
        }
        catch (Exception ex)
        {
            Status = "Could not read the background: " + ex.Message;
        }
        if (path == BackgroundPath)
            await EnsurePreviewClipAsync(path);
    }

    private void StopPreviewClip()
    {
        _clipCts?.Cancel();
        _clipKey = "";
        SourceClip = null;
        ClipStatus = "";
    }

    /// <summary>
    /// Makes the editor play the background as the cooler will: the same start and length, looped.
    /// The still frame stays visible while the copy is made; copies are cached by source, start and
    /// length, and only the ones on screen are kept.
    /// </summary>
    private async Task EnsurePreviewClipAsync(string path)
    {
        if (MediaPreparer.IsImage(path))
        {
            StopPreviewClip();
            return;
        }
        var start = StartSeconds;
        var duration = DurationSeconds;
        var key = ClipKey(path, start, duration);
        if (key == _clipKey)
            return;
        _clipCts?.Cancel();
        _clipKey = key;
        var cts = _clipCts = new CancellationTokenSource();
        try
        {
            if (!File.Exists(ClipFile(key)))
                SourceClip = null;
            var file = await MakePreviewClipAsync(path, start, duration, key, text =>
            {
                if (!cts.IsCancellationRequested)
                    ClipStatus = text;
            }, cts.Token);
            if (cts.IsCancellationRequested || key != _clipKey)
                return;
            ClipStatus = "";
            SourceClip = new Uri(file);
            PruneClips();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                ClipStatus = "";
                _clipKey = "";
                AddLog("Video preview unavailable (the still frame is shown): " + ex.Message);
            }
        }
    }

    private static string ClipKey(string path, double start, double? duration) =>
        $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{start.ToString(CultureInfo.InvariantCulture)}|{duration?.ToString(CultureInfo.InvariantCulture)}";

    private string ClipFile(string key) => Path.Combine(_storage.PreviewDir,
        "clip-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)), 0, 8) + ".mp4");

    /// <summary>Returns the cached preview copy for this clip, making it first if needed.</summary>
    private async Task<string> MakePreviewClipAsync(string path, double start, double? duration, string key, Action<string>? status, CancellationToken token)
    {
        var file = ClipFile(key);
        if (File.Exists(file))
            return file;
        var part = file + ".part";
        try
        {
            // Start and length are often typed digit by digit; wait for the value to settle.
            await Task.Delay(600, token);
            status?.Invoke("Preparing the video preview…");
            var expected = duration ?? Math.Max(0, await Task.Run(() => MediaPreparer.ProbeDurationAsync(path, token)) - start);
            await Task.Run(() => MediaPreparer.RenderPreviewClipAsync(path, start, duration, part, token, seconds =>
                _ui.BeginInvoke(() =>
                {
                    if (expected > 0)
                        status?.Invoke($"Preparing the video preview… {Math.Clamp(seconds / expected, 0, 1):P0}");
                })));
            File.Move(part, file, overwrite: true);
            return file;
        }
        finally
        {
            try { File.Delete(part); } catch { }
        }
    }

    /// <summary>Keeps only the copies on screen (a replaced one may still be open in the player; it goes next time).</summary>
    private void PruneClips()
    {
        var keep = new[] { SourceClip?.LocalPath, OtherClip?.LocalPath };
        foreach (var old in Directory.EnumerateFiles(_storage.PreviewDir, "clip-*.mp4"))
        {
            if (!keep.Any(k => string.Equals(k, old, StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [RelayCommand]
    private void TogglePreviewPlaying() => PreviewPlaying = !PreviewPlaying;

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
        SchedulePreconvert();
        if (HasApplied && IsConnected)
        {
            _liveNeedsConfig |= needsConfig;
            _liveDebounce.Stop();
            _liveDebounce.Start();
        }
    }

    // ================================================================== background conversion

    private DispatcherTimer? _preconvertDebounce;
    private string? _preconvertKey;

    /// <summary>Converts the design for the cooler 3 s after the last change, so Apply only has to upload.</summary>
    private void SchedulePreconvert()
    {
        _preconvertDebounce ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _preconvertDebounce!.Stop();
            _ = PreconvertAsync();
        }, _ui) { IsEnabled = false };
        _preconvertDebounce.Stop();
        _preconvertDebounce.Start();
    }

    /// <summary>
    /// Starts converting the current design at low priority. A design already converted (or being
    /// converted) is left alone, so edits that do not touch the video (text, sensors) cost nothing;
    /// a changed design stops the previous background run. Apply joins a run in progress.
    /// </summary>
    private async Task PreconvertAsync()
    {
        if (IsBusy)
            return; // an Apply is converting already
        var layout = BuildLayout();
        if (layout.Background.DeviceFile is not null)
            return;
        string key;
        string? overlay;
        try
        {
            overlay = RenderDrawingPng(layout);
            key = MediaPreparer.KeyOf(layout.Background, layout.Orientation, overlay, layout.SecondBackground);
        }
        catch (Exception)
        {
            return; // a file of the design is missing; Apply will say so
        }
        if (key == _preconvertKey)
            return;
        _preparer.CancelBackground();
        _preconvertKey = key;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _preparer.PrepareAsync(layout.Background, layout.Orientation, overlay, null, CancellationToken.None, layout.SecondBackground, background: true);
            if (clock.Elapsed > TimeSpan.FromSeconds(1))
                AddLog($"Converted for the cooler in the background ({clock.Elapsed.TotalSeconds:0} s): Apply will only upload it.");
        }
        catch (OperationCanceledException)
        {
            if (_preconvertKey == key)
                _preconvertKey = null;
        }
        catch (Exception ex)
        {
            if (_preconvertKey == key)
                _preconvertKey = null;
            AddLog("Background conversion failed (Apply will try again): " + ex.Message);
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
        // The canvas changed shape: faces only exist in portrait; refill the area in the new canvas.
        _loading = true;
        try
        {
            if (value != LayoutOrientation.Portrait)
            {
                Area = ScreenArea.Whole;
                // Landscape has no faces: one media fills the screen (Ctrl+Z brings the other back).
                SetOther(null, edited: false);
            }
        }
        finally
        {
            _loading = false;
        }
        NotifyPlacement();
        FillArea();
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
        OnPropertyChanged(nameof(HasAnyBackground));
        OnPropertyChanged(nameof(IsVideoBackground));
        OnPropertyChanged(nameof(BackgroundName));
        BackgroundEdited();
    }

    partial void OnBackgroundDeviceFileChanged(string? value)
    {
        OnPropertyChanged(nameof(BackgroundName));
        OnPropertyChanged(nameof(HasAnyBackground));
        BackgroundEdited();
    }

    partial void OnDrawingChanged(string? value) => OnPropertyChanged(nameof(HasDrawing));

    partial void OnEditorModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsDrawMode));
        OnPropertyChanged(nameof(IsBackgroundMode));
        if (value is "Draw" or "Background")
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
        NotifyPlacement();
        _ = EnsurePlacementAsync();
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
        else if (!wasConnected && state == EngineState.Connected)
            _ = PruneMediaCacheAsync(); // (an Apply cleans up after itself)
    }

    /// <summary>
    /// Deletes cached conversions that are no longer needed: what is stored on the cooler and the
    /// current design are kept (so Apply and the "On the cooler" previews stay instant); older designs
    /// go. Needs the cooler's file list, so it only runs while connected.
    /// </summary>
    private async Task PruneMediaCacheAsync()
    {
        if (!IsConnected)
            return;
        try
        {
            var onCooler = await _engine.ListDeviceMediaAsync();
            var keepKeys = new HashSet<string>(onCooler.Select(m => DeviceKey(m.Name)).OfType<string>());
            var keepFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layout = BuildLayout();
            if (layout.Background.DeviceFile is null)
            {
                var overlay = RenderDrawingPng(layout);
                if (overlay is not null)
                    keepFiles.Add(overlay);
                try
                {
                    keepKeys.Add(MediaPreparer.KeyOf(layout.Background, layout.Orientation, overlay, layout.SecondBackground));
                }
                catch (Exception)
                {
                    // A file of the design is missing: nothing of it to keep.
                }
            }
            var freed = await Task.Run(() => _preparer.PruneCache(keepKeys, keepFiles));
            if (freed > 0)
                AddLog($"Freed {freed / 1048576.0:0} MB of old conversions (kept what is on the cooler and your current design).");
        }
        catch (Exception ex)
        {
            AddLog("Cache cleanup skipped: " + ex.Message);
        }
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

    private void RefreshPreviews()
    {
        var fahrenheit = Fahrenheit;
        foreach (var w in Widgets)
            w.PreviewText = _text.Resolve(w.ToModel(), fahrenheit);
        UpdateSensorValues();
        CpuTempText = _sensors.Format(SensorHub.RolePrefix + "cpu-temp", 0, true, fahrenheit);
        GpuTempText = _sensors.Format(SensorHub.RolePrefix + "gpu-temp", 0, true, fahrenheit);
    }

    private int _knownSensorCount = -1;

    /// <summary>
    /// Sensors start in the background after the window appears; rebuild the picker the moment
    /// they arrive (and whenever hardware is added later) instead of waiting for a timer.
    /// </summary>
    private void OnSensorsUpdated()
    {
        var count = _sensors.Catalog.Count;
        if (count == _knownSensorCount)
            return;
        var first = _knownSensorCount <= 0;
        _knownSensorCount = count;
        RebuildSensorRows();
        RefreshPreviews();
        if (first)
            OnPropertyChanged(nameof(SelectedSensorRow));
    }

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
    /// <summary>The cache key of one of this app's uploads ("upn360-&lt;key&gt;.mp4.h264_…"), or null.</summary>
    private static string? DeviceKey(string deviceName) =>
        deviceName.StartsWith(DisplayEngine.RemotePrefix, StringComparison.Ordinal)
            ? deviceName[DisplayEngine.RemotePrefix.Length..].Split('.')[0]
            : null;

    private string? DeviceThumbnailPath(string deviceName)
    {
        if (DeviceKey(deviceName) is not { } key)
            return null;
        var png = Path.Combine(_storage.MediaCacheDir, key + ".png");
        return File.Exists(png) ? png : null;
    }

    /// <summary>The exact stream this app sent for an upload, if it is still in the local cache.</summary>
    private string? DeviceStreamPath(string deviceName)
    {
        if (DeviceKey(deviceName) is not { } key)
            return null;
        var h264 = Path.Combine(_storage.MediaCacheDir, key + ".h264");
        return File.Exists(h264) ? h264 : null;
    }

    private CancellationTokenSource? _deviceClipCts;

    /// <summary>
    /// Moving previews for "On the cooler": this app's uploads, made from the stream that was sent
    /// (turned upright again for portrait designs). Copies of files no longer on the cooler are removed.
    /// </summary>
    private async Task LoadDeviceClipsAsync(List<DeviceMediaViewModel> items, CancellationToken token)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (token.IsCancellationRequested)
                return;
            if (DeviceStreamPath(item.Name) is not { } h264 || DeviceKey(item.Name) is not { } key)
                continue;
            var mp4 = Path.Combine(_storage.PreviewDir, $"device-clip-{key}.mp4");
            keep.Add(mp4);
            try
            {
                var seconds = await Task.Run(() => MediaPreparer.ProbeStreamSecondsAsync(h264, token), token);
                if (seconds > 0)
                    item.DurationText = TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
                if (!File.Exists(mp4))
                {
                    // The upright still tells the design's orientation (portrait streams are stored turned).
                    var portrait = DeviceThumbnailPath(item.Name) is { } png && IsTall(png);
                    var part = mp4 + ".part";
                    try
                    {
                        await Task.Run(() => MediaPreparer.RenderStreamPreviewClipAsync(h264, portrait, MediaLibrary.CardClipSeconds, part, token), token);
                        File.Move(part, mp4, overwrite: true);
                    }
                    finally
                    {
                        try { File.Delete(part); } catch { }
                    }
                }
                item.Clip = new Uri(mp4);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // No moving preview for this one; its still stays.
            }
        }
        foreach (var old in Directory.EnumerateFiles(_storage.PreviewDir, "device-clip-*"))
        {
            if (!keep.Contains(old))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsTall(string imagePath)
    {
        try
        {
            using var stream = File.OpenRead(imagePath);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return frame.PixelHeight > frame.PixelWidth;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Adds a line to the Activity log (used by app-level services).</summary>
    public void Report(string line) => AddLog(line);

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
                layout.SecondBackground = current.SecondBackground;
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

    /// <summary>
    /// Asks where a new media goes (whole screen, top face or front face); set by the main window.
    /// Returns null when the user cancels.
    /// </summary>
    public Func<string, ScreenArea?>? AskArea { get; set; }

    /// <summary>Where a new media goes: asked in portrait; landscape has only the whole screen.</summary>
    public ScreenArea? ChooseArea(string title) =>
        Orientation != LayoutOrientation.Portrait ? ScreenArea.Whole
        : AskArea is { } ask ? ask(title)
        : ScreenArea.Whole;

    [RelayCommand]
    private void ChooseBackground()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a background",
            Filter = "Images, GIFs and videos|" + string.Join(";", MediaExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        if (dialog.ShowDialog() == true && ChooseArea(Path.GetFileName(dialog.FileName)) is { } area)
            SetBackground(dialog.FileName, area);
    }

    public static bool IsMediaFile(string path) => MediaExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void SetBackground(string path, ScreenArea area)
    {
        PrepareSlot(area);
        _loading = true;
        try
        {
            BackgroundDeviceFile = null;
            ResetPlacement();
            Area = Orientation == LayoutOrientation.Portrait ? area : ScreenArea.Whole;
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
        Status = $"{Path.GetFileName(path)} is on the {ActiveFaceName}. Apply to send it to the cooler.";
    }

    public YouTubeImporter YouTube => _youTube ??= new YouTubeImporter(_storage.DownloadDir);

    /// <summary>Uses a downloaded YouTube section; the start is cut frame-exactly at conversion.</summary>
    public void UseImportedClip(ImportedClip clip, string? title, ScreenArea area)
    {
        PrepareSlot(area);
        _loading = true;
        try
        {
            BackgroundDeviceFile = null;
            ResetPlacement();
            Area = Orientation == LayoutOrientation.Portrait ? area : ScreenArea.Whole;
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
        Status = $"Imported “{title}” on the {ActiveFaceName}. Adjust it under Background or in BACKGROUND mode, then Apply.";
    }

    /// <summary>A downloaded clip that was not placed (the area question was cancelled) stays in My media.</summary>
    public void KeepImportedClip(ImportedClip clip, string? title)
    {
        _library.Remember(LibraryKind.YouTube, clip.FilePath, title, Math.Round(clip.OffsetSeconds, 3), Math.Round(clip.LengthSeconds, 3));
        ReloadLibrary();
        Status = $"“{title}” is saved in Library, My media. Use it from there whenever you like.";
    }

    /// <summary>A new source is measured again and starts as "fill the current area", centred.</summary>
    private void ResetPlacement()
    {
        SourceWidth = 0;
        SourceHeight = 0;
        MediaX = null;
        MediaY = null;
        MediaWidth = null;
        BackgroundFit = BackgroundFit.Fill;
        SourceFrame = null;
        _sourceFrameKey = "";
        StopPreviewClip();
    }

    [RelayCommand]
    private void ClearBackground()
    {
        if (_other is not null)
        {
            // Clearing one face leaves the other face's media in place.
            var face = ActiveFaceName;
            EditOtherMedia();
            SetOther(null, edited: true);
            Status = $"Removed the {face} media. Ctrl+Z brings it back.";
            return;
        }
        if (!HasAnyBackground)
            return;
        var name = BackgroundDeviceFile ?? Path.GetFileName(BackgroundPath);
        BackgroundDeviceFile = null;
        BackgroundPath = null;
        Status = $"Removed {name}. Ctrl+Z brings it back.";
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

        // Then the moving previews of videos and GIFs (stills first, so the page fills quickly).
        foreach (var item in items.Where(i => i.IsVideo && i.Exists))
        {
            if (token.IsCancellationRequested)
                return;
            try
            {
                var entry = item.Entry;
                var seconds = await Task.Run(() => MediaLibrary.PlayedSecondsAsync(entry, token), token);
                if (seconds > 0)
                    item.DurationText = TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
                var mp4 = await Task.Run(() => _library.PreviewClipAsync(entry, token), token);
                if (mp4 is not null)
                    item.Clip = new Uri(mp4);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // No moving preview for this one; its still stays.
            }
        }
        try
        {
            _library.PruneCache();
        }
        catch (IOException)
        {
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
        if (ChooseArea(e.Title) is not { } area)
            return;
        PrepareSlot(area);
        _loading = true;
        try
        {
            BackgroundDeviceFile = null;
            ResetPlacement();
            Area = Orientation == LayoutOrientation.Portrait ? area : ScreenArea.Whole;
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
        Status = $"{e.Title} is on the {ActiveFaceName}. Apply to send it to the cooler.";
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
                var item = new DeviceMediaViewModel(m.Name, m.Size, m.IsPreset, m.ReadOnly, m.InUse)
                {
                    HasLocalCopy = DeviceStreamPath(m.Name) is not null,
                };
                if (DeviceThumbnailPath(m.Name) is { } png)
                    item.Thumbnail = LoadImage(png);
                DeviceItems.Add(item);
            }
            DeviceListNote = $"{media.Count} files on the cooler · {media.Count(m => m.IsPreset)} TRYX presets";
            _deviceClipCts?.Cancel();
            _deviceClipCts = new CancellationTokenSource();
            _ = LoadDeviceClipsAsync(DeviceItems.ToList(), _deviceClipCts.Token);
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
        // A file already on the cooler plays as it is, on the whole screen.
        SetOther(null, edited: false);
        Area = ScreenArea.Whole;
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
            Status = IsKanaliBlocking ? "Close KANALI first. It owns the cooler while it runs." : "The cooler is not connected.";
            return;
        }
        IsBusy = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressDetail = "";
        try
        {
            // Created on the UI thread, so reports from the engine's worker threads are marshalled here.
            var progress = new Progress<ProgressInfo>(p =>
            {
                Status = p.Text;
                ProgressIndeterminate = p.Fraction is null;
                ProgressValue = p.Fraction ?? 0;
                ProgressDetail = p.Detail ?? "";
            });
            var layout = BuildLayout();
            var overlay = includeBackground && layout.Background.DeviceFile is null ? RenderDrawingPng(layout) : null;
            await _engine.ApplyAsync(layout, includeBackground, overlay, progress);
            if (_engine.LastApplyTimings is { } timings)
            {
                AddLog("Apply " + timings);
                try
                {
                    var file = Path.Combine(_storage.LocalDir, "apply.log");
                    var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
                    lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {timings}");
                    File.WriteAllLines(file, lines.TakeLast(50));
                }
                catch (IOException)
                {
                }
            }
            HasApplied = true;
            BackgroundDirty = false;
            _storage.AutoApply = true;
            Commit();
            Status = quiet ? "Restored your design on the cooler." : "Applied to the cooler.";
            if (includeBackground)
                _ = PruneMediaCacheAsync();
        }
        catch (Exception ex)
        {
            Status = "Apply failed: " + ex.Message;
            AddLog(Status);
        }
        finally
        {
            IsBusy = false;
            ProgressDetail = "";
        }
    }

    public void Shutdown()
    {
        // Stop preview and library conversions (their ffmpeg processes are killed on cancel).
        foreach (var cts in new[] { _clipCts, _otherCts, _thumbCts, _deviceClipCts, _previewCts })
            cts?.Cancel();
        _preconvertDebounce?.Stop();
        _preparer.CancelAll();
        Lighting.Dispose();
        _tick.Stop();
        CommitNow();
        _storage.SaveLayout(BuildLayout());
    }
}
