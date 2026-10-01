using System.IO.Compression;
using System.Text.Json.Nodes;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SmartObjectTests
{
    [Fact]
    public void Conversion_preserves_appearance_outer_properties_and_editable_text_and_undo()
    {
        var session = EditorSession.NewCanvas(160, 100, SKColors.White);
        var text = session.AddText(new SKPoint(12, 10), new TextStyle { Text = "Hello", Size = 20, Color = 0xFF3377DD });
        text.Opacity = .7; text.Transform = text.Transform with { Rotation = 12 };
        session.AddMask(text); session.EditingMask = false;
        using var before = session.Flatten();
        var transform = text.Transform; var mask = text.Mask;
        var smart = session.ConvertToSmartObject();
        Assert.True(smart.IsLive); Assert.False(session.CanEditPixels); Assert.Equal(transform, smart.Transform);
        Assert.Same(mask, smart.Mask); Assert.Equal(.7, smart.Opacity); Assert.Null(smart.Text);
        Assert.Equal("Hello", smart.SmartObject!.OpenDocument().ActiveLayer!.Text!.Text);
        using var after = session.Flatten(); Assert.Equal(before.GetPixelSpan().ToArray(), after.GetPixelSpan().ToArray());
        Assert.False(session.BeginStroke(new SKPoint(15, 15), out var problem)); Assert.Contains("contents", problem);
        session.Undo(); Assert.NotNull(session.ActiveLayer!.Text); Assert.Null(session.ActiveLayer.SmartObject);
        session.Redo(); Assert.True(session.ActiveLayer!.IsSmartObject);
    }

    [Fact]
    public void Updating_contents_updates_shared_instances_not_independent_copy_and_history_is_immutable()
    {
        var parent = EditorSession.NewCanvas(40, 30, SKColors.Red);
        parent.ConvertToSmartObject(); var source = parent.ActiveLayer!.SmartObject!;
        parent.DuplicateSelectedLayers();
        var independent = parent.DuplicateSmartObjectIndependent(parent.ActiveLayer!);
        var independentId = independent.Id;
        var contents = new EditorSession(source.OpenDocument()); contents.Fill(SKColors.Blue);
        parent.UpdateSmartObject(source, contents.Document);
        Assert.Equal(2, parent.Document.AllLayers().Count(layer => layer.Pixels!.GetPixel(5, 5) == SKColors.Blue));
        Assert.Equal(SKColors.Red, parent.Document.Find(independentId)!.Pixels!.GetPixel(5, 5));
        Assert.Equal(SKColors.Red, source.Preview.GetPixel(5, 5));
        parent.Undo(); Assert.All(parent.Document.Layers, layer => Assert.Equal(SKColors.Red, layer.Pixels!.GetPixel(5, 5)));
        parent.Redo(); Assert.Equal(2, parent.Document.AllLayers().Count(layer => layer.Pixels!.GetPixel(5, 5) == SKColors.Blue));
        Assert.Throws<InvalidOperationException>(() => parent.UpdateSmartObject(source, contents.Document));
    }

    [Fact]
    public void Embedded_content_round_trips_once_and_source_snapshots_are_structurally_private()
    {
        var parent = EditorSession.NewCanvas(60, 40, SKColors.Red);
        var shape = parent.AddShape(new ShapeStyle(ShapeKind.Rectangle, 0xFF2244BB, 0), new SKRect(10, 10, 30, 30))!;
        parent.SelectLayer(parent.Document.Layers[0].Id); parent.SelectLayer(shape.Id, extend: true);
        parent.ConvertToSmartObject(); parent.DuplicateSelectedLayers();
        var source = parent.ActiveLayer!.SmartObject!;
        var opened = source.OpenDocument(); opened.Layers.Clear();
        Assert.Equal(2, source.OpenDocument().Layers.Count);
        using var buffer = new MemoryStream(); ProjectFile.Write(parent.Document, buffer); buffer.Position = 0;
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
        {
            using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            var manifest = JsonNode.Parse(reader.ReadToEnd())!;
            Assert.Single(manifest["smartObjects"]!.AsObject());
            Assert.Equal(2, zip.Entries.Count(entry => entry.FullName.StartsWith("images/")));
        }
        buffer.Position = 0; var loaded = ProjectFile.Read(buffer);
        Assert.Same(loaded.Layers[0].SmartObject, loaded.Layers[1].SmartObject);
        Assert.Same(loaded.Layers[0].Pixels, loaded.Layers[1].Pixels);
        Assert.Equal(2, loaded.Layers[0].SmartObject!.OpenDocument().Layers.Count);
        Assert.NotNull(loaded.Layers[0].SmartObject!.OpenDocument().Layers[1].Shape);
        using var before = parent.Flatten(); using var after = DocumentRenderer.Flatten(loaded);
        Assert.Equal(before.GetPixelSpan().ToArray(), after.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Nested_content_round_trips_and_cannot_contain_itself()
    {
        var inside = EditorSession.NewCanvas(24, 16, SKColors.Green); inside.ConvertToSmartObject();
        var outer = SmartObjectSource.Create(inside.Document);
        var document = new Document(24, 16); var layer = Layer.Raster("Nested", outer.Preview); layer.SmartObject = outer; document.Layers.Add(layer);
        using var buffer = new MemoryStream(); ProjectFile.Write(document, buffer); buffer.Position = 0;
        var loaded = ProjectFile.Read(buffer);
        Assert.True(loaded.Layers[0].SmartObject!.OpenDocument().Layers[0].IsSmartObject);
        var parent = new EditorSession(document);
        Assert.Throws<InvalidOperationException>(() => parent.UpdateSmartObject(outer, document));
    }

    [Fact]
    public void Resizing_parent_does_not_resample_source_or_change_transformed_corners()
    {
        var parent = EditorSession.NewCanvas(40, 30, SKColors.Red); parent.ConvertToSmartObject();
        var layer = parent.ActiveLayer!; layer.Transform = layer.Transform with { X = 3, Y = 4, Rotation = 25, FlipHorizontal = true };
        var source = layer.Pixels; var corners = layer.Transform.Corners(40, 30);
        parent.ResizeImage(80, 90);
        Assert.Same(source, layer.Pixels); Assert.Equal((40, 30), (layer.SmartObject!.Width, layer.SmartObject.Height));
        var resized = layer.Transform.Corners(40, 30);
        for (var i = 0; i < 4; i++) { Assert.Equal(corners[i].X * 2, resized[i].X, 3); Assert.Equal(corners[i].Y * 3, resized[i].Y, 3); }
    }

    [Fact]
    public void Rasterize_retains_preview_and_undo_restores_contents_and_memory_budget_counts_sources_once()
    {
        var parent = EditorSession.NewCanvas(40, 30, SKColors.Red); parent.ConvertToSmartObject();
        parent.DuplicateSelectedLayers();
        Assert.Equal(40L * 30 * 2, parent.Document.RasterPixels()); // source + one shared preview
        var bitmaps = new HashSet<SKBitmap>(); parent.Document.CollectBitmaps(bitmaps); Assert.Equal(2, bitmaps.Count);
        var preview = parent.ActiveLayer!.Pixels;
        parent.RasterizeShape(parent.ActiveLayer); Assert.True(parent.CanEditPixels); Assert.Same(preview, parent.ActiveLayer.Pixels);
        parent.Fill(SKColors.Blue); Assert.Equal(SKColors.Red, preview!.GetPixel(0, 0));
        parent.Undo(); parent.Undo(); Assert.True(parent.ActiveLayer!.IsSmartObject);
    }

    [Fact]
    public void Changing_content_size_preserves_instance_frame_and_resizes_mask_grid()
    {
        var parent = EditorSession.NewCanvas(40, 30, SKColors.Red); parent.ConvertToSmartObject();
        parent.AddMask(parent.ActiveLayer!); parent.EditingMask = false;
        var source = parent.ActiveLayer!.SmartObject!; var frame = parent.ActiveLayer.Transform;
        var inside = new EditorSession(source.OpenDocument()); inside.ResizeCanvas(60, 50, Anchor.TopLeft);
        parent.UpdateSmartObject(source, inside.Document);
        Assert.Equal(frame, parent.ActiveLayer.Transform); Assert.Equal((60, 50), (parent.ActiveLayer.Mask!.Width, parent.ActiveLayer.Mask.Height));
        parent.Undo(); Assert.Equal((40, 30), (parent.ActiveLayer!.Mask!.Width, parent.ActiveLayer.Mask.Height));
    }

    [Fact]
    public void Cyclic_project_is_rejected_without_recursing_forever()
    {
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("""{"format":"org.composa.project","version":7,"width":4,"height":4,"layers":[{"kind":"raster","smartObject":"a"}],"smartObjects":{"a":{"content":{"format":"org.composa.project","version":7,"width":4,"height":4,"layers":[{"kind":"raster","smartObject":"a"}]}}}}""");
        file.Position = 0; Assert.Throws<InvalidDataException>(() => ProjectFile.Read(file));
    }
}
