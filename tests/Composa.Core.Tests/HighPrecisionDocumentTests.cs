using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HighPrecisionDocumentTests
{
    [Fact]
    public void Gaussian_blur_keeps_the_low_bits_of_a_smooth_gradient()
    {
        var document = Scene();
        using var result = Pixels.Clone(document.Layers[0].Pixels!);
        new GaussianBlurAdjustment { Radius = 2 }.Apply(result);
        var pixels = new PixelBuffer(result);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => pixels.Read(x, 4).R).Distinct().Count());
        Assert.InRange(Math.Abs(pixels.Read(256, 4).R - (24000 + 256) / 65535f), 0, 1e-6);
    }

    private static Document Scene()
    {
        var document = new Document(512, 8) { Depth = ColorDepth.Sixteen };
        var source = Pixels.NewColor(512, 8, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(source);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 512; x++)
        {
            var value = (24000 + x) / 65535f;
            pixels.Write(x, y, new(value, value, value, 1));
        }
        document.InsertAboveActive(Layer.Raster("Fine gradient", source));
        return document;
    }

    [Fact]
    public void Layers_adjustments_history_project_and_export_retain_real_16_bit_detail()
    {
        var document = Scene();
        var session = new EditorSession(document);
        session.Apply("Exposure", () => document.Layers.Add(Layer.ForAdjustment(new ExposureAdjustment { Exposure = .5 })));
        session.InvalidateAll();
        using var edited = session.Flatten();
        Assert.Equal(SKColorType.RgbaF32, edited.ColorType);
        var colors = new PixelBuffer(edited);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => colors.Read(x, 4).R).Distinct().Count());
        using var projectBytes = new MemoryStream(); ProjectFile.Write(document, projectBytes); projectBytes.Position = 0;
        var project = ProjectFile.Read(projectBytes);
        Assert.Equal(ColorDepth.Sixteen, project.Depth);
        Assert.Equal(SKColorType.RgbaF32, project.Layers[0].Pixels!.ColorType);
        using var reloaded = DocumentRenderer.Flatten(project);
        var loadedColors = new PixelBuffer(reloaded);
        for (var x = 0; x < 512; x++) Assert.InRange(Math.Abs(colors.Read(x, 4).R - loadedColors.Read(x, 4).R), 0, 1e-7);
        using var png = new MemoryStream(ImageFiles.Encode(reloaded, ExportFormat.Png));
        using var exported = Png16Codec.TryDecode(png)!;
        var exportedColors = new PixelBuffer(exported);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => exportedColors.Read(x, 4).R).Distinct().Count());
        session.Undo(); Assert.Single(session.Document.Layers); Assert.Equal(ColorDepth.Sixteen, session.Document.Depth);
        session.Redo(); Assert.Equal(2, session.Document.Layers.Count);
    }

    [Fact]
    public void Rendering_to_a_display_buffer_does_not_reduce_the_document_or_its_export()
    {
        var document = Scene();
        using var display = Pixels.NewColor(128, 4);
        DocumentRenderer.Render(document, display, new SKRectI(0, 0, 128, 4), new RenderView(.25f, new(0, 0)));
        Assert.True(display.GetPixel(64, 1).Red > 0);
        Assert.Equal(SKColorType.RgbaF32, document.Layers[0].Pixels!.ColorType);
        using var flattened = DocumentRenderer.Flatten(document);
        var pixels = new PixelBuffer(flattened);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => pixels.Read(x, 4).R).Distinct().Count());
    }

    [Fact]
    public void Group_masks_Blend_If_and_color_overlays_keep_the_float_source()
    {
        var document = Scene();
        var layer = document.Layers[0];
        layer.Mask = Pixels.NewMask(512, 8, 255);
        layer.BlendIf = new() { Gray = new() { ThisLayer = new(0, 128, 255, 255) } };
        layer.Effects = new() { ColorOverlay = new() { Color = (uint)SKColors.Blue, Opacity = .1 } };
        var group = Layer.Group("Group"); group.Mask = Pixels.NewMask(512, 8, 200); group.Opacity = .8;
        document.Layers.Clear(); group.Children.Add(layer); document.Layers.Add(group);
        using var result = DocumentRenderer.Flatten(document);
        var pixels = new PixelBuffer(result);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => pixels.Read(x, 4).R).Distinct().Count());
        Assert.InRange(pixels.Read(10, 4).A, .3, .7);
        Assert.True(pixels.Read(10, 4).B > pixels.Read(10, 4).R);
    }

    [Theory]
    [InlineData(BlendMode.LinearBurn)]
    [InlineData(BlendMode.LinearDodge)]
    [InlineData(BlendMode.VividLight)]
    [InlineData(BlendMode.LinearLight)]
    [InlineData(BlendMode.PinLight)]
    [InlineData(BlendMode.HardMix)]
    [InlineData(BlendMode.Subtract)]
    [InlineData(BlendMode.Divide)]
    [InlineData(BlendMode.DarkerColor)]
    [InlineData(BlendMode.LighterColor)]
    public void Custom_modes_agree_across_working_precisions(BlendMode mode)
    {
        SKBitmap Render(ColorDepth depth)
        {
            var doc = new Document(8, 8) { Depth = depth };
            var bottom = Pixels.NewColor(8, 8, depth); bottom.Erase(new SKColor(200, 100, 50));
            var top = Pixels.NewColor(8, 8, depth); top.Erase(new SKColor(90, 180, 240));
            doc.Layers.Add(Layer.Raster("Bottom", bottom));
            var layer = Layer.Raster("Top", top); layer.Blend = mode; layer.Opacity = .6; doc.Layers.Add(layer);
            return DocumentRenderer.Flatten(doc);
        }
        using var eight = Render(ColorDepth.Eight); using var high = Render(ColorDepth.Sixteen);
        TestImages.AssertColor(eight.GetPixel(4, 4), high.GetPixel(4, 4), 2);
    }

    [Fact]
    public void History_budget_counts_float_channels_in_bytes_and_shared_sources_only_once()
    {
        var document = Scene();
        var original = document.Layers[0].Pixels!;
        document.Layers.Add(Layer.Raster("Shared", original));
        Assert.Equal((long)original.ByteCount / 4, document.RasterPixels());
        Assert.Equal(ColorDepth.Sixteen, document.Clone().Depth);
        Assert.Same(original, document.Clone().Layers[0].Pixels);
    }
}
