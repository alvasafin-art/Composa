using Composa.Editing;
using Composa.AI;
using Composa.Filters;
using Composa.IO.Psd;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class WarpEditingTests
{
    [Fact]
    public void Pointer_edit_is_one_undo_step_preserves_source_transform_and_cancel_and_reset_restore_exact_references()
    {
        var session = EditorSession.NewCanvas(240, 180); var layer = session.ActiveLayer!; var original = layer.Pixels!; var transform = layer.Transform;
        var before = original.Bytes; var entries = session.History.Count;
        var edit = session.BeginWarp();
        for (var i = 0; i < 30; i++) edit.MovePoint(5, new(80 + i, 60 - i));
        Assert.Equal(entries, session.History.Count); Assert.True(session.IsInteracting); Assert.False(session.CanEditPixels);
        session.CommitWarp(); var mesh = layer.Warp;
        Assert.Equal(entries + 1, session.History.Count); Assert.Equal(transform, layer.Transform);
        Assert.Same(original, layer.SmartObject!.OpenDocument().ActiveLayer!.Pixels); Assert.Equal(before, original.Bytes);
        session.Undo(); Assert.Null(session.ActiveLayer!.Warp); Assert.Same(original, session.ActiveLayer.Pixels);
        session.Redo(); Assert.Equal(mesh, session.ActiveLayer!.Warp);
        var reedit = session.BeginWarp(); reedit.MovePoint(6, new(150, 20)); session.CancelWarp(); Assert.Equal(mesh, session.ActiveLayer!.Warp);
        session.BeginWarp().Reset(); session.CommitWarp(); Assert.Null(session.ActiveLayer!.Warp); session.Undo(); Assert.Equal(mesh, session.ActiveLayer!.Warp);
        var count = session.History.Count; session.BeginWarp(); session.CommitWarp(); Assert.Equal(count, session.History.Count);
    }

    [Fact]
    public void A_new_command_commits_the_open_mesh_before_snapshotting_and_invalid_geometry_does_not_change_history()
    {
        var session = EditorSession.NewCanvas(240, 180); var layer = session.ActiveLayer!;
        var edit = session.BeginWarp(); edit.MovePoint(5, new(90, 35)); var mesh = layer.Warp;
        session.AddBlankLayer(); Assert.Null(session.WarpEdit); Assert.False(session.IsInteracting);
        session.Undo(); Assert.Equal(mesh, session.ActiveLayer!.Warp); session.Undo(); Assert.Null(session.ActiveLayer!.Warp);
        var history = session.History.Count; var original = session.ActiveLayer.Pixels;
        Assert.Throws<ArgumentException>(() => session.SetWarp(session.ActiveLayer, WarpMesh.Create().WithPoint(0, new(double.NaN, 0))));
        Assert.Equal(history, session.History.Count); Assert.Same(original, session.ActiveLayer.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rasterizing_retains_current_appearance_with_filter_mask_and_outer_masks_and_undo_recovers_the_editable_mesh(bool independent)
    {
        var session = EditorSession.NewCanvas(240, 180); var layer = session.ActiveLayer!; var source = layer.Pixels!;
        source.Erase(SKColors.Red); session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = 20, Seed = 5 });
        layer.SmartFilterMask = Pixels.NewMask(240, 180, 100);
        layer.Mask = Pixels.NewMask(240, 180, 180); layer.MaskLinked = !independent;
        if (independent) layer.PlaceMask(SKMatrix.Identity);
        var mesh = WarpMesh.Create().WithPoint(5, new(.25, .22)); session.SetWarp(layer, mesh);
        using var before = session.Flatten(); session.RasterizeShape(layer); using var after = session.Flatten();
        Assert.Equal(before.Bytes, after.Bytes); Assert.Null(layer.Warp); Assert.False(layer.IsLive); Assert.Null(layer.FilterSource);
        session.Undo(); Assert.Equal(mesh, session.ActiveLayer!.Warp);
        Assert.NotNull(session.ActiveLayer.SmartObject!.OpenDocument().ActiveLayer!.SmartFilterMask);
        using var restored = session.Flatten(); Assert.Equal(before.Bytes, restored.Bytes);
    }

    [Fact]
    public void Photoshop_export_has_the_warped_appearance_and_an_explicit_conversion_report()
    {
        var session = EditorSession.NewCanvas(240, 180); var layer = session.ActiveLayer!;
        layer.Pixels!.Erase(SKColors.Red); layer.Mask = Pixels.NewMask(240, 180);
        using (var canvas = new SKCanvas(layer.Mask)) using (var paint = new SKPaint { Color = SKColors.White }) canvas.DrawRect(40, 30, 90, 75, paint);
        session.SetWarp(layer, WarpLayerTests.Shift(WarpMesh.Create(), .2, -.1));
        Assert.Contains(PsdExport.Conversions(session.Document), note => note.Message.Contains("Warp"));
        using var stream = new MemoryStream(); PsdExport.Write(session.Document, stream); stream.Position = 0;
        var imported = PsdImport.Load(stream.ToArray()); using var expected = session.Flatten(); using var actual = DocumentRenderer.Flatten(imported.ToDocument());
        Assert.Equal(expected.Bytes, actual.Bytes);
    }

    [Fact]
    public void Existing_linked_mask_remains_editable_in_the_retained_source_and_cancelling_does_not_leave_a_conversion_step()
    {
        var session = EditorSession.NewCanvas(240, 180); var layer = session.ActiveLayer!; var pixels = layer.Pixels!; pixels.Erase(SKColors.Red);
        var mask = Pixels.NewMask(240, 180, 128); layer.Mask = mask; var entries = session.History.Count;
        var edit = session.BeginWarp(); Assert.Null(layer.SmartObject); Assert.Same(mask, layer.Mask);
        edit.MovePoint(5, new(70, 40)); session.CancelWarp();
        Assert.Equal(entries, session.History.Count); Assert.Same(pixels, session.ActiveLayer!.Pixels); Assert.Same(mask, session.ActiveLayer.Mask); Assert.Null(session.ActiveLayer.SmartObject);
        edit = session.BeginWarp(); edit.MovePoint(5, new(70, 40)); session.CommitWarp(); Assert.Equal(entries + 1, session.History.Count);
        Assert.Same(mask, session.ActiveLayer!.SmartObject!.OpenDocument().ActiveLayer!.Mask);
        session.Undo(); Assert.Same(mask, session.ActiveLayer!.Mask); Assert.Null(session.ActiveLayer.SmartObject);
    }

    [Fact]
    public void Settling_an_unchanged_Warp_keeps_live_layer_references_for_the_command_that_follows()
    {
        var session = EditorSession.NewCanvas(60, 40); var layer = session.ActiveLayer!; layer.Mask = Pixels.NewMask(60, 40, 128);
        var document = session.Document; var entries = session.History.Count; session.BeginWarp();
        session.Apply("Following command", () => layer.Name = "Changed");
        Assert.Same(document, session.Document); Assert.Same(layer, session.ActiveLayer); Assert.Equal("Changed", session.ActiveLayer!.Name);
        Assert.Equal(entries + 1, session.History.Count); Assert.Null(layer.SmartObject); Assert.Null(session.WarpEdit);
    }

    [Fact]
    public void Mask_added_after_Warp_paints_at_the_pointer_and_keeps_its_appearance_when_rasterized()
    {
        var session = EditorSession.NewCanvas(240, 180); var layer = session.ActiveLayer!; layer.Pixels!.Erase(SKColors.Red);
        session.SetWarp(layer, WarpLayerTests.Shift(WarpMesh.Create(), .25, 0)); session.AddMask(layer);
        Assert.True(layer.MaskLinked); Assert.False(layer.MaskFollowsWarp); Assert.True(session.CanEditPixels);
        var error = session.PaintStroke([new SKPoint(100, 80), new SKPoint(102, 80)], new() { Size = 24, Hardness = 1, Opacity = 1 }, SKColors.Black);
        Assert.Null(error);
        using var before = session.Flatten(); Assert.Equal(0, before.GetPixel(100, 80).Alpha); Assert.Equal(255, before.GetPixel(160, 80).Alpha);
        session.RasterizeShape(layer); using var after = session.Flatten(); Assert.Equal(before.Bytes, after.Bytes);
        Assert.True(layer.MaskLinked); Assert.True(layer.MaskFollowsWarp); Assert.NotNull(layer.Mask);
    }

    [Fact]
    public void Cancelling_or_settling_a_mesh_inside_an_automation_transaction_keeps_its_enclosing_undo_snapshot()
    {
        var session = EditorSession.NewCanvas(120, 90); var layer = session.ActiveLayer!; var name = layer.Name; var entries = session.History.Count;
        session.RunTransaction("Script", s =>
        {
            layer.Name = "Script result";
            var edit = s.BeginWarp(); edit.MovePoint(5, new(35, 15)); s.CancelWarp(); Assert.Null(layer.Warp);
            s.BeginWarp(); s.CommitWarp(); Assert.True(s.IsInteracting);
        });
        Assert.Equal(entries + 1, session.History.Count); Assert.Equal("Script result", session.ActiveLayer!.Name);
        session.Undo(); Assert.Equal(name, session.ActiveLayer!.Name); Assert.Null(session.ActiveLayer.Warp);
    }

    [Fact]
    public void Vector_mask_added_after_Warp_matches_the_drawn_document_path_and_stays_editable_across_save_and_rasterize()
    {
        var session = EditorSession.NewCanvas(240, 180); var layer = session.ActiveLayer!; layer.Pixels!.Erase(SKColors.Red);
        session.SetWarp(layer, WarpLayerTests.Shift(WarpMesh.Create(), .25, 0));
        var path = session.AddPath(new() { Closed = true, Nodes = [BezierNode.Corner(100, 40), BezierNode.Corner(170, 40), BezierNode.Corner(170, 130), BezierNode.Corner(100, 130)] });
        session.SetVisible(path, false); session.ApplyPathAsVectorMask(path, layer);
        Assert.False(layer.VectorMaskFollowsWarp); var geometry = layer.VectorMask;
        using var before = session.Flatten(); Assert.Equal(255, before.GetPixel(110, 80).Alpha); Assert.Equal(0, before.GetPixel(200, 80).Alpha);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0; var loaded = ProjectFile.Read(stream);
        Assert.False(loaded.Find(layer.Id)!.VectorMaskFollowsWarp); using var restored = DocumentRenderer.Flatten(loaded); Assert.Equal(before.Bytes, restored.Bytes);
        session.RasterizeShape(layer); Assert.True(layer.VectorMaskFollowsWarp); Assert.NotNull(layer.VectorMask);
        using var rasterized = session.Flatten(); Assert.Equal(before.Bytes, rasterized.Bytes);
        session.Undo(); Assert.Equal(geometry, session.Document.Find(layer.Id)!.VectorMask); Assert.False(session.Document.Find(layer.Id)!.VectorMaskFollowsWarp);
    }

    [Fact]
    public void Scene_AI_receives_the_actual_deformed_silhouette_even_when_its_retained_source_is_opaque()
    {
        var session = EditorSession.NewCanvas(300, 240, SKColors.Gray); var image = Pixels.NewColor(100, 80); image.Erase(SKColors.Red);
        var layer = session.AddImageLayer("Object", image, new(120, 120), fit: false); var original = image.Bytes;
        session.SetWarp(layer, WarpLayerTests.Shift(WarpMesh.Create(), .3, 0));
        using var input = SceneEditInput.Prepare(session.Document.Clone(), new() { Task = AiTaskKind.MatchToScene });
        Assert.True(input.IsCutout); Assert.Equal(0, input.SubjectMask.GetPixel(85, 100).Alpha); Assert.Equal(255, input.SubjectMask.GetPixel(110, 100).Alpha);
        Assert.Equal(SKColors.Gray, input.Background.GetPixel(110, 100)); Assert.Equal(original, image.Bytes);
    }
}
