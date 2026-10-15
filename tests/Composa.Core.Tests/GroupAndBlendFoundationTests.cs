using Composa.Filters;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class GroupAndBlendFoundationTests
{
    [Theory]
    [InlineData(BlendMode.Normal, 40, 80, 120)]
    [InlineData(BlendMode.PassThrough, 215, 175, 135)]
    public void Only_pass_through_groups_let_adjustments_affect_the_external_backdrop(BlendMode mode, byte r, byte g, byte b)
    {
        var document = new Document(16, 12);
        document.Layers.Add(Layer.Raster("Backdrop", Solid(16, 12, new SKColor(40, 80, 120))));
        var group = Layer.Group("Group");
        group.Blend = mode;
        group.Children.Add(Layer.ForAdjustment(new InvertAdjustment()));
        document.Layers.Add(group);
        using var composite = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(r, g, b), composite.GetPixel(8, 6));
    }

    [Fact]
    public void New_groups_default_to_pass_through_and_rasters_to_normal()
    {
        Assert.Equal(BlendMode.PassThrough, Layer.Group("Group").Blend);
        Assert.Equal(BlendMode.PassThrough, new Layer { Kind = LayerKind.Group }.Blend);
        Assert.Equal(BlendMode.Normal, Layer.Raster("Raster", Solid(1, 1, SKColors.Red)).Blend);
    }

    [Theory]
    [InlineData(BlendMode.DarkerColor, 200, 20, 40)]
    [InlineData(BlendMode.LighterColor, 30, 180, 170)]
    public void Whole_color_modes_choose_a_complete_pixel_instead_of_channel_minimums(BlendMode mode, byte r, byte g, byte b)
    {
        var document = new Document(8, 8);
        document.Layers.Add(Layer.Raster("Base", Solid(8, 8, new SKColor(200, 20, 40))));
        var top = Layer.Raster("Top", Solid(8, 8, new SKColor(30, 180, 170)));
        top.Blend = mode;
        document.Layers.Add(top);
        using var composite = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(r, g, b), composite.GetPixel(4, 4));
    }

    [Fact]
    public void Dissolve_is_binary_and_does_not_change_across_partial_render_boundaries()
    {
        var document = new Document(720, 480);
        var top = Layer.Raster("Dissolve", Solid(720, 480, SKColors.Red));
        top.Blend = BlendMode.Dissolve;
        top.Opacity = .4;
        document.Layers.Add(top);
        using var full = DocumentRenderer.Flatten(document);
        using var partial = Pixels.NewColor(720, 480);
        DocumentRenderer.Render(document, partial, new SKRectI(173, 71, 647, 413));
        var covered = 0;
        for (var y = 71; y < 413; y++)
        for (var x = 173; x < 647; x++)
        {
            var pixel = full.GetPixel(x, y);
            Assert.Equal(pixel, partial.GetPixel(x, y));
            Assert.True(pixel.Alpha is 0 or 255);
            if (pixel.Alpha == 255) covered++;
        }
        Assert.InRange(covered / (474.0 * 342), .39, .41);
        using var panned = Pixels.NewColor(120, 100);
        DocumentRenderer.Render(document, panned, new SKRectI(0, 0, 120, 100), new RenderView(1, new SKPoint(13, 27)));
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 120; x++) Assert.Equal(full.GetPixel(x + 13, y + 27), panned.GetPixel(x, y));
    }
}
