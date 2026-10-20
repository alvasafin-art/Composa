using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class LiquifyWorkspaceTests
{
    private static SKBitmap Source(ColorDepth depth, int width = 240, int height = 160)
    {
        var source = Pixels.NewWorkingColor(width, height, depth, RgbProfile.DisplayP3); var p = new PixelBuffer(source);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) p.Write(x, y, new(.1f + x / 65535f, x % 2 == 0 ? .7f : .15f, y / (float)height * .5f, .8f));
        return source;
    }
    private static LiquifyField Move(LiquifyField field) => field.Stroke(new(80.5f, 80.5f), new(100.5f, 80.5f), LiquifyTool.Push, 120, 1, 1);

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Push_samples_the_immutable_original_and_preserves_untouched_pixels_profile_and_alpha(ColorDepth depth)
    {
        using var source = Source(depth); var bytes = source.Bytes;
        var field = Move(new(240, 160)); using var result = field.Render(source);
        Assert.True(field.HasDeformation); Assert.Equal(new SKPoint(-20, 0), field.DisplacementAt(100, 80));
        var input = new PixelBuffer(source); var actual = new PixelBuffer(result);
        Assert.Equal(input.Read(80, 80), actual.Read(100, 80)); Assert.Equal(input.Read(10, 10), actual.Read(10, 10));
        Assert.Equal(Pixels.DepthOf(source), Pixels.DepthOf(result)); Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(result)); Assert.Equal(bytes, source.Bytes);
        using var again = field.Render(source); Assert.Equal(result.Bytes, again.Bytes);
        using var restored = field.RestoreAll().Render(source); Assert.Equal(bytes, restored.Bytes);
    }

    [Fact]
    public void Reconstruct_changes_coordinates_not_a_crossfade_of_old_and_displaced_colors()
    {
        using var source = Source(ColorDepth.Sixteen); var field = Move(new(240, 160));
        var originalField = field;
        field = field.Stroke(new(100.5f, 80.5f), new(100.5f, 80.5f), LiquifyTool.Reconstruct, 30, .45, 1);
        Assert.Equal(new SKPoint(-11, 0), field.DisplacementAt(100, 80)); Assert.Equal(new SKPoint(-20, 0), originalField.DisplacementAt(100, 80));
        using var rendered = field.Render(source);
        // Both old endpoints are bright stripes; the restored coordinate is a dark stripe. A color crossfade fails.
        Assert.Equal(new PixelBuffer(source).Read(89, 80), new PixelBuffer(rendered).Read(100, 80));
        field = field.Stroke(new(100.5f, 80.5f), new(100.5f, 80.5f), LiquifyTool.Reconstruct, 30, 1, 1);
        Assert.Equal(default, field.DisplacementAt(100, 80)); using var reconstructed = field.Render(source);
        Assert.Equal(new PixelBuffer(source).Read(100, 80), new PixelBuffer(reconstructed).Read(100, 80));
    }

    [Fact]
    public void Freeze_preserves_current_shape_for_push_and_reconstruction_and_thaw_releases_it()
    {
        var field = Move(new(240, 160));
        var frozen = field.Stroke(new(100.5f, 80.5f), new(100.5f, 80.5f), LiquifyTool.Freeze, 30, 1, 1);
        Assert.Equal(255, frozen.FrozenAt(100, 80)); Assert.Equal(0, field.FrozenAt(100, 80));
        var reconstructed = frozen.Stroke(new(100.5f, 80.5f), new(100.5f, 80.5f), LiquifyTool.Reconstruct, 50, 1, 1);
        Assert.Equal(frozen.DisplacementAt(100, 80), reconstructed.DisplacementAt(100, 80));
        var pushed = reconstructed.Stroke(new(90.5f, 80.5f), new(110.5f, 80.5f), LiquifyTool.Push, 80, 1, 1);
        Assert.Equal(frozen.DisplacementAt(100, 80), pushed.DisplacementAt(100, 80));
        var thawed = pushed.Stroke(new(100.5f, 80.5f), new(100.5f, 80.5f), LiquifyTool.Thaw, 30, 1, 1);
        Assert.Equal(0, thawed.FrozenAt(100, 80));
        Assert.Equal(default, thawed.Stroke(new(100.5f, 80.5f), new(100.5f, 80.5f), LiquifyTool.Reconstruct, 30, 1, 1).DisplacementAt(100, 80));
        Assert.False(frozen.RestoreAll().HasDeformation); Assert.Equal(255, frozen.RestoreAll().FrozenAt(100, 80)); Assert.Equal(0, frozen.ThawAll().FrozenAt(100, 80));
    }

    [Fact]
    public void Selection_and_partial_freeze_constrain_deformation_and_overlay_in_source_coordinates()
    {
        using var selection = Pixels.NewMask(240, 160); using (var canvas = new SKCanvas(selection))
        using (var paint = new SKPaint { Color = SKColors.White }) canvas.DrawRect(90, 0, 150, 160, paint);
        var field = new LiquifyField(240, 160).Stroke(new(80, 80), new(100, 80), LiquifyTool.Push, 100, 1, 1, selection);
        Assert.Equal(default, field.DisplacementAt(85, 80)); Assert.True(field.DisplacementAt(100, 80).X < -10);
        var frozen = new LiquifyField(240, 160).Stroke(new(100, 80), new(100, 80), LiquifyTool.Freeze, 80, .5, 1);
        var partial = frozen.Stroke(new(80, 80), new(100, 80), LiquifyTool.Push, 100, 1, 1, selection);
        Assert.InRange(Math.Abs(partial.DisplacementAt(100, 80).X), 5, 15);
        using var overlay = frozen.FreezePreview(120, 80, selection);
        Assert.Equal(255, overlay.GetPixel(20, 40).Alpha); Assert.InRange(overlay.GetPixel(50, 40).Alpha, (byte)127, (byte)129); Assert.Equal(0, overlay.GetPixel(100, 40).Alpha);
    }

    [Fact]
    public void Sparse_history_shares_untouched_tiles_and_has_an_actual_retained_memory_budget()
    {
        var identity = new LiquifyField(6000, 4000); Assert.Equal(0, identity.AllocatedBytes);
        var first = identity.Stroke(new(64, 64), new(64, 64), LiquifyTool.Freeze, 20, 1, 1);
        var next = first.Stroke(new(320, 320), new(320, 320), LiquifyTool.Freeze, 20, 1, 1);
        var history = new LiquifyHistory(identity); history.Push(first); history.Push(next);
        Assert.Equal(next.AllocatedBytes, history.RetainedBytes); Assert.InRange(next.AllocatedBytes, 1, 128 * 128 * 2);
        history.Undo(); Assert.Same(first, history.Current); history.Redo(); Assert.Same(next, history.Current);
        history.MemoryBudget = 1; history.Push(next.ThawAll()); Assert.False(history.CanUndo); Assert.False(history.CanRedo); Assert.Equal(0, history.RetainedBytes);
        Assert.Equal(255, next.FrozenAt(64, 64)); Assert.Equal(0, identity.FrozenAt(64, 64));
    }

    [Fact]
    public void Preview_and_full_render_can_share_the_field_while_a_new_stroke_is_created_and_cancellation_is_honored()
    {
        using var source = Source(ColorDepth.Sixteen, 600, 400); var field = Move(new(600, 400)); var before = source.Bytes;
        using var expected = field.Render(source); var expectedBytes = expected.Bytes;
        Parallel.For(0, 8, i =>
        {
            if (i % 2 == 0) { using var full = field.Render(source); Assert.Equal(expectedBytes, full.Bytes); }
            else { using var view = field.Render(source, 120); Assert.Equal(120, view.Width); Assert.Equal(80, view.Height); Assert.Equal(SKColorType.RgbaF32, view.ColorType); Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(view)); }
        });
        var changed = field.Stroke(new(100, 100), new(110, 100), LiquifyTool.Push, 80); Assert.NotSame(field, changed); Assert.Equal(before, source.Bytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => field.Render(source, token: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => field.Stroke(new(float.NaN, 2), new(3, 4), LiquifyTool.Push, 30));
        using var wrongMask = Pixels.NewMask(20, 20); Assert.Throws<ArgumentException>(() => field.Stroke(new(20, 20), new(21, 21), LiquifyTool.Push, 30, selection: wrongMask));
    }

    [Fact]
    public void Session_adopts_once_preserves_masks_placement_and_undo_and_refuses_stale_or_identity_results()
    {
        var document = new Document(400, 300) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 }; var source = Source(document.Depth);
        var layer = Layer.Raster("Photo", source, 30, 40); layer.Mask = Pixels.NewMask(80, 60, 255); layer.MaskLinked = false; layer.PlaceMask(SKMatrix.CreateTranslation(50, 60));
        document.InsertAboveActive(layer); var session = new EditorSession(document); var placement = layer.Transform; var maskMatrix = layer.MaskMatrix;
        var field = Move(new(240, 160)); var result = field.Render(source); Assert.True(session.ApplyLiquify(source, field, result));
        Assert.Equal("Liquify", session.History.UndoName); Assert.Same(result, layer.Pixels); Assert.Equal(placement, layer.Transform); Assert.Equal(maskMatrix, layer.MaskMatrix);
        using var stale = field.Render(source); Assert.False(session.ApplyLiquify(source, field, stale));
        session.Undo(); Assert.Same(source, session.ActiveLayer!.Pixels); session.Redo(); Assert.Same(result, session.ActiveLayer!.Pixels);
        using var identity = new LiquifyField(240, 160).Render(result); Assert.False(session.ApplyLiquify(result, new(240, 160), identity));
        using var wrongFormat = Pixels.NewMask(result.Width, result.Height, 255);
        Assert.Throws<ArgumentException>(() => session.ApplyLiquify(result, field, wrongFormat)); Assert.Same(result, session.ActiveLayer.Pixels);
        session.ActiveLayer.Locks = LayerLocks.Transparency; Assert.False(session.CanLiquify);
    }

    [Fact]
    public void Editing_retained_RGB8_source_in_RGB16_uses_document_precision_without_replacing_original_history()
    {
        var document = new Document(240, 160) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 }; var source = Source(ColorDepth.Eight);
        var layer = Layer.Raster("Retained", source); document.InsertAboveActive(layer); var session = new EditorSession(document);
        using var working = Pixels.ConvertWorking(source, document.Depth, document.Profile); var field = Move(new(240, 160)); var result = field.Render(working);
        Assert.True(session.ApplyLiquify(source, field, result)); Assert.Equal(SKColorType.RgbaF32, layer.Pixels!.ColorType);
        session.Undo(); Assert.Same(source, session.ActiveLayer!.Pixels);
    }
}
