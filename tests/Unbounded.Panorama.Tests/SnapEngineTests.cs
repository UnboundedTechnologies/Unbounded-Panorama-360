using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.Tests;

public class SnapEngineTests
{
    private const double W = 1080;
    private const double H = 2240;

    [Fact]
    public void Centre_of_box_snaps_to_screen_centre()
    {
        var moving = new Box(86, 500, 900, 100); // centre 536, 4 px off
        var r = SnapEngine.SnapMove(moving, [], W, H, threshold: 8);
        Assert.Equal(90, r.X);
        Assert.Contains(r.Guides, g => g.Vertical && g.Position == 540 && g.Label == "centre");
    }

    [Fact]
    public void Left_edge_snaps_to_other_widget_left_edge()
    {
        var other = new Box(200, 100, 300, 80);
        var moving = new Box(205, 600, 120, 80); // 5 px right of the other's left edge
        var r = SnapEngine.SnapMove(moving, [other], W, H, threshold: 8);
        Assert.Equal(200, r.X);
        Assert.Contains(r.Guides, g => g.Vertical && g.Position == 200);
    }

    [Fact]
    public void Nothing_snaps_beyond_threshold()
    {
        var other = new Box(200, 100, 300, 80);
        var moving = new Box(300, 700, 113, 77);
        var r = SnapEngine.SnapMove(moving, [other], W, H, threshold: 2);
        Assert.Equal(300, r.X);
        Assert.Equal(700, r.Y);
    }

    [Fact]
    public void Vertical_stack_snaps_to_existing_gap()
    {
        // Column at x 100..500: A ends at 100, B starts at 150 → gap 50. Moving lands 46 below B.
        var a = new Box(100, 0, 400, 100);
        var b = new Box(100, 150, 400, 100);
        var moving = new Box(100, 296, 400, 100);
        var r = SnapEngine.SnapMove(moving, [a, b], W, H, threshold: 8);
        Assert.Equal(300, r.Y);
        Assert.Equal(2, r.Guides.Count(g => g.IsSpacing && g.Label == "50"));
    }

    [Fact]
    public void Box_centres_between_two_neighbours()
    {
        var left = new Box(0, 500, 200, 100);
        var right = new Box(800, 500, 200, 100);
        var moving = new Box(397, 500, 200, 100); // centred position is 400 (gaps 200/200)
        var r = SnapEngine.SnapMove(moving, [left, right], W, H, threshold: 8);
        Assert.Equal(400, r.X);
    }

    [Fact]
    public void Resize_snaps_to_equal_width()
    {
        var other = new Box(100, 100, 600, 80);
        var moving = new Box(100, 400, 596, 80);
        var (width, guides) = SnapEngine.SnapWidth(moving, [other], W, H, threshold: 8);
        Assert.Equal(600, width);
        Assert.Contains(guides, g => g.IsSpacing && g.Label == "600");
    }
}

public class LayoutCompilerTests
{
    [Fact]
    public void Hidden_widgets_are_not_sent_and_ids_stay_consistent()
    {
        var layout = new ScreenLayout
        {
            Widgets =
            [
                new Widget { Content = WidgetContent.Text, Text = "a" },
                new Widget { Content = WidgetContent.Clock, Hidden = true },
                new Widget { Content = WidgetContent.Clock },
            ],
        };
        var overlay = LayoutCompiler.Compile(layout, w => "x");
        var updates = LayoutCompiler.CompileUpdates(layout, w => "y");

        Assert.Equal(2, overlay.LabelGroups.Count);
        // The visible clock is the second visible widget → second id pair.
        var (group, label) = LayoutCompiler.IdPool[1];
        Assert.Equal(group, overlay.LabelGroups[1].GroupId);
        Assert.Single(updates.LabelGroups);
        Assert.Equal(group, updates.LabelGroups[0].GroupId);
        Assert.Equal(label, updates.LabelGroups[0].LabelTexts[0].LabelId);
    }

    [Fact]
    public void Portrait_uses_rotation_270_verified_on_hardware()
    {
        Assert.Equal(270u, LayoutCompiler.UiRotation(Orientation.Portrait));
        Assert.Equal(0u, LayoutCompiler.UiRotation(Orientation.Landscape));
    }

    [Fact]
    public void Colors_are_0x00RRGGBB()
    {
        Assert.Equal(0x00FF7A59u, LayoutCompiler.ParseColor("#FF7A59"));
        Assert.Equal(0x00FFFFFFu, LayoutCompiler.ParseColor("not a colour"));
    }
}

public class FrameCodecTests
{
    [Fact]
    public void Reassembler_handles_split_and_garbage()
    {
        var frame = Protocol.FrameCodec.Encode([1, 2, 3, 4, 5]);
        var r = new Protocol.FrameReassembler();
        r.Append([0xAA, 0xBB]);          // noise before the magic
        r.Append(frame.AsSpan(0, 3));     // magic split across reads
        Assert.Null(r.TryTake());
        r.Append(frame.AsSpan(3));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, r.TryTake());
        Assert.Equal(2, r.DiscardedBytes);
    }
}
