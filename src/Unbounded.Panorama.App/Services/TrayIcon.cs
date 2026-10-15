using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Unbounded.Panorama.App.Services;

/// <summary>Notification-area icon: double-click opens, menu offers Open and Exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon(Action open, Action exit)
    {
        _image = DrawIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Unbounded Panorama 360", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Unbounded Panorama 360",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void ShowHint(string text) => _icon.ShowBalloonTip(3000, "Unbounded Panorama 360", text, Forms.ToolTipIcon.Info);

    /// <summary>A small portrait "screen" glyph with the accent gradient.</summary>
    public static Icon DrawIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            var r = new Rectangle(14, 4, 36, 56);
            const int radius = 9;
            path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            using var fill = new LinearGradientBrush(r, Color.FromArgb(0x4F, 0x8C, 0xFF), Color.FromArgb(0x22, 0xD3, 0xEE), 90f);
            g.FillPath(fill, path);
            using var line = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            g.FillRectangle(line, 22, 16, 20, 5);
            g.FillRectangle(line, 22, 27, 14, 4);
            g.FillRectangle(line, 22, 36, 18, 4);
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
