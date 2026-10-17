using System.Buffers.Binary;
using Unbounded.Panorama.Server.Lcd;
using Unbounded.Panorama.Server.Render;
using Xunit;

namespace Unbounded.Panorama.Tests;

/// <summary>The container edition's screen protocol and media placement (no hardware needed).</summary>
public class ContainerEditionTests
{
    [Fact]
    public void Frame_header_matches_what_the_screen_accepts()
    {
        // Captured on the wire from a Frozen Warframe Pro (320×320): 12 34 56 78, 3, 320, 320, …, 2 at 56, 204800 at 60.
        var frame = new byte[64];
        ThermalrightBulkScreen.WriteHeader(frame, 320, 320);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, frame[..4]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(320u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal(320u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Assert.Equal(2, frame[56]);
        Assert.Equal(204_800u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(60)));
        Assert.All(frame[16..56], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0, 0, 255, 0x00, 0x1F)]   // blue: 00 1F, as captured
    [InlineData(255, 0, 0, 0xF8, 0x00)]   // red
    [InlineData(0, 255, 0, 0x07, 0xE0)]   // green
    [InlineData(255, 255, 255, 0xFF, 0xFF)]
    public void Pixels_are_rgb565_high_byte_first(byte r, byte g, byte b, byte hi, byte lo)
    {
        var target = new byte[2];
        ThermalrightBulkScreen.ToRgb565([b, g, r, 255], target);
        Assert.Equal(new[] { hi, lo }, target);
    }

    [Fact]
    public void Known_panels_get_their_resolution()
    {
        Assert.Equal((320, 320), ThermalrightBulkScreen.Resolution(32));
        Assert.Equal((480, 480), ThermalrightBulkScreen.Resolution(0x48));
    }

    [Fact]
    public void Media_covers_the_screen_by_default_with_a_centered_crop()
    {
        // 16:9 video on a 320×320 screen: scaled to 568×320, the middle 320 columns visible.
        var p = MediaPlacement.Compute(320, 320, 1920, 1080, null, null, null)!;
        Assert.Equal(568, p.MediaWidth);
        Assert.Equal(320, p.MediaHeight);
        Assert.Equal((0, 0, 320, 320), (p.VisibleX, p.VisibleY, p.VisibleWidth, p.VisibleHeight));
        Assert.Equal(124, p.CropX);
    }

    [Fact]
    public void Media_placed_partly_off_screen_shows_only_the_visible_part()
    {
        var p = MediaPlacement.Compute(320, 320, 1000, 1000, -50, 100, 200)!;
        Assert.Equal((0, 100), (p.VisibleX, p.VisibleY));
        Assert.Equal((150, 200), (p.VisibleWidth, p.VisibleHeight));
        Assert.Equal((50, 0), (p.CropX, p.CropY));
    }

    [Fact]
    public void Media_entirely_off_screen_is_not_played()
    {
        Assert.Null(MediaPlacement.Compute(320, 320, 1000, 1000, 400, 0, 200));
    }
}
