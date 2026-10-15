using System.Windows.Media;
using Unbounded.Panorama.App.Controls;

namespace Unbounded.Panorama.Tests;

public class ColorMathTests
{
    [Theory]
    [InlineData("#FF0000", 0, 1, 1)]
    [InlineData("#00FF00", 120, 1, 1)]
    [InlineData("#0000FF", 240, 1, 1)]
    [InlineData("#FFFFFF", 0, 0, 1)]
    [InlineData("#000000", 0, 0, 0)]
    public void Known_colours_convert_to_hsv(string hex, double h, double s, double v)
    {
        Assert.True(ColorPicker.TryParse(hex, out var c));
        var (hh, ss, vv) = ColorPicker.ToHsv(c);
        Assert.Equal(h, hh, 3);
        Assert.Equal(s, ss, 3);
        Assert.Equal(v, vv, 3);
    }

    [Fact]
    public void Every_rgb_value_survives_a_round_trip()
    {
        // Sample the cube densely; HSV → RGB must give back the exact bytes.
        for (var r = 0; r < 256; r += 15)
        for (var g = 0; g < 256; g += 15)
        for (var b = 0; b < 256; b += 15)
        {
            var c = Color.FromRgb((byte)r, (byte)g, (byte)b);
            var (h, s, v) = ColorPicker.ToHsv(c);
            Assert.Equal(c, ColorPicker.FromHsv(h, s, v));
        }
    }

    [Theory]
    [InlineData("#bfc7d5", "#BFC7D5")]
    [InlineData("BFC7D5", "#BFC7D5")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("  #00e5ff ", "#00E5FF")]
    public void Hex_parsing_accepts_common_forms(string input, string expected)
    {
        Assert.True(ColorPicker.TryParse(input, out var c));
        Assert.Equal(expected, ColorPicker.ToHex(c));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void Invalid_hex_is_rejected(string input) => Assert.False(ColorPicker.TryParse(input, out _));
}
