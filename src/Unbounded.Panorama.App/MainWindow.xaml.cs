using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Unbounded.Panorama.App.ViewModels;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.App;

/// <summary>Shell: custom title bar on the Mica backdrop, navigation, file drop and paste.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            Services.TrayIcon.DrawIcon().Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        PreviewKeyDown += OnKey;
        StateChanged += (_, _) => FitMaximized();
        vm.AskArea = title => AreaPickerWindow.Ask(this, title);

        // Our own clicks are not file drags (see WatchFileDrag).
        PreviewMouseLeftButtonDown += (_, _) => _ownPress = true;
        PreviewMouseLeftButtonUp += (_, _) => _ownPress = false;
        _dragWatch = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(40), System.Windows.Threading.DispatcherPriority.Input, (_, _) => WatchFileDrag(), Dispatcher);
        IsVisibleChanged += (_, _) => _dragWatch.IsEnabled = IsVisible;
    }

    // ================================================================== chrome

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);

        // Windows 11 Mica behind a transparent WPF surface; the semi-opaque root keeps it legible elsewhere.
        if (source?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        var dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var mica = DWMSBT_MAINWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int));

        // The app runs elevated (CPU sensors), and Windows blocks OLE drag-and-drop from Explorer into
        // elevated windows. Classic WM_DROPFILES still works once it is allowed through the filter.
        foreach (var message in new uint[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
            ChangeWindowMessageFilterEx(hwnd, message, MSGFLT_ALLOW, IntPtr.Zero);
        source?.AddHook(WndProc);
        UseClassicFileDrop(hwnd);
        // WPF registers its OLE drop target as the window is set up; take it off again once that is done.
        Dispatcher.BeginInvoke(() => UseClassicFileDrop(hwnd), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Explorer only falls back to WM_DROPFILES when the window has no OLE drop target, and WPF
    /// registers one on every window (verified: "OleDropTargetInterface" was set). Nothing in the
    /// app uses WPF drag-and-drop, so the OLE target is revoked.
    /// </summary>
    private static void UseClassicFileDrop(IntPtr hwnd)
    {
        RevokeDragDrop(hwnd);
        DragAcceptFiles(hwnd, true);
    }
    /// <summary>A maximised chromeless window spills past the screen edge by the resize border; pull it back.</summary>
    private void FitMaximized()
    {
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnLibraryChecked(object sender, RoutedEventArgs e) => _vm.NavigateCommand.Execute("Library");

    // ================================================================== YouTube, file drop, paste

    public void OpenYouTube(string? url)
    {
        var dialog = new YouTubeWindow(_vm.YouTube, url) { Owner = this, Icon = Icon };
        Services.WindowChrome.UseDarkTitleBar(dialog);
        if (dialog.ShowDialog() == true && dialog.Result is { } clip)
        {
            if (_vm.ChooseArea(dialog.VideoTitle ?? "YouTube clip") is { } area)
            {
                _vm.UseImportedClip(clip, dialog.VideoTitle, area);
                _vm.CurrentPage = "Studio";
            }
            else
            {
                // Not placed now, but kept: it is in My media for later.
                _vm.KeepImportedClip(clip, dialog.VideoTitle);
            }
        }
    }

    /// <summary>A file from a drop (its zone decides, no question) or a paste (asked: there is nothing to aim).</summary>
    private void UseFile(string path, ScreenArea? zone)
    {
        if ((zone ?? _vm.ChooseArea(System.IO.Path.GetFileName(path))) is not { } area)
            return;
        _vm.SetBackground(path, area);
        _vm.CurrentPage = "Studio";
    }

    // ------------------------------------------------------------------ file drag feedback

    // Windows sends no drag-over events to this elevated window (the OLE target is revoked so that
    // Explorer falls back to WM_DROPFILES). A drag in progress is recognised instead by polling: the
    // left button is down, the cursor is over this window (WindowFromPoint skips Explorer's drag
    // image but not a window being moved), and the press did not start in this window.
    private readonly System.Windows.Threading.DispatcherTimer _dragWatch;
    private bool _ownPress;
    private bool _sizeMove;
    private bool _dragShown;

    /// <summary>The zone lit up while dragging; a drop goes exactly there (what you see is what you get).</summary>
    private ScreenArea? _litZone;

    private void WatchFileDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (!down)
            _ownPress = false;
        Point? at = null;
        if (down && !_ownPress && !_sizeMove && GetCursorPos(out var cursor)
            && GetAncestor(WindowFromPoint(cursor), GA_ROOT) == hwnd)
        {
            // Aiming needs the screen preview: a drag over Library or Settings brings Studio up.
            if (_vm.CurrentPage != "Studio")
                _vm.CurrentPage = "Studio";
            at = PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        if (at is null && !_dragShown)
            return;
        _dragShown = at is not null;
        if (at is { } point)
            _litZone = Studio.DropZoneAt(point) ?? _litZone;
        Studio.ShowDropTarget(at);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Enter)
        {
            _vm.ApplyCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V && Keyboard.FocusedElement is not TextBox && TryPaste())
        {
            e.Handled = true;
        }
    }

    /// <summary>A copied YouTube link opens the importer; a copied file becomes the background.</summary>
    private bool TryPaste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(MainViewModel.IsMediaFile);
                if (file is not null)
                {
                    UseFile(file, null);
                    return true;
                }
            }
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (text.Contains("youtu", StringComparison.OrdinalIgnoreCase))
                {
                    OpenYouTube(text);
                    return true;
                }
            }
        }
        catch (ExternalException)
        {
            // Clipboard busy in another app.
        }
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ENTERSIZEMOVE)
            _sizeMove = true;
        else if (msg == WM_EXITSIZEMOVE)
            _sizeMove = false;
        if (msg != WM_DROPFILES)
            return IntPtr.Zero;
        string? media = null;
        // The zone that was lit decides; failing that (no drag was seen), where it was dropped.
        var zone = _litZone;
        _litZone = null;
        try
        {
            if (zone is null && DragQueryPoint(wParam, out var drop) && ClientToScreen(hwnd, ref drop))
                zone = Studio.DropZoneAt(PointFromScreen(new Point(drop.X, drop.Y)));
            var count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(wParam, i, null, 0);
                var buffer = new StringBuilder((int)length + 1);
                DragQueryFile(wParam, i, buffer, (uint)buffer.Capacity);
                var path = buffer.ToString();
                if (MainViewModel.IsMediaFile(path))
                {
                    media = path;
                    break;
                }
                _vm.Status = $"{System.IO.Path.GetFileName(path)} is not an image, GIF or video.";
            }
        }
        finally
        {
            DragFinish(wParam);
        }
        _dragShown = false;
        Studio.ShowDropTarget(null);
        handled = true;
        if (media is not null)
        {
            // After this message returns, so Explorer is not kept waiting while the user answers.
            var file = media;
            Dispatcher.BeginInvoke(() => UseFile(file, zone));
        }
        return IntPtr.Zero;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, uint size);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);

    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int VK_LBUTTON = 0x01;
    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll")]
    private static extern bool DragQueryPoint(IntPtr hDrop, out POINT point);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);}
