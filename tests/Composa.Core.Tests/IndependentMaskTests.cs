using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class IndependentMaskTests
{
    private static (EditorSession Session, Layer Layer) Example(ColorDepth depth = ColorDepth.Eight)
    {
        var document = new Document(80, 60) { Depth = depth };
        var image = Pixels.NewColor(40, 30, depth); image.Erase(SKColors.Red);
        var layer = Layer.Raster("Product", image, 10, 10);
        layer.Mask = SelectionMask.FromRect(40, 30, new(20, 0, 40, 30));
        document.InsertAboveActive(layer);
        return (new EditorSession(document), layer);
    }

    private static void SameMatrix(SKMatrix expected, SKMatrix actual)
    {
        foreach (var point in new[] { new SKPoint(0, 0), new(5, 7), new(40, 30), new(20, 15) })
        {
            var a = expected.MapPoint(point); var b = actual.MapPoint(point);
            Assert.InRange(Math.Abs(a.X - b.X), 0, .0002); Assert.InRange(Math.Abs(a.Y - b.Y), 0, .0002);
        }
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Unlink_keeps_coverage_then_layer_moves_under_the_mask_and_relink_keeps_its_position(ColorDepth depth)
    {
        var (session, layer) = Example(depth); var raw = layer.Mask!; var bytes = raw.Bytes;
        using var before = session.Flatten(); var matrix = layer.MaskMatrix;
        session.SetMaskLinked(layer, false);
        using var unlinked = session.Flatten(); Assert.Equal(before.Bytes, unlinked.Bytes); SameMatrix(matrix, layer.MaskMatrix);
        session.Nudge(5, 0); SameMatrix(matrix, layer.MaskMatrix);
        using var moved = session.Flatten(); Assert.Equal(0, moved.GetPixel(28, 20).Alpha); Assert.Equal(255, moved.GetPixel(32, 20).Alpha);
        session.SetMaskLinked(layer, true); SameMatrix(matrix, layer.MaskMatrix);
        session.Nudge(7, 3); SameMatrix(matrix.PostConcat(SKMatrix.CreateTranslation(7, 3)), layer.MaskMatrix);
        Assert.Same(raw, layer.Mask); Assert.Equal(bytes, raw.Bytes);
        session.Undo(); session.Undo(); Assert.False(session.ActiveLayer!.MaskLinked); SameMatrix(matrix, session.ActiveLayer.MaskMatrix);
    }

    [Fact]
    public void Transforming_an_unlinked_mask_changes_only_its_frame_and_cancel_restores_it()
    {
        var (session, layer) = Example(); session.SetMaskLinked(layer, false); session.EditingMask = true;
        var image = layer.Pixels; var raw = layer.Mask; var frame = layer.Transform; var matrix = layer.MaskMatrix;
        session.Nudge(-8, 2); Assert.Equal(frame, layer.Transform); Assert.Same(image, layer.Pixels); Assert.Same(raw, layer.Mask);
        SameMatrix(matrix.PostConcat(SKMatrix.CreateTranslation(-8, 2)), layer.MaskMatrix);
        var edit = session.BeginTransform()!; edit.DistortCorner(0, new SKPoint(4, 8)); session.CancelTransform();
        layer = session.ActiveLayer!; SameMatrix(matrix.PostConcat(SKMatrix.CreateTranslation(-8, 2)), layer.MaskMatrix);
        session.SetMaskTransform(layer, session.EditingControlTransform(layer) with { Rotation = 27 });
        Assert.Equal(27, session.EditingControlTransform(layer).Rotation);
        Assert.Same(image, layer.Pixels); Assert.Same(raw, layer.Mask);
    }

    [Fact]
    public void Paint_and_selection_use_the_mask_grid_without_growing_or_moving_the_image()
    {
        var (session, layer) = Example(); session.SetMaskLinked(layer, false); session.EditingMask = true;
        var image = layer.Pixels; var frame = layer.Transform;
        session.SelectRect(new(2, 2, 6, 6)); session.Fill(SKColors.White);
        Assert.Equal(frame, layer.Transform); Assert.Same(image, layer.Pixels);
        using var selected = SelectionMask.FromLayer(session.Document, layer, fromMask: true);
        Assert.Equal(255, selected!.GetPixel(3, 3).Alpha); Assert.Equal(0, selected.GetPixel(7, 3).Alpha);
        Assert.Equal(255, selected.GetPixel(35, 20).Alpha); Assert.Equal(0, selected.GetPixel(20, 20).Alpha);
        session.Undo(); Assert.Equal((40, 30), (session.ActiveLayer!.Mask!.Width, session.ActiveLayer.Mask.Height));
    }

    [Fact]
    public void Applying_the_mask_matches_the_render_and_undo_keeps_the_source()
    {
        var (session, layer) = Example(ColorDepth.Sixteen); session.SetMaskLinked(layer, false);
        session.SetMaskTransform(layer, layer.MaskTransform! with { X = 4, Rotation = 13 });
        var mask = layer.Mask; var image = layer.Pixels;
        using var before = session.Flatten(); session.ApplyMask(layer); using var after = session.Flatten();
        Assert.Null(layer.Mask);
        var a = new PixelBuffer(before); var b = new PixelBuffer(after);
        for (var y = 0; y < before.Height; y++) for (var x = 0; x < before.Width; x++)
            Assert.InRange(Math.Abs(a.Read(x, y).A - b.Read(x, y).A), 0, .00001);
        session.Undo(); Assert.Same(mask, session.ActiveLayer!.Mask); Assert.Same(image, session.ActiveLayer.Pixels);
    }

    [Fact]
    public void Canvas_operations_transform_independent_masks_with_the_document()
    {
        var (session, layer) = Example(); session.SetMaskLinked(layer, false); var raw = layer.Mask;
        var matrix = layer.MaskMatrix; session.Crop(new(3, 4, 73, 54));
        matrix = matrix.PostConcat(SKMatrix.CreateTranslation(-3, -4)); SameMatrix(matrix, layer.MaskMatrix);
        session.FlipCanvas(true); matrix = matrix.PostConcat(SKMatrix.CreateScale(-1, 1, 35, 0)); SameMatrix(matrix, layer.MaskMatrix);
        session.RotateCanvas(true); matrix = matrix.PostConcat(new SKMatrix(0, -1, 50, 1, 0, 0, 0, 0, 1)); SameMatrix(matrix, layer.MaskMatrix);
        session.ResizeImage(100, 210); matrix = matrix.PostConcat(SKMatrix.CreateScale(2, 3)); SameMatrix(matrix, layer.MaskMatrix);
        Assert.Same(raw, layer.Mask);
    }

    [Fact]
    public void Smart_filter_padding_and_new_smart_object_pixels_do_not_shift_the_mask()
    {
        var (session, layer) = Example(); session.SetMaskLinked(layer, false);
        var matrix = layer.MaskMatrix; var raw = layer.Mask;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 4, ClampEdges = false });
        SameMatrix(matrix, layer.MaskMatrix); Assert.Same(raw, layer.Mask);
        session.Undo(); layer = session.ActiveLayer!;
        session.ConvertToSmartObject(); layer = session.ActiveLayer!; matrix = layer.MaskMatrix; raw = layer.Mask;
        var content = layer.SmartObject!.OpenDocument(); var editor = new EditorSession(content); editor.ResizeCanvas(60, 50, Anchor.TopLeft);
        session.UpdateSmartObject(layer.SmartObject!, editor.Document);
        SameMatrix(matrix, layer.MaskMatrix); Assert.Same(raw, layer.Mask);
    }

    [Fact]
    public void Project_keeps_link_state_independent_geometry_and_raw_coverage()
    {
        var (session, layer) = Example(); session.SetMaskLinked(layer, false);
        session.SetMaskTransform(layer, layer.MaskTransform! with { X = -4, Rotation = 19, Distort = [0, 0, 3, 1, -2, 0, 0, 2] });
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var restored = ProjectFile.Read(stream); var actual = restored.ActiveLayer!;
        Assert.False(actual.MaskLinked); SameMatrix(layer.MaskMatrix, actual.MaskMatrix); Assert.Equal(layer.Mask!.Bytes, actual.Mask!.Bytes);
        using var a = DocumentRenderer.Flatten(session.Document); using var b = DocumentRenderer.Flatten(restored); Assert.Equal(a.Bytes, b.Bytes);
    }

    [Fact]
    public void Effects_follow_effective_mask_and_its_cache_changes_when_the_image_moves_under_it()
    {
        var (session, layer) = Example(); session.SetMaskLinked(layer, false);
        layer.Effects = new() { Shadow = new() { Distance = 5, Blur = 0, Opacity = 1 } };
        using var first = session.Flatten();
        Assert.Equal(0, first.GetPixel(27, 42).Alpha); Assert.Equal(255, first.GetPixel(34, 42).Alpha);
        session.Nudge(10, 0); using var second = session.Flatten();
        Assert.Equal(0, second.GetPixel(27, 42).Alpha); Assert.Equal(255, second.GetPixel(34, 42).Alpha);
        Assert.Equal(0, second.GetPixel(55, 42).Alpha); // The old mask, rather than a stale layer-local cache, limits the shadow.
        session.SetMaskTransform(layer, layer.MaskTransform! with { X = 20 }); using var third = session.Flatten();
        Assert.Equal(0, third.GetPixel(34, 42).Alpha); Assert.Equal(255, third.GetPixel(46, 42).Alpha);
    }

    [Fact]
    public void Rotate_flip_and_align_commands_address_the_selected_unlinked_mask()
    {
        var (session, layer) = Example(); session.SetMaskLinked(layer, false); session.EditingMask = true;
        var frame = layer.Transform; var raw = layer.Mask;
        session.RotateLayers(90); Assert.InRange(session.EditingControlTransform(layer).Rotation, 89.999, 90.001);
        session.FlipLayers(true); Assert.Equal(frame, layer.Transform);
        session.AlignObjects(ObjectAlignment.Left, AlignmentReference.Canvas);
        var box = session.EditingControlTransform(layer).Bounds(raw!.Width, raw.Height); Assert.InRange(Math.Abs(box.Left), 0, .001);
        Assert.Equal(frame, layer.Transform); Assert.Same(raw, layer.Mask);
    }

    [Fact]
    public void Invert_updates_the_outside_coverage_once_per_preview_and_cancel_restores_it()
    {
        var (session, layer) = Example(); session.SetMaskLinked(layer, false); session.EditingMask = true;
        var raw = layer.Mask; session.BeginPreview("Invert mask");
        session.PreviewAdjustment(new InvertAdjustment()); session.PreviewAdjustment(new InvertAdjustment());
        Assert.Equal(255, layer.MaskDefault); Assert.Equal(255, layer.Mask!.GetPixel(5, 5).Alpha);
        Assert.Equal(0, layer.Mask.GetPixel(25, 5).Alpha);
        session.CancelPreview(); layer = session.ActiveLayer!; Assert.Equal(0, layer.MaskDefault); Assert.Same(raw, layer.Mask);
        session.Fill(SKColors.White); Assert.Equal(255, layer.MaskDefault);
        session.Undo(); Assert.Equal(0, session.ActiveLayer!.MaskDefault);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PSD_keeps_mask_extent_beyond_the_object_and_default_value(bool group, bool linked)
    {
        var (session, layer) = Example();
        if (group)
        {
            var folder = Layer.Group("Folder"); folder.Children.Add(layer); session.Document.Layers.Clear(); session.Document.InsertAboveActive(folder); layer = folder;
            layer.Mask = Pixels.NewMask(110, 90, 255);
        }
        session.SetMaskLinked(layer, false); layer.MaskDefault = 255;
        session.SetMaskTransform(layer, LayerTransform.Identity(layer.Mask!.Width, layer.Mask.Height) with { X = -5, Y = -3 });
        if (linked) session.SetMaskLinked(layer, true);
        using var stream = new MemoryStream(); PsdExport.Write(session.Document, stream);
        var restored = PsdImport.Load(stream.ToArray()).ToDocument(); var actual = restored.Layers[0];
        Assert.Equal(linked, actual.MaskLinked); Assert.Equal(layer.MaskDefault, actual.MaskDefault);
        Assert.Equal(layer.Mask.Bytes, actual.Mask!.Bytes); SameMatrix(layer.MaskMatrix, actual.MaskMatrix);
        using var a = DocumentRenderer.Flatten(session.Document); using var b = DocumentRenderer.Flatten(restored); Assert.Equal(a.Bytes, b.Bytes);
    }
}
