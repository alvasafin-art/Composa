using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class ProfessionalEditingTests
{
    private static VectorPath Square() => new() { Closed = true, Nodes = [BezierNode.Corner(.25, .25), BezierNode.Corner(.75, .25), BezierNode.Corner(.75, .75), BezierNode.Corner(.25, .75)] };
    [Fact]
    public void Layer_locks_block_pixels_and_position_and_inherit_from_folders()
    {
        var session = EditorSession.NewCanvas(40, 30, SKColors.White); var layer = session.ActiveLayer!; var original = layer.Pixels;
        session.SetLayerLocks(layer, LayerLocks.Pixels | LayerLocks.Position);
        session.Fill(SKColors.Red); Assert.Same(original, layer.Pixels);
        Assert.False(session.BeginStroke(new SKPoint(10, 10), out var problem)); Assert.Contains("Unlock", problem!);
        var transform = layer.Transform; session.Nudge(10, 5); Assert.Equal(transform, layer.Transform);
        session.RotateLayers(90); session.FlipLayers(true); Assert.Equal(transform, layer.Transform);
        var folder = Layer.Group("Locked folder"); folder.Locks = LayerLocks.All; folder.Children.Add(layer);
        session.Document.Layers.Clear(); session.Document.Layers.Add(folder);
        Assert.True(session.IsLocked(layer)); Assert.True(session.PositionLocked(layer));
    }
    [Fact]
    public void Transparency_lock_preserves_alpha_and_does_not_paint_empty_pixels()
    {
        var session = new EditorSession(new Document(32, 32)); var pixels = Pixels.NewColor(32, 32);
        using (var canvas = new SKCanvas(pixels)) { using var paint = new SKPaint { Color = new SKColor(255, 0, 0, 80) }; canvas.DrawRect(8, 8, 16, 16, paint); }
        var layer = Layer.Raster("Paint", pixels); session.Document.InsertAboveActive(layer);
        session.SetLayerLocks(layer, LayerLocks.Transparency); session.Foreground = SKColors.Blue; session.Brush = new BrushSettings { Size = 32, Hardness = 1 };
        session.BeginStroke(new SKPoint(16, 16), out _); session.EndStroke();
        Assert.Equal(80, layer.Pixels!.GetPixel(16, 16).Alpha); Assert.Equal(0, layer.Pixels.GetPixel(3, 3).Alpha);
        Assert.Equal(80, pixels.GetPixel(16, 16).Alpha); Assert.Equal(SKColors.Red.Red, pixels.GetPixel(16, 16).Red);
        session.Undo(); Assert.Same(pixels, session.ActiveLayer!.Pixels);
    }
    [Fact]
    public void Saved_selections_are_undoable_and_follow_crop_resize_and_rotation()
    {
        var session = EditorSession.NewCanvas(40, 30, SKColors.White); session.SelectRect(new SKRect(10, 10, 20, 20));
        session.SaveSelection("Object"); var original = session.Document.AlphaChannels["Object"]; session.Deselect();
        session.Crop(new SKRectI(5, 5, 35, 25)); var channel = session.Document.AlphaChannels["Object"];
        Assert.Equal(30, channel.Width); Assert.Equal(255, channel.GetPixel(8, 8).Alpha); Assert.Equal(40, original.Width);
        session.LoadSelection("object"); Assert.Equal(255, session.Selection!.GetPixel(8, 8).Alpha);
        session.DeleteSelectionChannel("Object"); Assert.Empty(session.Document.AlphaChannels);
        session.Undo(); Assert.Single(session.Document.AlphaChannels);
    }
    [Fact]
    public void Independent_gradient_color_and_opacity_stops_interpolate_and_compare_by_value()
    {
        var ramp = new GradientRamp { Colors = [new(0, (uint)SKColors.Red), new(.5, (uint)SKColors.Green), new(1, (uint)SKColors.Blue)], Opacities = [new(0, 1), new(.5, .2), new(1, 0)] };
        Assert.Equal(51, ramp.Sample(.5).Alpha); Assert.Equal(SKColors.Green.Green, ramp.Sample(.5).Green);
        Assert.Equal(ramp, ramp with { Colors = ramp.Colors.ToArray(), Opacities = ramp.Opacities.ToArray() });
        var colors = ramp.ShaderColors(true); Assert.Equal(ramp.Sample(1), colors[0]); Assert.Equal(ramp.Sample(0), colors[^1]);
    }
    [Fact]
    public void Smart_filter_changes_disable_remove_and_undo_restore_source_and_placement()
    {
        var session = EditorSession.NewCanvas(40, 30, SKColors.Transparent); var layer = session.ActiveLayer!; var source = layer.Pixels!;
        var transform = layer.Transform;
        session.AddSmartFilter(layer, new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 2 });
        Assert.Same(source, layer.FilterSource); Assert.True(layer.Pixels!.Width > source.Width); Assert.False(session.CanEditPixels);
        var filter = layer.SmartFilters.Single(); session.Nudge(6, 3); var moved = layer.Transform;
        session.ChangeSmartFilter(layer, filter.Id, enabled: false);
        Assert.Same(source, layer.Pixels); Assert.Equal(transform.X + 6, layer.Transform.X); Assert.Equal(transform.Y + 3, layer.Transform.Y);
        session.Undo(); Assert.Equal(moved, session.ActiveLayer!.Transform); Assert.True(session.ActiveLayer.SmartFilters.Single().Enabled);
        session.ChangeSmartFilter(session.ActiveLayer, filter.Id, remove: true); Assert.Same(source, session.ActiveLayer!.Pixels);
    }
    [Fact]
    public void Vector_mask_and_raster_mask_multiply_once_with_and_without_effects()
    {
        var session = EditorSession.NewCanvas(40, 40, SKColors.Red); var layer = session.ActiveLayer!;
        layer.VectorMask = Square(); layer.Mask = Pixels.NewMask(40, 40, 128);
        using var before = session.Flatten(); Assert.InRange(before.GetPixel(20, 20).Alpha, 127, 129); Assert.Equal(0, before.GetPixel(2, 2).Alpha);
        session.SetEffects(layer, new LayerEffects { ColorOverlay = new() { Color = (uint)SKColors.Blue } });
        using var after = session.Flatten(); Assert.InRange(after.GetPixel(20, 20).Alpha, 127, 129); Assert.Equal(0, after.GetPixel(2, 2).Alpha);
    }
    [Fact]
    public void Closed_bezier_path_stays_editable_and_can_be_a_mask_on_a_transformed_layer()
    {
        var session = EditorSession.NewCanvas(100, 100, SKColors.Red); var target = session.ActiveLayer!;
        var path = session.AddPath(new VectorPath { Closed = true, Nodes = [BezierNode.Corner(20, 20), new(80, 20, 50, 5, 80, 50), BezierNode.Corner(80, 80), BezierNode.Corner(20, 80)] });
        Assert.NotNull(path.Shape!.Path); Assert.True(path.Pixels!.Width < 100);
        session.ApplyPathAsVectorMask(path, target); Assert.NotNull(target.VectorMask);
        path.Visible = false; using var image = session.Flatten(); Assert.Equal(255, image.GetPixel(50, 50).Alpha); Assert.Equal(0, image.GetPixel(5, 5).Alpha);
        session.Undo(); Assert.Null(session.Document.Find(target.Id)!.VectorMask);
    }
    [Fact]
    public void Patch_and_healing_preserve_original_and_match_constant_background_color()
    {
        using var source = Pixels.NewColor(60, 40); source.Erase(new SKColor(110, 140, 170));
        using (var canvas = new SKCanvas(source)) { using var paint = new SKPaint { Color = SKColors.Black }; canvas.DrawRect(12, 12, 6, 6, paint); }
        using var mask = SelectionMask.FromRect(60, 40, new SKRect(8, 8, 22, 22));
        using var healed = PatchBlend.Blend(source, source, mask, new SKPointI(28, 0));
        Assert.Equal(new SKColor(110, 140, 170), healed.GetPixel(15, 15)); Assert.Equal(SKColors.Black, source.GetPixel(15, 15));
    }
    [Fact]
    public void Refinement_can_contract_and_output_a_cutout_without_changing_source_or_selection()
    {
        using var source = Pixels.NewColor(40, 40); source.Erase(SKColors.Blue);
        using var mask = SelectionMask.FromRect(40, 40, new SKRect(8, 8, 32, 32));
        using var refined = MaskRefinement.Refine(mask, source, new MaskRefinementSettings(Shift: -3, EdgeAware: false));
        Assert.Equal(0, refined.GetPixel(9, 9).Alpha); Assert.Equal(255, mask.GetPixel(9, 9).Alpha);
        using var cutout = MaskRefinement.Cutout(source, refined, true); Assert.Equal(0, cutout.GetPixel(2, 2).Alpha); Assert.Equal(255, source.GetPixel(2, 2).Alpha);
    }
    [Fact]
    public void Native_project_round_trips_filters_paths_channels_locks_and_gradient_stops()
    {
        var session = EditorSession.NewCanvas(48, 48, SKColors.Red); var layer = session.ActiveLayer!;
        session.SelectRect(new SKRect(5, 5, 25, 25)); session.SaveSelection("Object");
        session.AddSmartFilter(layer, new FilterSettings { Kind = FilterKind.AddNoise, Amount = 4, Seed = 17 });
        layer.VectorMask = Square(); layer.Locks = LayerLocks.Position;
        layer.Effects = new() { GradientOverlay = new() { Ramp = GradientRamp.Presets["Sunset"] } };
        var path = Path.Combine(AppContext.BaseDirectory, "professional-" + Guid.NewGuid().ToString("N") + ".cmps");
        try
        {
            ProjectFile.Save(session.Document, path); var loaded = ProjectFile.Load(path); var restored = loaded.ActiveLayer!;
            Assert.NotNull(restored.FilterSource); Assert.Single(restored.SmartFilters); Assert.Equal(layer.Locks, restored.Locks);
            Assert.Equal(layer.VectorMask, restored.VectorMask); Assert.Single(loaded.AlphaChannels); Assert.Equal(layer.Effects, restored.Effects);
            using var original = session.Flatten(); using var rendered = DocumentRenderer.Flatten(loaded);
            Assert.Equal(original.GetPixelSpan().ToArray(), rendered.GetPixelSpan().ToArray());
            Assert.Contains(PsdExport.Conversions(loaded), note => note.Message.Contains("Smart filters"));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Updating_filtered_smart_object_rebuilds_from_new_contents_and_undo_restores_old_source()
    {
        var session = EditorSession.NewCanvas(30, 20, SKColors.Red); var layer = session.ConvertToSmartObject();
        session.AddSmartFilter(layer, new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 2 });
        var expected = layer.SmartObject!; var oldPixels = layer.Pixels; var oldTransform = layer.Transform;
        var content = new EditorSession(expected.OpenDocument()); content.Fill(SKColors.Blue);
        session.UpdateSmartObject(expected, content.Document);
        Assert.Same(layer.SmartObject!.Preview, layer.FilterSource); Assert.Equal(oldTransform, layer.Transform);
        Assert.True(layer.Pixels!.GetPixel(15 + layer.FilterPaddingX, 10 + layer.FilterPaddingY).Blue > 240);
        session.ChangeSmartFilter(layer, layer.SmartFilters.Single().Id, enabled: false);
        Assert.Equal(SKColors.Blue, layer.Pixels!.GetPixel(15, 10));
        session.Undo(); session.Undo(); Assert.Same(oldPixels, session.ActiveLayer!.Pixels); Assert.Same(expected, session.ActiveLayer.SmartObject);
    }
    [Fact]
    public void Converting_filtered_masked_layer_does_not_double_apply_its_filters_or_vector_mask()
    {
        var session = EditorSession.NewCanvas(40, 40, SKColors.Red); var layer = session.ActiveLayer!; layer.VectorMask = Square();
        session.AddSmartFilter(layer, new FilterSettings { Kind = FilterKind.AddNoise, Amount = 8, Seed = 7 });
        using var before = session.Flatten(); var smart = session.ConvertToSmartObject(); using var after = session.Flatten();
        Assert.Null(smart.FilterSource); Assert.Empty(smart.SmartFilters); Assert.NotNull(smart.VectorMask);
        var inner = smart.SmartObject!.OpenDocument().Layers.Single(); Assert.Null(inner.VectorMask); Assert.Single(inner.SmartFilters);
        Assert.Equal(before.GetPixelSpan().ToArray(), after.GetPixelSpan().ToArray());
    }
    [Fact]
    public void Resizing_filtered_layer_keeps_source_pixels_and_disable_preserves_scaled_position()
    {
        var session = EditorSession.NewCanvas(40, 30, SKColors.Red); var layer = session.ActiveLayer!; var source = layer.Pixels;
        session.AddSmartFilter(layer, new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 2 }); session.ResizeImage(80, 60);
        Assert.Same(source, layer.FilterSource); session.ChangeSmartFilter(layer, layer.SmartFilters.Single().Id, enabled: false);
        Assert.Same(source, layer.Pixels); Assert.Equal(80, layer.Transform.Width); Assert.Equal(60, layer.Transform.Height); Assert.Equal(0, layer.Transform.X, 5);
    }
    [Fact]
    public void Growing_smart_filter_remaps_vector_mask_to_the_same_document_coordinates()
    {
        var session = EditorSession.NewCanvas(40, 40, SKColors.Red); var layer = session.ActiveLayer!; layer.VectorMask = Square();
        var before = layer.Matrix.MapPoint((float)layer.VectorMask.Nodes[0].X * 40, (float)layer.VectorMask.Nodes[0].Y * 40);
        session.AddSmartFilter(layer, new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 3 });
        var node = layer.VectorMask!.Nodes[0]; var after = layer.Matrix.MapPoint((float)node.X * layer.Pixels!.Width, (float)node.Y * layer.Pixels.Height);
        Assert.Equal(before.X, after.X, 4); Assert.Equal(before.Y, after.Y, 4);
    }
    [Fact]
    public void Splitting_a_bezier_curve_preserves_its_geometry_and_nodes_can_be_removed()
    {
        var path = new VectorPath { Nodes = [new(.1, .2, .1, .2, .4, .1), new(.9, .8, .6, .9, .9, .8)] };
        var split = path.SplitSegment(0, .5); Assert.Equal(3, split.Nodes.Length); Assert.Equal(.5, split.Nodes[1].X, 6); Assert.Equal(.5, split.Nodes[1].Y, 6);
        using var a = Pixels.NewMask(100, 100); using var b = Pixels.NewMask(100, 100);
        using var originalPath = path.Build(100, 100); using var splitPath = split.Build(100, 100); using var paint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        using (var canvas = new SKCanvas(a)) canvas.DrawPath(originalPath, paint); using (var canvas = new SKCanvas(b)) canvas.DrawPath(splitPath, paint);
        Assert.Equal(a.GetPixelSpan().ToArray(), b.GetPixelSpan().ToArray()); Assert.Equal(2, split.RemoveNode(1).Nodes.Length);
    }
    [Fact]
    public void Feathered_repair_on_a_new_layer_recomposes_without_double_feathering()
    {
        using var original = Pixels.NewColor(20, 20); original.Erase(SKColors.White);
        using var filled = Pixels.NewColor(20, 20); filled.Erase(new SKColor(127, 127, 255));
        using var mask = Pixels.NewMask(20, 20, 128); using var repair = Pixels.RepairLayer(original, filled, mask);
        using var composite = Pixels.Clone(original); using (var canvas = new SKCanvas(composite)) canvas.DrawBitmap(repair, 0, 0);
        Assert.Equal(filled.GetPixel(10, 10), composite.GetPixel(10, 10)); Assert.Equal(128, repair.GetPixel(10, 10).Alpha);
    }
    [Fact]
    public void Default_edge_refinement_preserves_a_sharp_mask_on_a_uniform_image()
    {
        using var image = Pixels.NewColor(80, 60); image.Erase(SKColors.White);
        using var mask = SelectionMask.FromRect(80, 60, new SKRect(20, 10, 60, 50));
        using var refined = MaskRefinement.Refine(mask, image, new());
        Assert.Equal(mask.GetPixelSpan().ToArray(), refined.GetPixelSpan().ToArray());
    }
}
