using System.Buffers.Binary;
using Composa.ColorManagement;
using Composa.Filters;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HueBandTests
{
    [Fact]
    public void Feathering_crosses_red_continuously_and_four_edges_retain_a_full_circle()
    {
        var band = HueBand.FromEdges(315, 345, 15, 45); Assert.Equal(HueBand.Default(HueRange.Reds), band);
        foreach (var (hue, weight) in new[] { (315d, 0d), (330, .5), (345, 1), (0, 1), (15, 1), (30, .5), (45, 0), (180, 0) })
        { Assert.Equal(weight, band.Weight(hue), 10); Assert.Equal(weight, band.Weight(hue + 720), 10); }
        var circle = HueBand.FromEdges(0, 0, 360, 360); Assert.Equal(360, circle.Width); Assert.Equal(1, circle.Weight(145));
        Assert.Throws<ArgumentException>(() => HueBand.FromEdges(0, 180, 90, 270)); Assert.Throws<ArgumentException>(() => HueBand.FromEdges(double.NaN, 1, 2, 3));
        var normalized = new HueBand(-20, 40, 350, double.NaN).Normalized(); Assert.Equal(new HueBand(340, 40, 320, 0), normalized);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void All_ranges_classify_the_original_hue_instead_of_reclassifying_the_preceding_shift(ColorDepth depth)
    {
        using var pixels = Pixels.NewWorkingColor(2, 1, depth, RgbProfile.DisplayP3); var input = new PixelBuffer(pixels);
        input.Write(0, 0, new(.6f, 0, 0, .6f)); input.Write(1, 0, new(.6f, .6f, 0, .6f));
        var settings = new HueSaturationAdjustment().WithShift(HueRange.Reds, new(60, 0, 0)).WithShift(HueRange.Yellows, new(60, 0, 0));
        settings.Apply(pixels); var result = new PixelBuffer(pixels); var tolerance = depth == ColorDepth.Sixteen ? 1e-6 : .005;
        var expected = new[] { new PixelValue(.6f, .6f, 0, .6f), new PixelValue(0, .6f, 0, .6f) };
        for (var i = 0; i < 2; i++) for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(result.Read(i, 0)[c] - expected[i][c]), 0, tolerance);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Custom_band_targets_cyan_with_a_reds_control_and_keeps_other_hues_and_alpha(ColorDepth depth)
    {
        using var pixels = Pixels.NewWorkingColor(2, 1, depth, RgbProfile.DisplayP3); var input = new PixelBuffer(pixels);
        input.Write(0, 0, new(0, .7f, .7f, .7f)); input.Write(1, 0, new(.4f, .1f, .1f, .5f)); var red = input.Read(1, 0);
        var settings = new HueSaturationAdjustment().WithShift(HueRange.Reds, new(60, 0, 0)).WithBand(HueRange.Reds, HueBand.FromEdges(170, 175, 185, 190));
        settings.Apply(pixels); var actual = new PixelBuffer(pixels); var tolerance = depth == ColorDepth.Sixteen ? 1e-6 : .005;
        Assert.InRange(actual.Read(0, 0).G, 0, tolerance); Assert.InRange(Math.Abs(actual.Read(0, 0).B - .7), 0, tolerance);
        for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(actual.Read(1, 0)[c] - red[c]), 0, tolerance);
        Assert.Equal(red.A, actual.Read(1, 0).A);
    }

    [Fact]
    public void Overlapping_local_bands_are_independent_of_the_order_of_color_families()
    {
        var a = new HueSaturationAdjustment().WithShift(HueRange.Reds, new(40, 20, -10)).WithShift(HueRange.Yellows, new(-20, -30, 25));
        var b = new HueSaturationAdjustment().WithShift(HueRange.Reds, a.Shifts[2]).WithShift(HueRange.Yellows, a.Shifts[1])
            .WithBand(HueRange.Reds, a.BandFor(HueRange.Yellows)).WithBand(HueRange.Yellows, a.BandFor(HueRange.Reds));
        using var first = Pixels.NewColor(360, 1, ColorDepth.Sixteen); var pixels = new PixelBuffer(first);
        for (var x = 0; x < 360; x++) { ColorMath.HslToRgb(x, .6, .45, out var r, out var g, out var blue); pixels.Write(x, 0, new((float)r, (float)g, (float)blue, 1)); }
        using var second = Pixels.Clone(first); a.Apply(first); b.Apply(second); Assert.Equal(first.Bytes, second.Bytes);
    }

    [Fact]
    public void CMPS_retains_custom_bands_and_legacy_missing_bands_have_the_default_intervals()
    {
        var settings = new HueSaturationAdjustment().WithShift(HueRange.Magentas, new(12, 20, 4)).WithBand(HueRange.Magentas, new(280.5, 12.5, 48, 17));
        var document = new Document(20, 20) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 }; document.InsertAboveActive(Layer.ForAdjustment(settings));
        using var stream = new MemoryStream(); ProjectFile.Write(document, stream); stream.Position = 0; var restored = ProjectFile.Read(stream);
        var adjustment = Assert.IsType<HueSaturationAdjustment>(restored.ActiveLayer!.Adjustment); Assert.True(settings.ContentEquals(adjustment));
        Assert.Equal(HueBand.Default(HueRange.Reds), new HueSaturationAdjustment().BandFor(HueRange.Reds));
        Assert.True(new HueSaturationAdjustment().WithBand(HueRange.Reds, new(10, 20, 30, 40)).IsIdentity);
    }

    [Fact]
    public void Photoshop_hue2_import_retains_signed_wrapped_edges_and_rejects_an_invalid_interval()
    {
        var data = PsdWriter.HueSaturation(false, (0, 0, 0), (0, 0, 0), (25, -10, 4));
        var boundaries = new short[] { -20, -10, 10, 20 };
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(16 + i * 2), boundaries[i]);
        var imported = Assert.IsType<HueSaturationAdjustment>(PsdAdjustments.Parse(new() { ["hue2"] = data }, out var approximate));
        Assert.True(approximate); Assert.Equal(new HslShift(25, -10, 4), imported.Shifts[1]); Assert.Equal(new HueBand(340, 10, 20, 10), imported.BandFor(HueRange.Reds));
        var invalid = new short[] { 0, 180, 90, 270 }; for (var i = 0; i < 4; i++) BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(16 + i * 2), invalid[i]);
        Assert.Null(PsdAdjustments.Parse(new() { ["hue2"] = data }, out _));
    }

    [Fact]
    public void Float_HSL_retains_adjacent_16_bit_levels_and_colorize_ignores_local_bands()
    {
        using var pixels = Pixels.NewWorkingColor(2, 1, ColorDepth.Sixteen, RgbProfile.DisplayP3); var input = new PixelBuffer(pixels);
        input.Write(0, 0, new(.5f, .2f, .1f, 1)); input.Write(1, 0, new(.5f + 1f / 65535, .2f, .1f, 1));
        new HueSaturationAdjustment().WithShift(HueRange.Master, new(5, 5, 0)).Apply(pixels);
        Assert.True(Math.Abs(input.Read(0, 0).R - input.Read(1, 0).R) > .1f / 65535);
        using var a = Pixels.Clone(pixels); using var b = Pixels.Clone(pixels);
        var tint = (new HueSaturationAdjustment { Colorize = true }).WithShift(HueRange.Master, new(300, 40, 0)); tint.Apply(a);
        tint.WithShift(HueRange.Reds, new(-180, -100, -100)).WithBand(HueRange.Reds, new(0, 0, 360, 0)).Apply(b); Assert.Equal(a.Bytes, b.Bytes);
    }
}
