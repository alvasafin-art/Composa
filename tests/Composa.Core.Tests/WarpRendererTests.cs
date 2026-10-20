using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class WarpRendererTests
{
    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Identity_and_integer_translation_keep_source_samples_precision_profile_and_alpha(ColorDepth depth)
    {
        using var source = Pixels.NewWorkingColor(83, 67, depth, RgbProfile.DisplayP3); var pixels = new PixelBuffer(source);
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++) pixels.Write(x, y, new(.2f + x / 65535f, .3f + y / 65535f, .1f, .7f));
        var bytes = source.Bytes;
        using var identity = WarpRenderer.Render(source, WarpMesh.Create()); Assert.Equal(new SKRectI(0, 0, 83, 67), identity.Bounds);
        Assert.Equal(bytes, identity.Bitmap.Bytes);
        var mesh = WarpMesh.Create(); for (var i = 0; i < mesh.Points.Length; i++) mesh = mesh.WithPoint(i, mesh.Points[i] + new WarpPoint(7.0 / 83, -3.0 / 67));
        using var moved = WarpRenderer.Render(source, mesh);
        Assert.Equal(source.ColorType, moved.Bitmap.ColorType); Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(moved.Bitmap));
        var actual = new PixelBuffer(moved.Bitmap); var tolerance = depth == ColorDepth.Sixteen ? .000004 : 1.1 / 255;
        for (var y = 1; y < 66; y++) for (var x = 1; x < 82; x++)
        for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(actual.Read(x + 7 - moved.Bounds.Left, y - 3 - moved.Bounds.Top)[c] - pixels.Read(x, y)[c]), 0, tolerance);
        Assert.Equal(bytes, source.Bytes);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Curved_surface_has_no_internal_alpha_seams_and_linked_mask_moves_with_the_material(ColorDepth depth)
    {
        using var source = Pixels.NewWorkingColor(320, 240, depth, RgbProfile.Rgb1998); var data = new PixelBuffer(source);
        for (var y = 0; y < 240; y++) for (var x = 0; x < 320; x++) data.Write(x, y, new(.15f, .3f, .1f, .65f));
        using var mask = Pixels.NewMask(320, 240, 128); var before = source.Bytes; var maskBefore = mask.Bytes;
        var mesh = WarpMesh.Create().WithPoint(5, new(.27, .21)).WithPoint(10, new(.74, .83));
        using var warped = WarpRenderer.Render(source, mesh, mask); var actual = new PixelBuffer(warped.Bitmap);
        var tolerance = depth == ColorDepth.Sixteen ? .000006 : 2.0 / 255;
        // Every sample is inside the surface, including patch and tessellation boundaries.
        for (var y = 12; y < 228; y++) for (var x = 12; x < 308; x++)
            Assert.True(Math.Abs(actual.Read(x - warped.Bounds.Left, y - warped.Bounds.Top).A - data.Read(x, y).A * (128f / 255)) <= tolerance,
                $"At {x},{y}: alpha {actual.Read(x - warped.Bounds.Left, y - warped.Bounds.Top).A}, expected {data.Read(x, y).A * (128f / 255)}; bounds {warped.Bounds}");
        Assert.Equal(before, source.Bytes); Assert.Equal(maskBefore, mask.Bytes);
        using var preview = WarpRenderer.Render(source, mesh, mask, .25);
        Assert.Equal(warped.Bounds, preview.Bounds); Assert.Equal(depth, Pixels.DepthOf(preview.Bitmap));
    }

    [Fact]
    public void Nonlinear_warp_samples_the_full_source_including_non_square_domain_and_preserves_control_nodes()
    {
        using var source = Pixels.NewColor(420, 180); var data = new PixelBuffer(source);
        for (var y = 0; y < 180; y++) for (var x = 0; x < 420; x++) data.Write(x, y, new(x / 420f, y / 180f, 0, 1));
        var mesh = WarpMesh.Create(3, 3, new(.1, .1, .8, .8)).WithPoint(5, new(.34, .21)).WithPoint(10, new(.68, .81));
        using var warped = WarpRenderer.Render(source, mesh); var actual = new PixelBuffer(warped.Bitmap);
        for (var j = 1; j <= 8; j++) for (var i = 1; i <= 8; i++)
        {
            var u = i / 9.0; var v = j / 9.0; var point = mesh.Evaluate(u, v);
            var x = (int)(point.X * source.Width) - warped.Bounds.Left; var y = (int)(point.Y * source.Height) - warped.Bounds.Top;
            // Compare the pixel centre against the exact continuous inverse, not the rounded destination of a
            // control parameter (a fraction of a destination pixel can span several compressed source pixels).
            var target = new WarpPoint((x + warped.Bounds.Left + .5) / source.Width, (y + warped.Bounds.Top + .5) / source.Height);
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var at = mesh.Evaluate(u, v); var dx = (mesh.Evaluate(u + 1e-6, v) - at) * 1e6; var dy = (mesh.Evaluate(u, v + 1e-6) - at) * 1e6;
                var delta = target - at; var determinant = dx.X * dy.Y - dx.Y * dy.X;
                u += (delta.X * dy.Y - delta.Y * dy.X) / determinant; v += (dx.X * delta.Y - dx.Y * delta.X) / determinant;
            }
            var original = mesh.Domain.At(u, v); var color = actual.Read(x, y);
            Assert.InRange(Math.Abs(color.R - (original.X - .5 / source.Width)), 0, .006);
            Assert.InRange(Math.Abs(color.G - (original.Y - .5 / source.Height)), 0, .006); Assert.Equal(1f, color.A);
        }
        // The control frame is no longer a crop: later source content outside it must remain visible.
        var outside = mesh.EvaluateExtended((.03 - mesh.Domain.X) / mesh.Domain.Width, (.5 - mesh.Domain.Y) / mesh.Domain.Height);
        Assert.InRange(actual.Read((int)(outside.X * source.Width) - warped.Bounds.Left, (int)(outside.Y * source.Height) - warped.Bounds.Top).A, .99f, 1f);
    }

    [Fact]
    public void Render_refuses_invalid_planes_oversized_extents_and_cancels_without_touching_source()
    {
        using var source = Pixels.NewColor(80, 60); var bytes = source.Bytes;
        using var wrongMask = Pixels.NewMask(40, 60);
        Assert.Throws<ArgumentException>(() => WarpRenderer.Render(source, WarpMesh.Create(), wrongMask));
        Assert.Throws<ArgumentOutOfRangeException>(() => WarpRenderer.Render(source, WarpMesh.Create(), scale: double.NaN));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => WarpRenderer.Render(source, WarpMesh.Create(), cancellationToken: cancellation.Token));
        using var wide = Pixels.NewColor(3000, 2); var mesh = WarpMesh.Create(1, 1).WithPoint(1, new(16, 0)).WithPoint(3, new(16, 1));
        Assert.Throws<InvalidOperationException>(() => WarpRenderer.Render(wide, mesh)); Assert.Equal(bytes, source.Bytes);
    }

    [Fact]
    public void Padded_filter_texture_and_original_share_the_same_warped_output_grid_without_moving_either_source()
    {
        using var original = Pixels.NewWorkingColor(100, 80, ColorDepth.Sixteen, RgbProfile.DisplayP3); var before = new PixelBuffer(original);
        for (var y = 0; y < 80; y++) for (var x = 0; x < 100; x++) before.Write(x, y, new(.2f + x / 65535f, .3f, .1f, .7f));
        using var filtered = Pixels.NewLike(original, 120, 94);
        using (var canvas = new SKCanvas(filtered)) canvas.DrawBitmap(original, 10, 7);
        new PixelBuffer(filtered).Write(5, 35, new(0, .4f, 0, .5f)); var bytes = original.Bytes;
        var geometry = new WarpGeometry(WarpLayerTests.Shift(WarpMesh.Create(), .2, 0), 100, 80, new(-10, -7, 110, 87));
        using var after = WarpRenderer.RenderRegion(filtered, geometry, new(-10, -7));
        using var source = WarpRenderer.RenderRegion(original, geometry, SKPoint.Empty, outputBounds: after.Bounds);
        Assert.Equal(after.Bounds, source.Bounds); var a = new PixelBuffer(after.Bitmap); var b = new PixelBuffer(source.Bitmap);
        var x0 = 15 - after.Bounds.Left; var y0 = 28 - after.Bounds.Top;
        Assert.InRange(Math.Abs(a.Read(x0, y0).A - .5), 0, .000004); Assert.Equal(0, b.Read(x0, y0).A);
        var x1 = 40 - after.Bounds.Left; var y1 = 30 - after.Bounds.Top;
        for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(a.Read(x1, y1)[c] - b.Read(x1, y1)[c]), 0, .000004);
        Assert.Equal(bytes, original.Bytes); Assert.Equal(original.ColorType, source.Bitmap.ColorType); Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(after.Bitmap));
    }

    [Fact]
    public void Source_coverage_is_applied_before_sampling_so_legacy_small_masks_do_not_square_alpha_at_the_outer_edge()
    {
        using var source = Pixels.NewWorkingColor(90, 60, ColorDepth.Sixteen, RgbProfile.DisplayP3); var pixels = new PixelBuffer(source);
        using var expectedSource = Pixels.NewLike(source); var expected = new PixelBuffer(expectedSource);
        for (var y = 0; y < 60; y++) for (var x = 0; x < 90; x++)
        {
            var value = new PixelValue(.2f + x / 65535f, .3f, .1f, .65f); pixels.Write(x, y, value); expected.Write(x, y, value * (128f / 255));
        }
        using var mask = Pixels.NewMask(9, 6, 128); var bytes = source.Bytes;
        var mesh = WarpLayerTests.Shift(WarpMesh.Create(), .13, .07).WithPoint(5, new(.39, .34));
        var layer = Layer.Raster("Raw model", source); layer.Mask = mask; layer.Warp = mesh;
        var actual = layer.WarpAppearance(); using var reference = WarpRenderer.Render(expectedSource, mesh);
        Assert.Equal(reference.Bitmap.Info.Size, actual.Pixels!.Info.Size);
        var a = new PixelBuffer(actual.Pixels); var b = new PixelBuffer(reference.Bitmap);
        for (var y = 0; y < a.Height; y++) for (var x = 0; x < a.Width; x++) for (var c = 0; c < 4; c++)
            Assert.InRange(Math.Abs(a.Read(x, y)[c] - b.Read(x, y)[c]), 0, .000004);
        Assert.Equal(bytes, source.Bytes);
    }
}
