using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Unbounded.Panorama.App.ViewModels;

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
        DragAcceptFiles(hwnd, true);
        source?.AddHook(WndProc);
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
            _vm.UseImportedClip(clip, dialog.VideoTitle);
            _vm.CurrentPage = "Studio";
        }
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
                    _vm.SetBackground(file);
                    _vm.CurrentPage = "Studio";
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
        if (msg != WM_DROPFILES)
            return IntPtr.Zero;
        try
        {
            var count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(wParam, i, null, 0);
                var buffer = new StringBuilder((int)length + 1);
                DragQueryFile(wParam, i, buffer, (uint)buffer.Capacity);
                var path = buffer.ToString();
                if (MainViewModel.IsMediaFile(path))
                {
                    _vm.SetBackground(path);
                    _vm.CurrentPage = "Studio";
                    break;
                }
                _vm.Status = $"{System.IO.Path.GetFileName(path)} is not an image, GIF or video.";
            }
        }
        finally
        {
            DragFinish(wParam);
        }
        handled = true;
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
}
