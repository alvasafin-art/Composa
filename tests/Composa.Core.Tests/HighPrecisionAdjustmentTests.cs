using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HighPrecisionAdjustmentTests
{
    private static SKBitmap Ramp()
    {
        var result = Pixels.NewColor(512, 3, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(result);
        for (var y = 0; y < 3; y++)
        for (var x = 0; x < 512; x++)
        {
            var value = (24000 + x) / 65535f;
            var alpha = y == 0 ? 1 : y == 1 ? 32767 / 65535f : 17 / 65535f;
            pixels.Write(x, y, new PixelValue(value, value, value, alpha).Premultiplied);
        }
        return result;
    }

    [Fact]
    public void Exposure_and_PNG_save_preserve_adjacent_levels_and_fine_alpha()
    {
        using var source = Ramp();
        var original = source.Bytes;
        using var edited = Pixels.Clone(source);
        new ExposureAdjustment { Exposure = .5 }.Apply(edited);
        var pixels = new PixelBuffer(edited);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => pixels.Read(x, 0).R).Distinct().Count());
        for (var y = 0; y < 3; y++) Assert.Equal(new PixelBuffer(source).Read(13, y).A, pixels.Read(13, y).A);
        using var stream = new MemoryStream(ImageFiles.Encode(edited, ExportFormat.Png));
        using var decoded = Png16Codec.TryDecode(stream)!;
        var saved = new PixelBuffer(decoded);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => saved.Read(x, 0).R).Distinct().Count());
        Assert.InRange(Math.Abs(saved.Read(13, 2).A - 17 / 65535f), 0, 1e-8);
        Assert.Equal(original, source.Bytes);
    }

    [Fact]
    public void Fine_levels_survive_repeated_inversion_and_nontrivial_curves_and_levels()
    {
        using var source = Ramp();
        using var edited = Pixels.Clone(source);
        var invert = new InvertAdjustment();
        for (var i = 0; i < 10; i++) invert.Apply(edited);
        var before = new PixelBuffer(source); var after = new PixelBuffer(edited);
        for (var x = 0; x < 512; x++) Assert.InRange(Math.Abs(before.Read(x, 1).R - after.Read(x, 1).R), 0, 1e-7);
        var curve = new CurvesAdjustment().WithChannel(0, [new(0, 0), new(128, 160), new(255, 255)]);
        curve.Apply(edited);
        new LevelsAdjustment().WithRange(0, new LevelsRange { Gamma = 1.2 }).Apply(edited);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => after.Read(x, 0).R).Distinct().Count());
        Assert.True(after.Read(100, 0).R > before.Read(100, 0).R);
    }

    public static IEnumerable<object[]> Corrections()
    {
        yield return [new InvertAdjustment()];
        yield return [new ExposureAdjustment { Exposure = .3, Offset = .01, Gamma = 1.1 }];
        yield return [new BrightnessContrastAdjustment { Brightness = 8, Contrast = 12 }];
        yield return [new LevelsAdjustment().WithRange(0, new LevelsRange { InputBlack = 12, InputWhite = 230, Gamma = 1.1 })];
        yield return [new CurvesAdjustment().WithChannel(0, [new(0, 0), new(128, 150), new(255, 255)])];
        yield return [new HueSaturationAdjustment().WithShift(HueRange.Master, new(20, 10, 5))];
        yield return [new BlackAndWhiteAdjustment { Tint = true }];
        yield return [new ColorBalanceAdjustment().WithShift(1, 0, 15)];
        yield return [new GradientMapAdjustment { Shadows = (uint)SKColors.DarkBlue, Highlights = (uint)SKColors.Gold }];
        yield return [new GrainAdjustment { Amount = 15, Size = 2, Seed = 123 }];
        yield return [new AddNoiseAdjustment { Amount = 5, Seed = 123, Gaussian = true }];
    }

    [Theory]
    [MemberData(nameof(Corrections))]
    public void High_precision_corrections_agree_with_the_existing_8_bit_behavior(Adjustment correction)
    {
        using var eight = Pixels.NewColor(16, 8);
        eight.Erase(new SKColor(90, 171, 40));
        using var high = Pixels.NewColor(16, 8, ColorDepth.Sixteen);
        using (var canvas = new SKCanvas(high)) canvas.DrawBitmap(eight, 0, 0);
        correction.Apply(eight); correction.Apply(high);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 16; x++) TestImages.AssertColor(eight.GetPixel(x, y), high.GetPixel(x, y), 2);
    }
}
