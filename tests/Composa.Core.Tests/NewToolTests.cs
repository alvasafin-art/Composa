using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class NewToolTests
{
    [Fact]
    public void Live_shape_recolor_preserves_geometry_masks_and_immutable_history()
    {
        var s = EditorSession.NewCanvas(200, 100);
        var layer = s.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Blue, 0), new SKRect(10, 10, 50, 50))!;
        var original = layer.Pixels!; var transform = layer.Transform;
        s.SetShapeColor(layer, SKColors.Yellow);
        Assert.Equal(SKColors.Blue, original.GetPixel(20, 20));
        Assert.Equal(SKColors.Yellow, layer.Pixels!.GetPixel(20, 20));
        Assert.Equal(transform, layer.Transform); Assert.True(layer.IsLive);
        s.Undo(); Assert.Equal((uint)SKColors.Blue, s.ActiveLayer!.Shape!.Fill);
        s.Redo(); Assert.Equal((uint)SKColors.Yellow, s.ActiveLayer!.Shape!.Fill);
    }

    [Fact]
    public void Bucket_fills_only_connected_pixels_and_selection_without_mutating_history()
    {
        var s = EditorSession.NewCanvas(80, 60, SKColors.White);
        s.SelectRect(new SKRect(38, 0, 42, 60)); s.Fill(SKColors.Black); s.Deselect();
        var original = s.ActiveLayer!.Pixels!;
        s.SelectRect(new SKRect(0, 0, 20, 60)); s.Foreground = SKColors.Yellow; s.WandTolerance = 0;
        s.BucketFill(new SKPoint(5, 5));
        Assert.Equal(SKColors.Yellow, s.ActiveLayer.Pixels!.GetPixel(5, 5));
        Assert.Equal(SKColors.White, s.ActiveLayer.Pixels.GetPixel(25, 5));
        Assert.Equal(SKColors.Black, s.ActiveLayer.Pixels.GetPixel(40, 5));
        Assert.Equal(SKColors.White, s.ActiveLayer.Pixels.GetPixel(70, 5));
        Assert.Equal(SKColors.White, original.GetPixel(5, 5));
        s.Undo(); Assert.Equal(SKColors.White, s.ActiveLayer!.Pixels!.GetPixel(5, 5));
    }

    [Fact]
    public void Expand_fills_empty_pixels_with_a_narrow_inward_seam_and_preserves_distant_source()
    {
        using var context = Pixels.NewColor(320, 240); using var empty = Pixels.NewMask(320, 240, 255);
        using (var c = new SKCanvas(context)) c.Clear(SKColors.White);
        for (var y = 0; y < 240; y++) for (var x = 0; x < 320; x++)
            empty.SetPixel(x, y, new SKColor(0, 0, 0, (byte)(x >= 240 ? 255 : 0)));
        Pixels.Invalidate(empty);
        using var mask = AiResultPostprocessor.ExpansionEditMask(empty, context, 32);
        Assert.Equal((byte)0, mask.GetPixel(220, 100).Alpha);
        Assert.InRange(mask.GetPixel(233, 100).Alpha, (byte)1, (byte)254);
        Assert.Equal((byte)255, mask.GetPixel(260, 100).Alpha);
    }
}
