using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class TrimapMattingTests
{
    public sealed class NativeMattingFactAttribute : FactAttribute
    { public NativeMattingFactAttribute() { if (!TrimapMatting.IsAvailable) Skip = "Run scripts/models/fetch.sh to install the pinned AI Matting model."; } }

    [Fact]
    public void Model_input_preserves_aspect_ratio_RGB_order_and_trimap_levels_and_pads_after_normalization()
    {
        using var image = Pixels.NewColor(35, 17); image.Erase(new SKColor(51, 102, 204)); using var trimap = Pixels.NewMask(35, 17, 128);
        trimap.SetPixel(0, 0, new SKColor(0, 0, 0, 255));
        var (input, width, height) = TrimapMatting.Pack(image, trimap); Assert.Equal(64, width); Assert.Equal(32, height); var area = width * height;
        Assert.InRange(Math.Abs(input[0] + .6f), 0, 1e-6); Assert.InRange(Math.Abs(input[area] + .2f), 0, 1e-6); Assert.InRange(Math.Abs(input[area * 2] - .6f), 0, 1e-6);
        Assert.Equal(1, input[area * 3]); Assert.Equal(128 / 255f, input[area * 3 + 1]);
        for (var c = 0; c < 4; c++) { Assert.Equal(0, input[c * area + 35]); Assert.Equal(0, input[c * area + 17 * width]); }
        using var wide = Pixels.NewWorkingColor(35, 17, ColorDepth.Sixteen, RgbProfile.DisplayP3);
        Assert.Throws<ArgumentException>(() => TrimapMatting.Pack(wide, trimap));
    }

    [NativeMattingFact]
    public void Pinned_native_model_accepts_rectangular_padded_input_and_preserves_all_hard_local_constraints()
    {
        Assert.True(RefinementModels.VitMatte.Verify());
        using var image = Pixels.NewWorkingColor(131, 97, ColorDepth.Sixteen, RgbProfile.DisplayP3); image.Erase(SKColors.DarkSlateBlue);
        using (var canvas = new SKCanvas(image)) using (var paint = new SKPaint { Color = SKColors.Orange, IsAntialias = true }) canvas.DrawOval(new(32, 15, 103, 88), paint);
        using var mask = SelectionMask.FromEllipse(131, 97, new(32, 15, 103, 88)); using var edits = Pixels.NewMask(131, 97);
        edits.SetPixel(66, 50, new SKColor(0, 0, 0, (byte)MattingMark.Background)); var bytes = image.Bytes;
        using var result = TrimapMatting.Refine(image, mask, 7, edits); Assert.Equal(131, result.Width); Assert.Equal(97, result.Height);
        Assert.Equal(255, result.GetPixel(60, 40).Alpha); Assert.Equal(0, result.GetPixel(66, 50).Alpha); Assert.Equal(0, result.GetPixel(0, 0).Alpha);
        Assert.Equal(bytes, image.Bytes); Assert.Equal(SKColorType.RgbaF32, image.ColorType); Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(image));
        Assert.ThrowsAny<OperationCanceledException>(() => TrimapMatting.Refine(image, mask, cancellation: new CancellationToken(true)));
    }

    [NativeMattingFact]
    public void Large_image_refinement_returns_to_the_original_grid_and_respects_every_certain_pixel()
    {
        using var image = Pixels.NewWorkingColor(1231, 93, ColorDepth.Sixteen, RgbProfile.DisplayP3); var pixels = new PixelBuffer(image);
        using var mask = SelectionMask.FromRect(1231, 93, new(45, 20, 1170, 74));
        for (var y = 0; y < 93; y++) for (var x = 0; x < 1231; x++)
        { var inside = x >= 45 && x < 1170 && y >= 20 && y < 74; pixels.Write(x, y, inside ? new((20000 + x) / 65535f, .6f, .25f, 1) : new(.15f, .2f, .7f, 1)); }
        var original = image.Bytes;
        using var result = TrimapMatting.Refine(image, mask, 4); using var tri = AlphaMatting.Trimap(mask, 4);
        Assert.Equal(image.Width, result.Width); Assert.Equal(image.Height, result.Height);
        for (var y = 0; y < 93; y++) for (var x = 0; x < 1231; x++)
        { var known = tri.GetPixel(x, y).Alpha; if (known != 128) Assert.Equal(known, result.GetPixel(x, y).Alpha); }
        Assert.Equal(original, image.Bytes); Assert.Equal(16, (int)Pixels.DepthOf(image));
        var progress = new List<(int Done, int Total)>();
        using var detailed = TrimapMatting.Refine(image, mask, 4, fineDetail: true, progress: new DirectProgress(p => progress.Add(p)));
        Assert.NotEmpty(progress); Assert.Equal(progress[^1].Total, progress[^1].Done);
        for (var y = 0; y < 93; y++) for (var x = 0; x < 1231; x++)
        { var known = tri.GetPixel(x, y).Alpha; if (known != 128) Assert.Equal(known, detailed.GetPixel(x, y).Alpha); }
        Assert.Equal(original, image.Bytes);
    }

    private sealed class DirectProgress(Action<(int Done, int Total)> changed) : IProgress<(int Done, int Total)>
    { public void Report((int Done, int Total) value) => changed(value); }

    [Fact]
    public void RGB_guided_upsampling_follows_a_color_edge_that_has_no_luminance_edge_and_keeps_certain_pixels_exact()
    {
        using var full = Pixels.NewColor(300, 100); var pixels = new PixelBuffer(full);
        using var known = Pixels.NewMask(300, 100, 128);
        for (var y = 0; y < 100; y++) for (var x = 0; x < 300; x++)
        {
            var a = Math.Clamp((153f - x) / 6, 0, 1);
            pixels.Write(x, y, new(.1f + .7f * a, .3f - .2f * a, .2803f - .0803f * a, 1));
            if (x < 130 || x >= 170) known.SetPixel(x, y, new SKColor(0, 0, 0, x < 130 ? (byte)255 : (byte)0));
        }
        using var small = Pixels.NewColor(75, 25); using (var canvas = new SKCanvas(small)) canvas.DrawImage(Pixels.ImageOf(full), new SKRect(0, 0, 75, 25), new SKSamplingOptions(SKFilterMode.Linear));
        using var matte = Pixels.NewMask(75, 25);
        for (var y = 0; y < 25; y++) for (var x = 0; x < 75; x++) matte.SetPixel(x, y, new SKColor(0, 0, 0, (byte)MathF.Round(Math.Clamp((153 - ((x + .5f) * 4 - .5f)) / 6, 0, 1) * 255)));
        using var result = RgbGuidedMatting.Upsample(full, small, matte, known, CancellationToken.None);
        for (var x = 140; x < 160; x++) Assert.InRange(Math.Abs(result.GetPixel(x, 50).Alpha / 255f - Math.Clamp((153f - x) / 6, 0, 1)), 0, .08f);
        Assert.Equal(255, result.GetPixel(0, 0).Alpha); Assert.Equal(0, result.GetPixel(299, 99).Alpha);
    }
}
