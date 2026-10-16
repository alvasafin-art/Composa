using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HighPrecisionRawTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(1)]
    public void Develop_preserves_adjacent_levels_and_applies_exposure_in_linear_light(double exposure)
    {
        var samples = new ushort[512 * 3];
        for (var x = 0; x < 512; x++)
        for (var c = 0; c < 3; c++) samples[x * 3 + c] = (ushort)(24000 + x + c * 1000);
        var raw = new RawImage(512, 1, samples);
        using var developed = raw.Develop(new() { Exposure = exposure }, depth: ColorDepth.Sixteen);
        Assert.Equal(SKColorType.RgbaF32, developed.ColorType);
        var pixels = new PixelBuffer(developed);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => pixels.Read(x, 0).R).Distinct().Count());
        for (var x = 0; x < 512; x++)
        for (var c = 0; c < 3; c++)
        {
            var encoded = samples[x * 3 + c] / 65535d;
            var linear = encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
            linear *= Math.Pow(2, exposure);
            var expected = linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
            Assert.InRange(Math.Abs(pixels.Read(x, 0)[c] - expected), 0, .0000001);
        }
        Assert.Equal((ushort)24001, raw.Samples[3]);
    }

    [Fact]
    public void Explicit_high_precision_import_retains_PNG_low_bits_and_converts_ICC()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Precision");
        using var decoded = ImageFiles.Load(Path.Combine(root, "png16-6-adam7.png"), ColorDepth.Sixteen);
        Assert.Equal(SKColorType.RgbaF32, decoded.ColorType);
        Assert.Equal(17, Enumerable.Range(0, 17).Select(x => new PixelBuffer(decoded).Read(x, 0).Straight.R).Distinct().Count());
        using var tagged = ImageFiles.Load(Path.Combine(root, "png16-adobe-rgb.png"), ColorDepth.Sixteen);
        using var reference = ImageFiles.Load(Path.Combine(root, "png16-srgb-reference.png"), ColorDepth.Sixteen);
        for (var x = 0; x < 17; x++)
        for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(new PixelBuffer(tagged).Read(x, 0)[c] - new PixelBuffer(reference).Read(x, 0)[c]), 0, .005);
    }
}
