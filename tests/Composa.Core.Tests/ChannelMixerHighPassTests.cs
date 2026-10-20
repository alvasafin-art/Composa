using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class ChannelMixerHighPassTests
{
    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Mixer_reads_original_channels_simultaneously_and_keeps_premultiplied_alpha(ColorDepth depth)
    {
        using var image = Pixels.NewWorkingColor(1, 1, depth, RgbProfile.DisplayP3); new PixelBuffer(image).Write(0, 0, new(.4f, .2f, .1f, .7f));
        var alpha = new PixelBuffer(image).Read(0, 0).A;
        new ChannelMixerAdjustment { Red = new(-50, 125, 25, 10), Green = new(0, 0, 100), Blue = new(100, 0, 0) }.Apply(image);
        var result = new PixelBuffer(image).Read(0, 0); var expected = new PixelValue(.145f, .1f, .4f, alpha);
        for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(result[c] - expected[c]), 0, depth == ColorDepth.Sixteen ? 1e-6 : .006);
        Assert.Equal(alpha, result.A); Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(image));
    }

    [Fact]
    public void Mixer_monochrome_constant_clipping_presets_and_identity_keep_independent_rows()
    {
        using var source = Pixels.NewColor(2, 1, ColorDepth.Sixteen); var p = new PixelBuffer(source); p.Write(0, 0, new(.4f, .2f, .1f, .7f)); p.Write(1, 0, default);
        var before = source.Bytes; new ChannelMixerAdjustment().Apply(source); Assert.Equal(before, source.Bytes);
        using var mono = Pixels.Clone(source); new ChannelMixerAdjustment { Monochrome = true }.Apply(mono); var value = new PixelBuffer(mono).Read(0, 0);
        Assert.InRange(Math.Abs(value.R - .26f), 0, 1e-6); Assert.Equal(value.R, value.G); Assert.Equal(value.G, value.B); Assert.Equal(.7f, value.A);
        using var clip = Pixels.Clone(source); new ChannelMixerAdjustment { Red = new(0, 0, 0, 200), Green = new(0, 0, 0, -200) }.Apply(clip);
        var clipped = new PixelBuffer(clip).Read(0, 0); Assert.Equal(.7f, clipped.R); Assert.Equal(0, clipped.G); Assert.Equal(.1f, clipped.B); Assert.Equal(default, new PixelBuffer(clip).Read(1, 0));
        Assert.Equal(new ChannelMix(200, -200, 0, 0), new ChannelMix(999, -999, double.NaN, double.PositiveInfinity).Normalized());
        Assert.Equal(new ChannelMix(100, 0, 0), ChannelMixerAdjustment.Presets["Swap Red / Blue"].Blue);
    }

    [Fact]
    public void Mixer_keeps_adjacent_16_bit_values_and_CMPS_retains_live_parameters_with_undo()
    {
        var document = new Document(2, 1) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var source = Pixels.NewWorkingColor(2, 1, document.Depth, document.Profile); var pixels = new PixelBuffer(source);
        pixels.Write(0, 0, new(.3f, .2f, .1f, .8f)); pixels.Write(1, 0, new(.3f + 1f / 65535, .2f, .1f, .8f)); var bytes = source.Bytes;
        document.InsertAboveActive(Layer.Raster("Original", source)); var session = new EditorSession(document);
        var settings = new ChannelMixerAdjustment { Red = new(70, 20, 10, 2), Blue = new(25, 0, 75) }; session.AddAdjustmentLayer(settings);
        using var view = session.Flatten(); Assert.True(new PixelBuffer(view).Read(1, 0).R - new PixelBuffer(view).Read(0, 0).R > .5f / 65535);
        using var stream = new MemoryStream(); ProjectFile.Write(document, stream); stream.Position = 0; var restored = ProjectFile.Read(stream);
        Assert.True(settings.ContentEquals(Assert.IsType<ChannelMixerAdjustment>(restored.ActiveLayer!.Adjustment))); Assert.Equal(bytes, source.Bytes);
        session.Undo(); Assert.Single(session.Document.Layers); Assert.Same(source, session.ActiveLayer!.Pixels);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void High_pass_flat_transparent_cutout_is_neutral_even_with_a_large_radius(ColorDepth depth)
    {
        using var source = Pixels.NewWorkingColor(80, 70, depth, RgbProfile.DisplayP3); var pixels = new PixelBuffer(source);
        for (var y = 30; y < 33; y++) for (var x = 35; x < 38; x++) pixels.Write(x, y, new(.12f, .03f, .07f, .2f));
        var bytes = source.Bytes; var (filtered, dx, dy) = ImageFilters.Run(source, new() { Kind = FilterKind.HighPass, Radius = 100 });
        using (filtered)
        {
            Assert.Equal((0, 0), (dx, dy)); Assert.Equal(source.Info, filtered.Info); var result = new PixelBuffer(filtered);
            for (var y = 0; y < 70; y++) for (var x = 0; x < 80; x++)
            {
                var alpha = pixels.Read(x, y).A; var actual = result.Read(x, y); Assert.Equal(alpha, actual.A);
                for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(actual[c] - alpha * .5), 0, depth == ColorDepth.Sixteen ? 1e-5 : .005);
            }
        }
        Assert.Equal(bytes, source.Bytes);
    }

    [Fact]
    public void High_pass_float_bands_match_a_full_float_reference_across_their_seams_and_clamp_canvas_edges()
    {
        using var source = Pixels.NewWorkingColor(85, 700, ColorDepth.Sixteen, RgbProfile.DisplayP3); var p = new PixelBuffer(source);
        for (var y = 0; y < 700; y++) for (var x = 0; x < 85; x++) p.Write(x, y, new(.2f + (x % 3) * .08f, .1f + (y % 11) * .02f, .05f, .7f));
        using var soft = Pixels.NewLike(source);
        using (var canvas = new SKCanvas(soft))
        using (var blur = SKImageFilter.CreateBlur(20, 20, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { ImageFilter = blur, BlendMode = SKBlendMode.Src }) canvas.DrawBitmap(source, 0, 0, paint);
        var (result, _, _) = ImageFilters.Run(source, new() { Kind = FilterKind.HighPass, Radius = 40 });
        using (result)
        {
            var actual = new PixelBuffer(result); var low = new PixelBuffer(soft);
            foreach (var y in new[] { 0, 1, 254, 255, 256, 257, 510, 511, 512, 513, 698, 699 }) for (var x = 0; x < 85; x++)
            {
                var input = p.Read(x, y); var value = input.Straight; var blurred = low.Read(x, y).Straight;
                var expected = new PixelValue(.5f + value.R - blurred.R, .5f + value.G - blurred.G, .5f + value.B - blurred.B, input.A).Premultiplied;
                for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(expected[c] - actual.Read(x, y)[c]), 0, 2e-6);
            }
        }
    }

    [Fact]
    public void High_pass_preserves_sub_byte_details_and_the_smart_stack_mask_reveals_original_alpha()
    {
        var source = Pixels.NewWorkingColor(512, 24, ColorDepth.Sixteen, RgbProfile.DisplayP3); var p = new PixelBuffer(source);
        for (var y = 0; y < 24; y++) for (var x = 0; x < 512; x++) p.Write(x, y, new(.2f + (x % 2) / 65535f, .1f, .05f, .5f));
        var (result, _, _) = ImageFilters.Run(source, new() { Kind = FilterKind.HighPass, Radius = 2 });
        using (result) Assert.True(Math.Abs(new PixelBuffer(result).Read(200, 12).R - new PixelBuffer(result).Read(201, 12).R) > .5f / 65535);
        var document = new Document(512, 24) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 }; var layer = Layer.Raster("Details", source); document.InsertAboveActive(layer); var session = new EditorSession(document);
        session.AddSmartFilter(layer, new() { Kind = FilterKind.HighPass, Radius = 2 }); Assert.Same(source, layer.FilterSource); Assert.Equal(0, layer.FilterPaddingX);
        session.AddSmartFilterMask(layer, hideAll: true); using var hidden = session.Flatten(); Assert.Equal(source.Bytes, hidden.Bytes);
        using var stream = new MemoryStream(); ProjectFile.Write(document, stream); stream.Position = 0; var loaded = ProjectFile.Read(stream); Assert.Equal(FilterKind.HighPass, loaded.ActiveLayer!.SmartFilters[0].Settings.Kind);
    }
}
