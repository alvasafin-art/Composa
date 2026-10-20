using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HighPrecisionFilterTests
{
    private static SKBitmap Source()
    {
        var source = Pixels.NewColor(512, 32, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(source);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var r = (24000 + x) / 65535f;
            pixels.Write(x, y, new(r, r * .8f, r * .6f, 1));
        }
        return source;
    }

    [Theory]
    [InlineData(FilterKind.GaussianBlur)]
    [InlineData(FilterKind.MotionBlur)]
    [InlineData(FilterKind.Sharpen)]
    [InlineData(FilterKind.AddNoise)]
    [InlineData(FilterKind.LensCorrection)]
    [InlineData(FilterKind.Vignette)]
    [InlineData(FilterKind.BloomGlow)]
    [InlineData(FilterKind.TonalContrast)]
    [InlineData(FilterKind.CameraRaw)]
    [InlineData(FilterKind.RemoveBackground)]
    [InlineData(FilterKind.Painterly)]
    public void Every_filter_has_a_float_path_and_never_mutates_the_committed_source(FilterKind kind)
    {
        using var source = Source();
        var before = source.Bytes;
        var settings = new FilterSettings
        {
            Kind = kind, Radius = 3, Angle = 25, Amount = 12, ClampEdges = true, Distortion = 10,
            CameraRaw = new() { Exposure = .2, Texture = 10, Clarity = 5 }, Painterly = new() { BrushSize = 8, Passes = 2 }
        };
        using var result = ImageFilters.Run(source, settings).Result;
        Assert.Equal(SKColorType.RgbaF32, result.ColorType);
        Assert.Equal(before, source.Bytes);
        var pixels = new PixelBuffer(result);
        for (var y = 0; y < result.Height; y++)
        for (var x = 0; x < result.Width; x++)
        {
            var p = pixels.Read(x, y);
            Assert.True(float.IsFinite(p.R) && float.IsFinite(p.G) && float.IsFinite(p.B) && float.IsFinite(p.A));
            Assert.InRange(p.A, 0, 1);
            Assert.InRange(p.R, 0, p.A); Assert.InRange(p.G, 0, p.A); Assert.InRange(p.B, 0, p.A);
        }
        if (kind is FilterKind.GaussianBlur or FilterKind.MotionBlur or FilterKind.Sharpen or FilterKind.CameraRaw or FilterKind.TonalContrast)
            Assert.True(Enumerable.Range(20, 472).Select(x => pixels.Read(x, result.Height / 2).R).Distinct().Count() > 450, kind.ToString());
    }

    [Fact]
    public void Camera_Raw_all_groups_preserve_fine_alpha_and_read_float_colors_correctly()
    {
        using var source = Source();
        var pixels = new PixelBuffer(source);
        var alpha = 32767 / 65535f;
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++) pixels.Write(x, y, pixels.Read(x, y) * alpha);
        var sampled = CameraRawPixels.StraightColor(source, 40, 10)!.Value;
        Assert.InRange(Math.Abs(sampled.Red - (24000 + 40) / 65535f), 0, 1e-7);
        Assert.NotNull(CameraRawPixels.AutoBalance(source));
        var settings = new CameraRawSettings
        {
            Exposure = .3, Texture = 12, Clarity = 8, Dehaze = 10, Glow = 5, VignetteAmount = -10, GrainAmount = 2,
            Curve = new() { Shadows = 10 }, Mixer = new CameraRawMixer().With(0, 0, 10),
            Grading = new() { Shadows = new() { Hue = 210, Saturation = 10 } },
            Optics = new() { RemoveChromaticAberration = true, PurpleAmount = 10, VignetteAmount = 4 },
            Detail = new() { SharpenAmount = 30, NoiseColor = 10, NoiseLuminance = 10 },
            Calibration = new() { RedHue = 5, ShadowTint = 5 }
        };
        using var edited = CameraRawPixels.Apply(source, settings);
        var result = new PixelBuffer(edited);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++) Assert.Equal(alpha, result.Read(x, y).A);
        Assert.True(Enumerable.Range(20, 472).Select(x => result.Read(x, 16).R).Distinct().Count() > 450);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(8)]
    [InlineData(30)]
    public void Float_blur_keeps_a_uniform_field_uniform_at_the_edges(double radius)
    {
        using var bitmap = Pixels.NewColor(64, 40, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(bitmap);
        var color = new PixelValue(24001 / 65535f, 16001 / 65535f, 8001 / 65535f, 32767 / 65535f);
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 64; x++) pixels.Write(x, y, color);
        ImageFilters.BlurInPlace(bitmap, (float)radius);
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 64; x++)
        {
            var actual = pixels.Read(x, y);
            Assert.InRange(Math.Abs(actual.R - color.R), 0, 1e-7);
            Assert.InRange(Math.Abs(actual.A - color.A), 0, 1e-7);
        }
    }
}
