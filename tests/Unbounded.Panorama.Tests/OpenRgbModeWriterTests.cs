using System.Buffers.Binary;
using System.Text;
using Unbounded.Panorama.Lighting;
using Xunit;

namespace Unbounded.Panorama.Tests;

/// <summary>The mode request, read back the way OpenRGB 1.0 reads it (RGBController::SetModeDescription, protocol 4).</summary>
public class OpenRgbModeWriterTests
{
    private sealed class Reader(byte[] data)
    {
        private int _at;
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_at)); _at += 4; return v; }
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_at)); _at += 2; return v; }
        public string Name(int length) { var s = Encoding.ASCII.GetString(data, _at, length - 1); Assert.Equal(0, data[_at + length - 1]); _at += length; return s; }
        public RgbColor Color() { var c = new RgbColor(data[_at], data[_at + 1], data[_at + 2]); _at += 4; return c; }
        public bool AtEnd => _at == data.Length;
    }

    // As OpenRGB 1.0 reported them on hardware: ENE Static (no direction: 0xFFFFFFFF) and Rainbow (direction 0).
    private static readonly ModeFields EneStatic = new(2, "Static", 1, 32, 0, 0, 0, 0, 0, 0, 0, 0, 0xFFFFFFFF, 1, []);
    private static readonly ModeFields EneRainbow = new(6, "Rainbow", 5, 3, 4, 0, 0, 0, 0, 0, 1, 0, 1, 0, []);
    private static readonly ModeFields AuraStatic = new(2, "Static", 1, 320, 0, 0, 0, 0, 1, 1, 0, 0, 0xFFFFFFFF, 2, [new RgbColor(9, 9, 9)]);

    [Fact]
    public void Fields_come_in_the_order_the_server_reads_them_and_the_size_is_right()
    {
        var bytes = OpenRgbModeWriter.Encode(EneRainbow, 3, []);
        var r = new Reader(bytes);
        Assert.Equal((uint)bytes.Length, r.U32()); // the server rejects a size that differs from the packet
        Assert.Equal(6u, r.U32());                 // mode index
        Assert.Equal("Rainbow", r.Name(r.U16()));
        Assert.Equal(5u, r.U32());                 // value
        Assert.Equal(3u, r.U32());                 // flags
        Assert.Equal(4u, r.U32());                 // speed min
        Assert.Equal(0u, r.U32());                 // speed max
        Assert.Equal(0u, r.U32());                 // brightness min
        Assert.Equal(0u, r.U32());                 // brightness max
        Assert.Equal(0u, r.U32());                 // colours min
        Assert.Equal(0u, r.U32());                 // colours max
        Assert.Equal(3u, r.U32());                 // speed (requested)
        Assert.Equal(0u, r.U32());                 // brightness
        Assert.Equal(1u, r.U32());                 // direction: a real one is kept
        Assert.Equal(0u, r.U32());                 // colour mode
        Assert.Equal(0, r.U16());                  // colours
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void A_mode_without_a_direction_is_sent_with_direction_zero()
    {
        // OpenRGB 1.0 reports 0xFFFFFFFF here but only accepts 0: echoing it made the request ignored.
        var r = new Reader(OpenRgbModeWriter.Encode(EneStatic, null, []));
        r.U32(); r.U32(); r.Name(r.U16());
        for (var i = 0; i < 10; i++)
            r.U32(); // value, flags, speed min/max, brightness min/max, colours min/max, speed, brightness
        Assert.Equal(0u, r.U32());
    }

    [Fact]
    public void A_mode_colour_replaces_the_current_one()
    {
        var bytes = OpenRgbModeWriter.Encode(AuraStatic, null, [new RgbColor(255, 128, 0)]);
        var r = new Reader(bytes);
        r.U32(); r.U32(); r.Name(r.U16());
        for (var i = 0; i < 12; i++)
            r.U32(); // value .. brightness (10), direction, colour mode
        Assert.Equal(1, r.U16());
        Assert.Equal(new RgbColor(255, 128, 0), r.Color());
        Assert.True(r.AtEnd);
    }
}
