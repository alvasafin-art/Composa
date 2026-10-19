using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SmartFilterMaskTests
{
    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Stack_mask_crossfades_filtered_RGBA_with_original_and_keeps_profile_and_precision(ColorDepth depth)
    {
        var document = new Document(80, 60) { Depth = depth, Profile = RgbProfile.DisplayP3 };
        var source = Pixels.NewWorkingColor(60, 40, depth, document.Profile); var sourcePixels = new PixelBuffer(source);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 60; x++) sourcePixels.Write(x, y, new(.2f + x / 65535f, .4f, .1f, .7f));
        var layer = Layer.Raster("Filtered", source); document.InsertAboveActive(layer); var session = new EditorSession(document);
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 2 });
        var filtered = layer.Pixels!; var filteredBytes = filtered.Bytes;
        layer.SmartFilterMask = Pixels.NewMask(16, 20); layer.SmartFilterMaskX = 5; layer.SmartFilterMaskY = 8; layer.SmartFilterMaskDefault = 128;
        var result = layer.RenderFilterPixels()!; var actual = new PixelBuffer(result); var raw = new PixelBuffer(filtered); var before = new PixelBuffer(layer.FilterSource!);
        Assert.Equal(document.Profile, Pixels.ProfileOf(result));
        var tolerance = depth == ColorDepth.Sixteen ? .00001 : 1.1 / 255;
        for (var y = 0; y < result.Height; y++) for (var x = 0; x < result.Width; x++)
        {
            var sx = x - layer.FilterPaddingX; var sy = y - layer.FilterPaddingY;
            var b = sx >= 0 && sy >= 0 && sx < source.Width && sy < source.Height ? before.Read(sx, sy) : default;
            var coverage = sx >= 5 && sx < 21 && sy >= 8 && sy < 28 ? 0 : 128f / 255;
            var expected = b * (1 - coverage) + raw.Read(x, y) * coverage;
            for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(actual.Read(x, y)[c] - expected[c]), 0, tolerance);
        }
        Assert.Equal(filteredBytes, filtered.Bytes);
        using var flat = session.Flatten(); Assert.True(flat.GetPixel(10, 15).Alpha > 0); // A black filter mask retains the object.
        layer.SmartFilterMaskEnabled = false; Assert.Same(filtered, layer.RenderFilterPixels());
    }

    [Fact]
    public void Live_mask_generations_refresh_the_appearance_without_changing_cached_filter_pixels()
    {
        var session = EditorSession.NewCanvas(40, 30, SKColors.Red); var layer = session.ActiveLayer!;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = 50, Seed = 12 });
        var filtered = layer.Pixels!; var bytes = filtered.Bytes;
        layer.SmartFilterMask = Pixels.NewMask(40, 30); var first = layer.RenderFilterPixels()!; var firstBytes = first.Bytes;
        layer.SmartFilterMask.Erase(SKColors.White); Pixels.Invalidate(layer.SmartFilterMask);
        var second = layer.RenderFilterPixels()!; Assert.NotSame(first, second);
        Assert.Equal(bytes, second.Bytes); Assert.Equal(firstBytes, first.Bytes); Assert.Equal(bytes, filtered.Bytes);
    }

    [Fact]
    public void Fill_brush_and_invert_edit_only_the_stack_mask_and_undo_restores_its_source()
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.Blue); var layer = session.ActiveLayer!; var original = layer.Pixels!;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = 50, Seed = 12 }); var filtered = layer.Pixels!; var bytes = filtered.Bytes;
        session.AddSmartFilterMask(layer, hideAll: true); var mask = layer.SmartFilterMask;
        Assert.True(session.IsEditingSmartFilterMask); Assert.True(session.CanEditPixels); Assert.Null(layer.Mask);
        Assert.Equal(original.Bytes, layer.RenderFilterPixels()!.Bytes);
        session.SelectRect(new(10, 10, 30, 30)); session.Fill(SKColors.White); session.Deselect();
        Assert.Equal(filtered.GetPixel(20, 20), layer.RenderFilterPixels()!.GetPixel(20, 20));
        Assert.Equal(SKColors.Blue, layer.RenderFilterPixels()!.GetPixel(5, 5));
        Assert.Null(session.PaintStroke([new SKPoint(20, 20)], new BrushSettings { Size = 12, Hardness = 1, Opacity = 1 }, SKColors.Black));
        Assert.Equal(SKColors.Blue, layer.RenderFilterPixels()!.GetPixel(20, 20));
        Assert.Same(filtered, layer.Pixels); Assert.Equal(bytes, filtered.Bytes); Assert.Same(original, layer.FilterSource); Assert.Null(layer.Mask);
        session.BeginPreview("Invert smart filter mask"); session.PreviewAdjustment(new InvertAdjustment());
        Assert.Equal(255, layer.SmartFilterMaskDefault); Assert.Equal(0, layer.MaskDefault);
        session.CancelPreview(); layer = session.ActiveLayer!; Assert.Equal(0, layer.SmartFilterMaskDefault);
        session.SetSmartFilterMaskEnabled(layer, false); Assert.Same(filtered, layer.RenderFilterPixels());
        session.Undo(); Assert.True(session.ActiveLayer!.SmartFilterMaskEnabled);
        Assert.Equal(0, mask!.GetPixel(20, 20).Alpha); // The first committed mask was never painted in place.
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Mask_grid_survives_perspective_filter_growth_project_and_PSD_rasterization(ColorDepth depth)
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.Blue, depth: depth); var layer = session.ActiveLayer!;
        layer.Transform = layer.Transform with { Distort = [2, 0, -3, 1, 0, -2, 1, 0] };
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 2 }); var filter = layer.SmartFilters[0];
        session.AddSmartFilterMask(layer); var mask = layer.SmartFilterMask;
        var position = layer.SmartFilterMaskMatrix.MapPoint(20, 20);
        session.ChangeSmartFilter(layer, filter.Id, settings: filter.Settings with { Radius = 6 });
        var updated = layer.SmartFilterMaskMatrix.MapPoint(20, 20);
        Assert.InRange(Math.Abs(updated.X - position.X), 0, .001); Assert.InRange(Math.Abs(updated.Y - position.Y), 0, .001); Assert.Same(mask, layer.SmartFilterMask);
        session.Fill(SKColors.Black); Assert.Null(layer.Mask);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var document = ProjectFile.Read(stream); var actual = document.ActiveLayer!;
        Assert.Equal(layer.SmartFilterMask!.Bytes, actual.SmartFilterMask!.Bytes); Assert.Equal(layer.SmartFilterMaskX, actual.SmartFilterMaskX);
        using var expected = session.Flatten(); using var restored = DocumentRenderer.Flatten(document); Assert.Equal(expected.Bytes, restored.Bytes);
        var bounds = Geometry.RoundOut(actual.Bounds); using var independentArea = DocumentRenderer.RenderLayers(document, document.Layers, bounds);
        for (var y = 0; y < expected.Height; y++) for (var x = 0; x < expected.Width; x++)
            Assert.InRange(Math.Abs(expected.GetPixel(x, y).Alpha - independentArea.GetPixel(x - bounds.Left, y - bounds.Top).Alpha), 0, 2);
        using var psd = new MemoryStream(); PsdExport.Write(document, psd); var imported = PsdImport.Load(psd.ToArray()); using var appearance = DocumentRenderer.Flatten(imported.ToDocument());
        for (var y = 0; y < expected.Height; y++) for (var x = 0; x < expected.Width; x++)
            Assert.True(Math.Abs(expected.GetPixel(x, y).Alpha - appearance.GetPixel(x, y).Alpha) <= 2,
                $"PSD alpha at {x},{y}: {expected.GetPixel(x, y).Alpha} vs {appearance.GetPixel(x, y).Alpha}; original {layer.Bounds}; imported {imported.Layers[0].Bounds}; raw imported alpha {imported.Layers[0].Pixels!.GetPixel(0, 0).Alpha}");
        var editor = new EditorSession(document); editor.RasterizeShape(actual);
        Assert.Null(actual.SmartFilterMask); Assert.Null(actual.FilterSource); using var rasterized = editor.Flatten();
        var beforeRasterize = new PixelBuffer(restored); var afterRasterize = new PixelBuffer(rasterized);
        for (var y = 0; y < restored.Height; y++) for (var x = 0; x < restored.Width; x++) for (var c = 0; c < 4; c++)
            Assert.InRange(Math.Abs(beforeRasterize.Read(x, y)[c] - afterRasterize.Read(x, y)[c]), 0, 2.1 / 255);
    }

    [Fact]
    public void Smart_object_update_resizes_the_filter_mask_source_grid_and_undo_keeps_the_old_mask()
    {
        var session = EditorSession.NewCanvas(40, 30, SKColors.Blue); var layer = session.ConvertToSmartObject();
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 2 }); session.AddSmartFilterMask(layer);
        session.SelectRect(new(0, 0, 20, 30)); session.Fill(SKColors.Black); session.Deselect(); var oldMask = layer.SmartFilterMask;
        var oldX = layer.SmartFilterMaskX; var oldWidth = oldMask!.Width;
        var contents = new EditorSession(layer.SmartObject!.OpenDocument()); contents.ResizeImage(80, 60);
        session.UpdateSmartObject(layer.SmartObject, contents.Document);
        Assert.Equal(oldWidth * 2, layer.SmartFilterMask!.Width); Assert.Equal(oldX * 2, layer.SmartFilterMaskX);
        session.Undo(); Assert.Same(oldMask, session.ActiveLayer!.SmartFilterMask);
    }

    [Fact]
    public void Perspective_sampling_is_independent_of_the_render_origin()
    {
        var matrix = new SKMatrix(.99f, 0, 0, 0, .99f, 0, .002f, 0, 1);
        var shifted = matrix.PostConcat(SKMatrix.CreateTranslation(100, 20));
        Assert.Equal(DocumentRenderer.SamplingFor(matrix), DocumentRenderer.SamplingFor(shifted));
    }

    [Fact]
    public void Mask_histograms_read_one_byte_per_pixel_and_include_black_as_an_editable_value()
    {
        using var mask = Pixels.NewMask(3, 2); var values = mask.GetPixelSpan();
        values[0] = 0; values[1] = 64; values[2] = 255; values[mask.RowBytes] = 0; values[mask.RowBytes + 1] = 128; values[mask.RowBytes + 2] = 255;
        var histogram = Histogram.Of(mask);
        for (var channel = 0; channel < 4; channel++)
        {
            Assert.Equal(6, histogram.Channel(channel).Sum()); Assert.Equal(2, histogram.Channel(channel)[0]);
            Assert.Equal(1, histogram.Channel(channel)[128]); Assert.Equal(2, histogram.Channel(channel)[255]);
        }
    }

    [Fact]
    public void Layer_effects_use_masked_filter_appearance_instead_of_the_unmasked_blur_cache()
    {
        var session = EditorSession.NewCanvas(64, 64, SKColors.Transparent); var layer = session.ActiveLayer!;
        session.SelectRect(new(22, 22, 42, 42)); session.Fill(SKColors.Red); session.Deselect();
        layer.Effects = new() { Shadow = new() { Blur = 2, Distance = 5, Opacity = 1 } };
        using var original = session.Flatten();
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 3 }); session.AddSmartFilterMask(layer, hideAll: true);
        using var masked = session.Flatten(); var a = new PixelBuffer(original); var b = new PixelBuffer(masked);
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) for (var c = 0; c < 4; c++)
            Assert.InRange(Math.Abs(a.Read(x, y)[c] - b.Read(x, y)[c]), 0, 1.1 / 255);
    }
}
