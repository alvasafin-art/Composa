using Composa.ColorManagement;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class WarpLayerTests
{
    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Linked_mask_follows_material_and_styles_use_the_deformed_silhouette(ColorDepth depth)
    {
        var document = new Document(340, 260) { Depth = depth, Profile = RgbProfile.DisplayP3 };
        var layer = Layer.Raster("Material", Pixels.NewWorkingColor(180, 140, depth, document.Profile), 70, 55);
        var pixels = new PixelBuffer(layer.Pixels!);
        for (var y = 0; y < 140; y++) for (var x = 0; x < 180; x++) pixels.Write(x, y, new(.2f + x / 65535f, .4f, .1f, .7f));
        layer.Mask = Pixels.NewMask(180, 140);
        using (var canvas = new SKCanvas(layer.Mask)) using (var paint = new SKPaint { Color = SKColors.White }) canvas.DrawRect(25, 15, 100, 90, paint);
        layer.Warp = Shift(WarpMesh.Create(), .18, -.1);
        layer.Transform = layer.Transform with { Rotation = 12 };
        layer.Effects = new() { Shadow = new() { Angle = 90, Distance = 12, Blur = 3, Opacity = .45 } };
        document.InsertAboveActive(layer); var original = layer.Pixels!.Bytes; var mask = layer.Mask.Bytes;
        var proxy = layer.WarpAppearance(); Assert.Null(proxy.Mask); Assert.Null(proxy.Warp); Assert.Same(layer.Effects, proxy.Effects);
        using var appearance = WarpRenderer.Render(layer.Pixels, layer.Warp, layer.Mask);
        var expectedLayer = Layer.Raster("Expected", appearance.Bitmap);
        expectedLayer.Transform = LayerTransform.FromMatrix(SKMatrix.CreateTranslation(appearance.Bounds.Left, appearance.Bounds.Top).PostConcat(layer.Matrix), appearance.Bitmap.Width, appearance.Bitmap.Height);
        expectedLayer.Effects = layer.Effects;
        using var actual = DocumentRenderer.Flatten(document); using var expected = DocumentRenderer.RenderLayers(document, [expectedLayer], document.Bounds);
        Assert.Equal(expected.Bytes, actual.Bytes); Assert.Equal(original, layer.Pixels.Bytes); Assert.Equal(mask, layer.Mask.Bytes);
        Assert.True(layer.VisibleBounds.Contains(layer.Bounds)); Assert.True(actual.GetPixel(140, 120).Alpha > 0);
        Assert.Same(proxy.Pixels, layer.Clone().WarpAppearance().Pixels);
    }

    [Fact]
    public void Independent_mask_stays_at_its_document_position_and_both_sides_of_a_warp_change_remain_immutable()
    {
        var document = new Document(260, 170); var layer = Layer.Raster("Warp", Pixels.NewColor(160, 100), 40, 30); layer.Pixels!.Erase(SKColors.Red);
        layer.Mask = Pixels.NewMask(45, 80, 255); layer.MaskLinked = false; layer.PlaceMask(SKMatrix.CreateTranslation(100, 40));
        layer.Warp = Shift(WarpMesh.Create(), .25, 0); document.InsertAboveActive(layer);
        var before = layer.WarpAppearance(); var beforeBytes = before.Pixels!.Bytes; var matrix = layer.MaskMatrix;
        using var rendered = DocumentRenderer.Flatten(document);
        Assert.Equal(0, rendered.GetPixel(90, 65).Alpha); Assert.Equal(255, rendered.GetPixel(110, 65).Alpha); Assert.Equal(0, rendered.GetPixel(150, 65).Alpha);
        layer.Warp = Shift(WarpMesh.Create(), .5, 0); var after = layer.WarpAppearance();
        Assert.Equal(matrix, after.MaskMatrix); Assert.Equal(matrix, layer.MaskMatrix); Assert.Equal(beforeBytes, before.Pixels.Bytes);
        Assert.NotEqual(before.Transform.X, after.Transform.X);
    }

    [Fact]
    public void Project_retains_original_float_source_mesh_profile_and_placement_without_baking_the_deformation()
    {
        var document = new Document(300, 240) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var layer = Layer.Raster("Retained", Pixels.NewWorkingColor(120, 80, document.Depth, document.Profile), 40, 30);
        var pixels = new PixelBuffer(layer.Pixels!); for (var y = 0; y < 80; y++) for (var x = 0; x < 120; x++) pixels.Write(x, y, new(.2f + x / 65535f, .3f, .1f, .8f));
        layer.Warp = WarpMesh.Create().WithPoint(5, new(.23, .27)); layer.Transform = layer.Transform with { Rotation = -17 };
        document.InsertAboveActive(layer); var source = layer.Pixels!.Bytes;
        using var stream = new MemoryStream(); ProjectFile.Write(document, stream); stream.Position = 0; var loaded = ProjectFile.Read(stream); var restored = loaded.ActiveLayer!;
        Assert.Equal(layer.Warp, restored.Warp); Assert.Equal(layer.Transform, restored.Transform);
        // Native RGB16 storage is 16-bit PNG, so arbitrary working floats round at the unsigned-16 boundary.
        Close(layer.Pixels, restored.Pixels!, 2f / 65535);
        Assert.Equal(document.Profile, loaded.Profile); Assert.Equal(ColorDepth.Sixteen, Pixels.DepthOf(restored.Pixels!));
        using var first = DocumentRenderer.Flatten(document); using var second = DocumentRenderer.Flatten(loaded); Close(first, second, 3f / 65535);
        Assert.Equal(source, layer.Pixels.Bytes);
    }

    [Fact]
    public void Reduced_view_keeps_physical_placement_independent_mask_and_effect_distances_and_does_not_allocate_native_warp()
    {
        var document = new Document(1800, 1200) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var source = Pixels.NewWorkingColor(1200, 800, document.Depth, document.Profile); source.Erase(SKColors.Red);
        var layer = Layer.Raster("Large", source, 250, 170); document.InsertAboveActive(layer);
        layer.Warp = Shift(WarpMesh.Create(), .1, -.1); layer.Effects = new() { Shadow = new() { Distance = 24, Blur = 12 } };
        layer.Mask = Pixels.NewMask(300, 400, 255); layer.MaskLinked = false; layer.PlaceMask(SKMatrix.CreateTranslation(750, 400));
        var reduced = layer.WarpAppearanceForView(.25f);
        Assert.InRange(reduced.Pixels!.Width, 300, 302); Assert.InRange(reduced.Pixels.Height, 200, 202);
        var nativeBounds = Geometry.RoundOut(layer.WarpBounds);
        var expectedLeft = layer.Matrix.MapPoint(nativeBounds.Left, nativeBounds.Top); var actualLeft = reduced.Matrix.MapPoint(0, 0);
        Assert.InRange(Math.Abs(expectedLeft.X - actualLeft.X) + Math.Abs(expectedLeft.Y - actualLeft.Y), 0, .0001);
        Assert.Equal(layer.MaskMatrix, reduced.MaskMatrix);
        Assert.InRange(reduced.Effects!.Shadow!.Distance * reduced.Transform.Width / reduced.Pixels.Width, 23.99, 24.01);
        using var view = Pixels.NewWorkingColor(450, 300, document.Depth, document.Profile);
        DocumentRenderer.Render(document, view, new(0, 0, 450, 300), new RenderView(.25f, SKPoint.Empty));
        Assert.True(view.GetPixel(200, 120).Alpha > 240); Assert.Equal(0, view.GetPixel(160, 120).Alpha);
        var full = layer.WarpAppearance(); Assert.True(full.Pixels!.Width > 1200);
        Assert.Same(reduced.Pixels, layer.WarpAppearanceForView(.25f).Pixels); Assert.Same(full.Pixels, layer.WarpAppearance().Pixels);
    }

    [Fact]
    public void Picking_uses_the_warped_silhouette_and_legacy_masks_are_sampled_in_their_own_grid()
    {
        var layer = Layer.Raster("Material", Pixels.NewColor(120, 80), 40, 30); layer.Pixels!.Erase(SKColors.Red);
        layer.Warp = Shift(WarpMesh.Create(), .5, 0);
        Assert.Equal(0, layer.HitCoverage(new(65, 60))); Assert.Equal(1, layer.HitCoverage(new(200, 60)));
        layer.Warp = null; layer.Mask = Pixels.NewMask(12, 8, 255);
        Assert.Equal(1, layer.HitCoverage(new(145, 80)));
        layer.Mask.Erase(SKColors.Transparent); Pixels.Invalidate(layer.Mask); Assert.Equal(0, layer.HitCoverage(new(145, 80)));
    }

    internal static WarpMesh Shift(WarpMesh mesh, double x, double y)
    { for (var i = 0; i < mesh.Points.Length; i++) mesh = mesh.WithPoint(i, mesh.Points[i] + new WarpPoint(x, y)); return mesh; }

    private static void Close(SKBitmap a, SKBitmap b, float tolerance)
    {
        Assert.Equal(a.Info.Size, b.Info.Size); var first = new PixelBuffer(a); var second = new PixelBuffer(b);
        for (var y = 0; y < a.Height; y++) for (var x = 0; x < a.Width; x++) for (var c = 0; c < 4; c++)
            Assert.InRange(Math.Abs(first.Read(x, y)[c] - second.Read(x, y)[c]), 0, tolerance);
    }
}
