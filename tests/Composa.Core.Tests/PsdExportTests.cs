using System.Buffers.Binary;
using Composa.Editing;
using Composa.Filters;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class PsdExportTests
{
    private static byte[] Write(Document document)
    {
        using var stream = new MemoryStream();
        PsdExport.Write(document, stream);
        return stream.ToArray();
    }

    private static void SamePicture(Document expected, Document actual, int tolerance = 1)
    {
        using var a = DocumentRenderer.Flatten(expected);
        using var b = DocumentRenderer.Flatten(actual);
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        var left = a.GetPixelSpan(); var right = b.GetPixelSpan();
        for (var i = 0; i < left.Length; i++) Assert.InRange(Math.Abs(left[i] - right[i]), 0, tolerance);
    }

    [Fact]
    public void Raster_layers_nested_folders_masks_clipping_unicode_and_resolution_round_trip()
    {
        var document = new Document(64, 48) { Resolution = 300 };
        document.Layers.Add(Layer.Raster("Background", Solid(64, 48, SKColors.Wheat)));
        var folder = Layer.Group("Группа ✓"); folder.Collapsed = true;
        var inner = Layer.Group("Inner"); inner.Opacity = 200 / 255.0;
        var child = Layer.Raster("Слой 🖌", Solid(20, 12, new SKColor(40, 180, 220, 180)), -3, 8);
        child.Mask = Pixels.NewMask(20, 12, 128);
        child.Mask.GetPixelSpan().Slice(0, 20).Fill(0); Pixels.Invalidate(child.Mask);
        inner.Children.Add(child);
        var clipped = Layer.Raster("Clip", Solid(12, 10, SKColors.Red), 2, 10); clipped.Clipped = true; clipped.Opacity = 128 / 255.0;
        inner.Children.Add(clipped);
        folder.Children.Add(inner);
        folder.Mask = Pixels.NewMask(64, 48, 210);
        document.Layers.Add(folder);
        var hidden = Layer.Raster("Hidden", Solid(2, 2, SKColors.Black), 70, 50); hidden.Visible = false;
        hidden.Mask = Pixels.NewMask(2, 2); hidden.MaskEnabled = false;
        document.Layers.Add(hidden);
        var bytes = Write(document);
        Assert.Equal("8BPS", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(12)));
        document.Guides.Add(new Guide(Guid.NewGuid(), GuideAxis.Vertical, 12.5));
        document.Guides.Add(new Guide(Guid.NewGuid(), GuideAxis.Horizontal, -4));
        bytes = Write(document);
        var imported = PsdImport.Load(bytes);
        Assert.Empty(imported.Conversions);
        var read = imported.ToDocument();
        Assert.Equal(300, read.Resolution);
        Assert.Equal(document.Guides.Select(g => (g.Axis, g.Position)), read.Guides.Select(g => (g.Axis, g.Position)));
        Assert.Equal(["Background", "Группа ✓", "Hidden"], read.Layers.Select(l => l.Name));
        Assert.True(read.Layers[1].Collapsed);
        Assert.Equal("Слой 🖌", read.Layers[1].Children[0].Children[0].Name);
        Assert.True(read.Layers[1].Children[0].Children[1].Clipped);
        Assert.False(read.Layers[2].Visible); Assert.False(read.Layers[2].MaskEnabled);
        Assert.Equal(-3, read.Layers[1].Children[0].Children[0].Transform.X);
        SamePicture(document, read);
        imported.Discard();
    }

    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.LinearBurn)]
    [InlineData(BlendMode.Screen)]
    [InlineData(BlendMode.VividLight)]
    [InlineData(BlendMode.Divide)]
    public void Blend_modes_on_layers_and_groups_are_preserved(BlendMode blend)
    {
        var document = new Document(12, 8);
        document.Layers.Add(Layer.Raster("Base", Solid(12, 8, SKColors.Wheat)));
        var folder = Layer.Group("Blend group"); folder.Blend = blend;
        var layer = Layer.Raster("Layer", Solid(7, 5, SKColors.CornflowerBlue), 1, 1); layer.Blend = blend;
        folder.Children.Add(layer); document.Layers.Add(folder);
        var imported = PsdImport.Load(Write(document));
        Assert.Empty(imported.Conversions);
        Assert.Equal(blend, imported.Layers[1].Blend);
        Assert.Equal(blend, imported.Layers[1].Children[0].Blend);
        SamePicture(document, imported.ToDocument()); imported.Discard();
    }

    [Fact]
    public void Effects_and_transforms_are_applied_without_applying_the_mask_twice()
    {
        var document = new Document(80, 60);
        var layer = Layer.Raster("Effects", Solid(20, 10, SKColors.Coral), 20, 20);
        layer.Transform = layer.Transform with { Rotation = 27, Width = 32, Height = 17 };
        layer.Mask = Pixels.NewMask(20, 10, 128);
        layer.Effects = new LayerEffects { GradientOverlay = new GradientOverlayEffect { StartColor = 0xFF0000FF, EndColor = 0xFFFF0000 }, Shadow = new ShadowEffect { Distance = 3, Blur = 2 } };
        document.Layers.Add(layer);
        var notes = PsdExport.Conversions(document);
        Assert.Contains(notes, n => n.Message.Contains("effects"));
        var originalPixels = layer.Pixels; var originalMask = layer.Mask;
        var imported = PsdImport.Load(Write(document));
        Assert.Empty(imported.Conversions);
        Assert.Null(imported.Layers[0].Effects); Assert.Null(imported.Layers[0].Mask);
        SamePicture(document, imported.ToDocument());
        Assert.Same(originalPixels, layer.Pixels); Assert.Same(originalMask, layer.Mask);
        Assert.Equal(128, layer.Mask.GetPixelSpan()[0]);
        imported.Discard();
    }

    [Fact]
    public void A_rotated_mask_remains_an_editable_separate_channel()
    {
        var document = new Document(40, 40);
        var layer = Layer.Raster("Rotated", Solid(12, 8, SKColors.Blue), 10, 10);
        layer.Transform = layer.Transform with { Rotation = 30 };
        layer.Mask = Pixels.NewMask(12, 8, 128);
        document.Layers.Add(layer);
        var imported = PsdImport.Load(Write(document));
        Assert.NotNull(imported.Layers[0].Mask);
        SamePicture(document, imported.ToDocument(), tolerance: 2);
        imported.Discard();
    }

    [Fact]
    public void Adjustment_export_has_an_exact_appearance_and_hidden_separate_sources()
    {
        var document = new Document(24, 16);
        document.Layers.Add(Layer.Raster("Base", Solid(24, 16, SKColors.Orange)));
        document.Layers.Add(Layer.ForAdjustment(new InvertAdjustment()));
        var top = Layer.Raster("Top", Solid(8, 8, SKColors.Red), 6, 2); top.Opacity = 0.5; document.Layers.Add(top);
        Assert.Contains(PsdExport.Conversions(document), n => n.LayerName == "Document");
        var imported = PsdImport.Load(Write(document));
        Assert.Empty(imported.Conversions);
        Assert.Equal(2, imported.Layers.Count);
        Assert.False(imported.Layers[0].Visible); Assert.True(imported.Layers[0].IsGroup);
        Assert.Equal(["Base", "Top"], imported.Layers[0].Children.Select(l => l.Name));
        Assert.Equal(PsdExport.AppearanceLayer, imported.Layers[1].Name);
        SamePicture(document, imported.ToDocument()); imported.Discard();
        Assert.Equal(3, document.Layers.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(DocumentLimits.MaxSide)]
    public void PackBits_handles_random_runs_literals_and_row_boundaries(int width)
    {
        var random = new Random(1);
        var row = new byte[width]; random.NextBytes(row);
        for (var i = 0; i + 10 < width; i += 29) row.AsSpan(i, 10).Fill(90);
        var packed = new byte[width * 2 + 2];
        var count = PsdExport.PackBits(row, packed);
        var decoded = new byte[width];
        PsdChannels.UnpackRows(packed.AsSpan(0, count), new[] { count }, width, 1, decoded, 0);
        Assert.Equal(row, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void High_depth_channel_decoding_preserves_samples_prediction_and_crops(int compression)
    {
        ushort[] values = [0, 128, 32768, 65535, 500, 40000, 40000, 256];
        var raw = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(i * 2), values[i]);
        byte[] data;
        if (compression == 0) data = raw;
        else if (compression == 1)
        {
            var rows = PsdWriter.PackRows(raw, 8, 2); var buffer = new PsdWriter.Buffer();
            foreach (var row in rows) buffer.U16((ushort)row.Length);
            foreach (var row in rows) buffer.Bytes(row);
            data = buffer.ToArray();
        }
        else
        {
            if (compression == 3)
                for (var y = 0; y < 2; y++)
                for (var x = 3; x > 0; x--)
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan((y * 4 + x) * 2), unchecked((ushort)(values[y * 4 + x] - values[y * 4 + x - 1])));
            using var packed = new MemoryStream();
            using (var zip = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) zip.Write(raw);
            data = packed.ToArray();
        }
        var plane = PsdChannels.Decode(compression, 4, 2, data, depth: 16);
        Assert.Equal(new byte[] { 0, 0, 128, 255, 2, 156, 156, 1 }, plane);
        Assert.Equal(new byte[] { 0, 128, 156, 156 }, PsdChannels.Decode(compression, 4, 2, data, crop: new PsdCrop(1, 0, 2, 2), depth: 16));
    }

    [Fact]
    public void Photoshop_merged_transparency_round_trips_without_a_white_fringe()
    {
        var document = new Document(5, 3);
        document.Layers.Add(Layer.Raster("Alpha", Solid(5, 3, new SKColor(50, 140, 230, 90))));
        var imported = PsdImport.LoadAppearance(Write(document), DocumentLimits.DocumentPixelBudget);
        SamePicture(document, imported.ToDocument()); imported.Discard();
    }

    [Fact]
    public void Empty_canvas_round_trips_as_transparent()
    {
        var document = new Document(3, 2);
        var imported = PsdImport.Load(Write(document));
        SamePicture(document, imported.ToDocument()); imported.Discard();
    }

    [Fact]
    public void A_failed_save_leaves_the_existing_destination_untouched_and_removes_its_temporary_file()
    {
        var folder = Directory.CreateTempSubdirectory("psd-export-").FullName;
        var path = Path.Combine(folder, "existing.psd");
        try
        {
            File.WriteAllText(path, "original");
            var document = new Document(2, 2) { Width = DocumentLimits.MaxSide + 1 };
            Assert.Throws<PsdException>(() => PsdExport.Save(document, path));
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
