using System.Globalization;
using Unbounded.Panorama.Wire;

namespace Unbounded.Panorama.Layout;

/// <summary>
/// Turns a ScreenLayout into the firmware's OverlayLayout and per-second MetricBatch updates.
/// Each widget becomes one group with one label. Group/label ids come from the id pairs
/// the firmware is known to accept.
/// </summary>
public static class LayoutCompiler
{
    /// <summary>(group, label) pairs: 100..110 and 200..210 with their value labels, plus the badge groups.</summary>
    public static readonly IReadOnlyList<(uint Group, uint Label)> IdPool = BuildPool();

    public static int MaxWidgets => IdPool.Count;

    /// <summary>Firmware ui_rotation for an orientation (verified: 270 is upright on a portrait mount).</summary>
    public static uint UiRotation(Orientation orientation) => orientation == Orientation.Portrait ? 270u : 0u;

    private static List<(uint, uint)> BuildPool()
    {
        var pool = new List<(uint, uint)>();
        for (uint i = 0; i <= 10; i++)
            pool.Add((100 + i, 102 + 3 * i));
        for (uint i = 0; i <= 10; i++)
            pool.Add((200 + i, 202 + 3 * i));
        pool.Add((300, 301));
        pool.Add((400, 401));
        return pool;
    }

    /// <summary>The widgets actually sent to the cooler, in id-pool order.</summary>
    public static List<Widget> Visible(ScreenLayout layout) => layout.Widgets.Where(w => !w.Hidden).ToList();

    public static OverlayLayout Compile(ScreenLayout layout, Func<Widget, string> textOf)
    {
        var widgets = Visible(layout);
        if (widgets.Count > MaxWidgets)
            throw new InvalidOperationException($"The cooler supports at most {MaxWidgets} visible widgets; this layout has {widgets.Count}.");

        var overlay = new OverlayLayout();
        for (var i = 0; i < widgets.Count; i++)
        {
            var w = widgets[i];
            var (groupId, labelId) = IdPool[i];
            var x = Math.Clamp(w.X, 0, layout.CanvasWidth - 1);
            var y = Math.Clamp(w.Y, 0, layout.CanvasHeight - 1);
            var label = new OverlayLabel
            {
                LabelId = labelId,
                Line = 0,
                Background = w.Badge ? OverlayLabel.Types.Background.GradientHorizontal : OverlayLabel.Types.Background.None,
                TextFont = FontName(w.Weight),
                TextSize = (uint)Math.Clamp(w.Size, 8, 600),
                TextColor = ParseColor(w.Color),
                Text = BadgeText(w, textOf(w)),
            };
            if (w.Badge)
            {
                label.BackgroundColor = ParseColor(w.BadgeFrom);
                label.GradientColor = ParseColor(w.BadgeTo);
            }
            overlay.LabelGroups.Add(new OverlayGroup
            {
                GroupId = groupId,
                GroupX = (uint)x,
                GroupY = (uint)y,
                GroupWidth = (uint)Math.Clamp(w.Width, 1, layout.CanvasWidth - x),
                GroupHeight = (uint)Math.Clamp(w.Height, 1, layout.CanvasHeight - y),
                TextAlign = w.Align switch
                {
                    TextAlign.Center => OverlayGroup.Types.TextAlignment.AlignCenter,
                    TextAlign.Right => OverlayGroup.Types.TextAlignment.AlignRight,
                    _ => OverlayGroup.Types.TextAlignment.AlignLeft,
                },
                LineGap = 0,
                Labels = { label },
            });
        }
        return overlay;
    }

    /// <summary>Text updates for the widgets whose content changes over time.</summary>
    public static MetricBatch CompileUpdates(ScreenLayout layout, Func<Widget, string> textOf)
    {
        var batch = new MetricBatch();
        var widgets = Visible(layout);
        for (var i = 0; i < widgets.Count && i < MaxWidgets; i++)
        {
            var w = widgets[i];
            if (!IsDynamic(w.Content))
                continue;
            var (groupId, labelId) = IdPool[i];
            batch.LabelGroups.Add(new OverlayGroupUpdate
            {
                GroupId = groupId,
                LabelTexts = { new OverlayLabelUpdate { LabelId = labelId, Text = BadgeText(w, textOf(w)) } },
            });
        }
        return batch;
    }

    public static bool IsDynamic(WidgetContent content) => content is WidgetContent.Sensor or WidgetContent.Clock or WidgetContent.Date;

    /// <summary>Badges get a little horizontal padding inside the pill.</summary>
    private static string BadgeText(Widget w, string text) => w.Badge ? $"  {text}  " : text;

    public static string FontName(FontWeightName weight) => weight switch
    {
        FontWeightName.Light => "roboto-light",
        FontWeightName.Medium => "roboto-medium",
        FontWeightName.Bold => "roboto-bold",
        _ => "roboto-regular",
    };

    /// <summary>"#RRGGBB" → 0x00RRGGBB (the firmware ignores the high byte).</summary>
    public static uint ParseColor(string? hex)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? rgb & 0x00FFFFFF
            : 0x00FFFFFF;
    }
}
