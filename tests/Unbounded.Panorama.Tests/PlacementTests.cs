using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Media;

namespace Unbounded.Panorama.Tests;

public class PlacementTests
{
    private static BackgroundMedia Media(double x, double y, double w, int sw = 3840, int sh = 2160, ScreenArea area = ScreenArea.Whole) => new()
    {
        SourcePath = "x.mp4", SourceWidth = sw, SourceHeight = sh, MediaX = x, MediaY = y, MediaWidth = w, Area = area,
    };

    [Fact]
    public void Areas_split_portrait_at_the_bend()
    {
        Assert.Equal(new Box(0, 0, 1080, 2240), Placement.AreaBox(ScreenArea.Whole, Orientation.Portrait));
        Assert.Equal(new Box(0, 0, 1080, 1100), Placement.AreaBox(ScreenArea.TopFace, Orientation.Portrait));
        Assert.Equal(new Box(0, 1100, 1080, 1140), Placement.AreaBox(ScreenArea.FrontFace, Orientation.Portrait));
        // Landscape has no face split.
        Assert.Equal(new Box(0, 0, 2240, 1080), Placement.AreaBox(ScreenArea.TopFace, Orientation.Landscape));
    }

    [Fact]
    public void Fill_covers_the_area_and_is_centred()
    {
        var area = Placement.AreaBox(ScreenArea.Whole, Orientation.Portrait);
        var r = Placement.Fill(area, 3840, 2160); // 16:9 into 1080x2240: height limits
        Assert.Equal(2240, r.H, 6);
        Assert.Equal(3840.0 * 2240 / 2160, r.W, 6);
        Assert.Equal(540, r.CenterX, 6);
        Assert.Equal(1120, r.CenterY, 6);
    }

    [Fact]
    public void Fit_stays_inside_the_area_and_is_centred()
    {
        var area = Placement.AreaBox(ScreenArea.TopFace, Orientation.Portrait);
        var r = Placement.Fit(area, 3840, 2160); // width limits: 1080 x 607.5
        Assert.Equal(1080, r.W, 6);
        Assert.Equal(607.5, r.H, 6);
        Assert.Equal(550, r.CenterY, 6);
    }

    [Fact]
    public void Filter_crops_to_area_and_pads_at_position()
    {
        // Fill the top face: 16:9 scaled to cover 1080x1100 → 1955.55 x 1100, centred horizontally.
        var area = Placement.AreaBox(ScreenArea.TopFace, Orientation.Portrait);
        var r = Placement.Fill(area, 3840, 2160);
        var f = MediaPreparer.PlacementFilter(Media(r.X, r.Y, r.W, area: ScreenArea.TopFace), Orientation.Portrait, "SQ");
        // Even rounding: w 1956, h 1100, x -438, y 0; visible 1080x1100 at (0,0), crop offset 438.
        Assert.Equal("SQ,scale=1956:1100,setsar=1,crop=1080:1100:438:0,pad=1080:2240:0:0:color=black,setsar=1", f);
    }

    [Fact]
    public void Filter_handles_media_partly_off_screen_and_inside_front_face()
    {
        // 1000 px wide video (16:9 → 562.5 high) placed at (500, 1500) in the front face: right part is off-screen.
        var f = MediaPreparer.PlacementFilter(Media(500, 1500, 1000, area: ScreenArea.FrontFace), Orientation.Portrait, "SQ");
        Assert.Equal("SQ,scale=1000:562,setsar=1,crop=580:562:0:0,pad=1080:2240:500:1500:color=black,setsar=1", f);
    }

    [Fact]
    public void Media_entirely_outside_the_area_gives_black()
    {
        var f = MediaPreparer.PlacementFilter(Media(0, 1200, 1080, area: ScreenArea.TopFace), Orientation.Portrait, "SQ");
        Assert.Contains("pad=1080:2240:0:0:color=black", f);
        Assert.Contains("crop=2:2", f);
    }
}
