using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class ObjectEditingTests
{
    [Fact]
    public void Duplicate_move_is_one_step_and_preserves_live_properties_and_original_pixels()
    {
        var s = EditorSession.NewCanvas(300, 200, null);
        var original = s.AddShape(new ShapeStyle(ShapeKind.RoundedRectangle, (uint)SKColors.Red, 12), new SKRect(30, 40, 110, 100))!;
        s.AddEffect(original, LayerEffectKind.GradientOverlay);
        var pixels = original.Pixels!;
        var edit = s.BeginTransform("Move", duplicate: true)!;
        var copy = s.ActiveLayer!;
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Same(pixels, copy.Pixels);
        Assert.Equal(original.Shape, copy.Shape);
        Assert.Equal(original.Effects, copy.Effects);
        edit.MoveBy(25, 15);
        s.CommitTransform();
        Assert.Equal((30d, 40d), (original.Transform.X, original.Transform.Y));
        Assert.Equal((55d, 55d), (copy.Transform.X, copy.Transform.Y));
        Assert.Equal("Duplicate and Move", s.History.UndoName);
        s.Undo();
        Assert.Equal(2, s.Document.Layers.Count);
        Assert.Equal(original.Id, s.ActiveLayer!.Id);
        Assert.Same(pixels, s.ActiveLayer.Pixels);
        s.Redo();
        Assert.Equal(3, s.Document.Layers.Count);
        Assert.Equal((55d, 55d), (s.ActiveLayer!.Transform.X, s.ActiveLayer.Transform.Y));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Abandoned_duplicate_move_leaves_no_copy_or_history(bool escape)
    {
        var s = EditorSession.NewCanvas(100, 100, SKColors.Red);
        var id = s.ActiveLayer!.Id;
        var edit = s.BeginTransform("Move", duplicate: true)!;
        if (escape) { edit.MoveBy(10, 20); s.CancelTransform(); } else s.CommitTransform();
        Assert.Single(s.Document.Layers);
        Assert.Equal(id, s.ActiveLayer!.Id);
        Assert.False(s.CanUndo);
        Assert.False(s.IsInteracting);
    }

    [Fact]
    public void Multiple_selected_layers_are_duplicated_and_moved_together()
    {
        var s = EditorSession.NewCanvas(200, 200, null);
        var first = s.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Red, 0), new SKRect(10, 10, 40, 40))!;
        var second = s.AddShape(new ShapeStyle(ShapeKind.Ellipse, (uint)SKColors.Blue, 0), new SKRect(80, 80, 120, 120))!;
        s.SelectLayer(first.Id, extend: true);
        var edit = s.BeginTransform("Move", duplicate: true)!;
        Assert.Equal(2, edit.Layers.Count);
        edit.MoveBy(15, 20); s.CommitTransform();
        Assert.Equal(5, s.Document.Layers.Count);
        Assert.Equal(10, first.Transform.X);
        Assert.Equal(80, second.Transform.X);
        s.Undo(); Assert.Equal(3, s.Document.Layers.Count);
        Assert.Equal(2, s.Document.SelectedLayerIds.Count);
    }

    [Fact]
    public void Shape_properties_redraw_and_undo_without_mutating_committed_pixels()
    {
        var s = EditorSession.NewCanvas(160, 120, null);
        var layer = s.AddShape(new ShapeStyle(ShapeKind.RoundedRectangle, (uint)SKColors.Red, 0), new SKRect(20, 20, 120, 100))!;
        var original = layer.Pixels!;
        var transform = layer.Transform;
        s.ChangeShapeStyle(layer, layer.Shape! with { Fill = (uint)SKColors.Blue, Stroke = (uint)SKColors.Green, StrokeWidth = 8, CornerRadius = 20 });
        Assert.Equal(SKColors.Red, original.GetPixel(50, 40));
        Assert.Equal(SKColors.Blue, layer.Pixels!.GetPixel(50, 40));
        Assert.Equal(SKColors.Green, layer.Pixels.GetPixel(2, 40));
        Assert.Equal(0, layer.Pixels.GetPixel(0, 0).Alpha);
        Assert.Equal(transform, layer.Transform);
        s.Undo(); Assert.Same(original, s.ActiveLayer!.Pixels);
        s.Redo(); Assert.Equal(20, s.ActiveLayer!.Shape!.CornerRadius);
        s.ChangeShapeStyle(s.ActiveLayer, s.ActiveLayer.Shape with { FillEnabled = false });
        Assert.Equal(0, s.ActiveLayer.Pixels!.GetPixel(50, 40).Alpha);
        Assert.True(s.ActiveLayer.Pixels.GetPixel(2, 40).Alpha > 240);
    }

    [Fact]
    public void Increasing_a_rotated_line_width_keeps_its_ends_and_does_not_crop_the_stroke()
    {
        var s = EditorSession.NewCanvas(300, 200, null);
        var line = s.AddLine(new SKPoint(60, 70), new SKPoint(180, 70), SKColors.Blue, 8)!;
        s.SetTransform(line, line.Transform with { Rotation = 35, FlipHorizontal = true });
        var originalPixels = line.Pixels!;
        var start = line.Matrix.MapPoint((float)(line.Shape!.StartX!.Value * originalPixels.Width), (float)(line.Shape.StartY!.Value * originalPixels.Height));
        var end = line.Matrix.MapPoint((float)(line.Shape.EndX!.Value * originalPixels.Width), (float)(line.Shape.EndY!.Value * originalPixels.Height));
        s.ChangeShapeStyle(line, line.Shape with { LineWidth = 24 });
        Assert.Equal(24, line.Pixels!.Height);
        var movedStart = line.Matrix.MapPoint((float)(line.Shape!.StartX!.Value * line.Pixels.Width), (float)(line.Shape.StartY!.Value * line.Pixels.Height));
        var movedEnd = line.Matrix.MapPoint((float)(line.Shape.EndX!.Value * line.Pixels.Width), (float)(line.Shape.EndY!.Value * line.Pixels.Height));
        Assert.InRange(Math.Abs(start.X - movedStart.X) + Math.Abs(start.Y - movedStart.Y), 0, .001);
        Assert.InRange(Math.Abs(end.X - movedEnd.X) + Math.Abs(end.Y - movedEnd.Y), 0, .001);
        Assert.True(line.Pixels.GetPixel(line.Pixels.Width / 2, 1).Alpha > 240);
        s.Undo(); Assert.Same(originalPixels, s.ActiveLayer!.Pixels);
    }

    [Fact]
    public void Gradient_overlay_respects_alpha_mask_reverse_and_toggle()
    {
        var s = EditorSession.NewCanvas(100, 50, null);
        var layer = s.ActiveLayer!;
        // Prepare the bitmap before adding it to the document.
        var pixels = Pixels.NewColor(100, 50); pixels.Erase(SKColors.Red.WithAlpha(128));
        layer = s.AddImageLayer("gradient", pixels, new SKPoint(50, 25));
        s.AddMask(layer);
        var mask = Pixels.NewMask(100, 50); mask.Erase(SKColors.White);
        using (var canvas = new SKCanvas(mask))
        using (var paint = new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src }) canvas.DrawRect(0, 0, 10, 50, paint);
        layer.Mask = mask;
        var gradient = new GradientOverlayEffect { StartColor = (uint)SKColors.Black, EndColor = (uint)SKColors.White, Angle = 0 };
        s.Begin("Gradient Overlay"); s.SetEffects(layer, new LayerEffects { GradientOverlay = gradient }); s.Commit();
        var flat = s.Composite();
        Assert.Equal(0, flat.GetPixel(5, 25).Alpha);
        Assert.InRange(flat.GetPixel(50, 25).Alpha, (byte)127, (byte)129);
        Assert.True(flat.GetPixel(15, 25).Red < flat.GetPixel(90, 25).Red);
        s.SetEffects(layer, new LayerEffects { GradientOverlay = gradient with { Reverse = true } });
        Assert.True(s.Composite().GetPixel(15, 25).Red > s.Composite().GetPixel(90, 25).Red);
        s.ToggleEffect(layer, LayerEffectKind.GradientOverlay);
        Assert.Equal(SKColors.Red.WithAlpha(128), s.Composite().GetPixel(50, 25));
        Assert.False(layer.Effects!.HasVisible);
    }

    [Fact]
    public void Radial_gradient_and_shape_properties_survive_project_roundtrip_and_scaling()
    {
        var s = EditorSession.NewCanvas(160, 120, null);
        var style = new ShapeStyle(ShapeKind.RoundedRectangle, (uint)SKColors.Red, 14) { Stroke = (uint)SKColors.Blue, StrokeWidth = 4 };
        var layer = s.AddShape(style, new SKRect(20, 20, 100, 100))!;
        s.SetEffects(layer, new LayerEffects { GradientOverlay = new GradientOverlayEffect { Radial = true, StartColor = (uint)SKColors.White, EndColor = (uint)SKColors.Black, Scale = 100 } });
        Assert.True(s.Composite().GetPixel(60, 60).Red > s.Composite().GetPixel(90, 60).Red);
        using var stream = new MemoryStream(); ProjectFile.Write(s.Document, stream); stream.Position = 0;
        var loaded = ProjectFile.Read(stream);
        Assert.Equal(style, loaded.Find(layer.Id)!.Shape);
        Assert.Equal(layer.Effects, loaded.Find(layer.Id)!.Effects);
        using var before = DocumentRenderer.Flatten(s.Document); using var after = DocumentRenderer.Flatten(loaded);
        Assert.Equal(before.Bytes, after.Bytes);
        s.SetTransform(layer, layer.Transform with { Width = 120, Height = 120 });
        Assert.Equal(120, layer.Pixels!.Width);
        Assert.Equal(style, layer.Shape);
        Assert.Equal(SKColors.Red, layer.Pixels.GetPixel(60, 60));
    }
}

