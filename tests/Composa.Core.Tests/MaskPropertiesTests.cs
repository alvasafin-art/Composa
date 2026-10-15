using Composa.Editing;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class MaskPropertiesTests
{
    private static Layer MaskedLayer()
    {
        var layer = Layer.Raster("Masked", Solid(64, 48, SKColors.Red));
        layer.Mask = SelectionMask.FromRect(64, 48, new SKRect(32, 0, 64, 48));
        return layer;
    }

    [Fact]
    public void Density_reveals_black_and_feather_softens_the_edge_without_mutating_the_mask()
    {
        var layer = MaskedLayer();
        var source = layer.Mask!;
        var bytes = source.Bytes;
        layer.MaskDensity = .5;
        layer.MaskFeather = 8;
        var effective = layer.RenderMask()!;
        Assert.Equal(128, effective.GetPixel(0, 24).Alpha);
        Assert.InRange((int)effective.GetPixel(31, 24).Alpha, 170, 200);
        Assert.Equal(255, effective.GetPixel(63, 24).Alpha);
        Assert.Equal(bytes, source.Bytes);
        Assert.Same(source, layer.Mask);
        Assert.Same(effective, layer.RenderMask());
        layer.MaskDensity = 1; layer.MaskFeather = 0;
        Assert.Same(source, layer.RenderMask());
    }

    [Fact]
    public void Feather_clamps_canvas_edges_and_never_fades_a_reveal_all_mask()
    {
        var layer = MaskedLayer();
        layer.Mask = Pixels.NewMask(64, 48, 255);
        layer.MaskFeather = 20;
        var mask = layer.RenderMask()!;
        Assert.Equal(255, mask.GetPixel(0, 0).Alpha);
        Assert.Equal(255, mask.GetPixel(63, 47).Alpha);
    }

    [Fact]
    public void Properties_cache_updates_after_painting_and_vector_masks_combine_once()
    {
        var layer = MaskedLayer();
        layer.MaskDensity = .5;
        var first = layer.RenderMask()!;
        // This bitmap is still uncommitted, as a brush's working mask would be.
        layer.Mask!.Erase(SKColors.White); Pixels.Invalidate(layer.Mask);
        var second = layer.RenderMask()!;
        Assert.NotSame(first, second);
        Assert.Equal(255, second.GetPixel(4, 4).Alpha);
        layer.VectorMask = new() { Closed = true, Nodes = [BezierNode.Corner(.25, .25), BezierNode.Corner(.75, .25), BezierNode.Corner(.75, .75), BezierNode.Corner(.25, .75)] };
        var combined = layer.RenderMask()!;
        Assert.Equal(0, combined.GetPixel(4, 4).Alpha);
        Assert.Equal(255, combined.GetPixel(32, 24).Alpha);
    }

    [Fact]
    public void Applying_and_undoing_properties_and_mask_preserves_the_original_source()
    {
        var document = new Document(64, 48);
        var layer = MaskedLayer(); document.InsertAboveActive(layer);
        var session = new EditorSession(document);
        var source = layer.Mask;
        session.SetMaskProperties(layer, .5, 8);
        using var before = DocumentRenderer.Flatten(document);
        session.ApplyMask(layer);
        using var after = DocumentRenderer.Flatten(document);
        Assert.Equal(before.Bytes, after.Bytes);
        Assert.Null(layer.Mask);
        session.Undo();
        Assert.Same(source, session.ActiveLayer!.Mask);
        Assert.Equal(.5, session.ActiveLayer.MaskDensity);
        session.Undo();
        Assert.Equal(1, session.ActiveLayer!.MaskDensity);
        Assert.Equal(0, session.ActiveLayer.MaskFeather);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Properties_round_trip_in_projects_and_PSD_keeps_native_values_when_possible(bool transformed, bool fill)
    {
        var document = new Document(96, 80);
        var layer = MaskedLayer(); document.Layers.Add(layer);
        layer.MaskDensity = 128 / 255.0; layer.MaskFeather = 8;
        if (transformed) layer.Transform = layer.Transform with { X = 12, Y = 10, Rotation = 10 };
        if (fill) layer.FillOpacity = .7;
        using var stream = new MemoryStream(); ProjectFile.Write(document, stream); stream.Position = 0;
        var project = ProjectFile.Read(stream);
        Assert.Equal(layer.MaskDensity, project.Layers[0].MaskDensity);
        Assert.Equal(layer.MaskFeather, project.Layers[0].MaskFeather);
        using var psd = new MemoryStream(); PsdExport.Write(document, psd);
        var imported = PsdImport.Load(psd.ToArray());
        if (!transformed)
        {
            Assert.Equal(layer.MaskDensity, imported.Layers[0].MaskDensity);
            Assert.Equal(layer.MaskFeather, imported.Layers[0].MaskFeather);
        }
        else Assert.Contains(PsdExport.Conversions(document), c => c.Message.Contains("Mask density"));
        using var before = DocumentRenderer.Flatten(document);
        using var after = DocumentRenderer.Flatten(imported.ToDocument());
        for (var y = 4; y < 70; y += 5)
        for (var x = 4; x < 85; x += 5) AssertColor(before.GetPixel(x, y), after.GetPixel(x, y), 3);
    }
}
