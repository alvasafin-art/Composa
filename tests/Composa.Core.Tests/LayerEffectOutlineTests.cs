using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public sealed class LayerEffectOutlineTests
{
    [Theory]
    [InlineData(1, 3, 3)]
    [InlineData(5, 97, 53)]
    [InlineData(8, 113, 47)]
    public void Outside_outline_matches_the_Skia_filter(int radius, int width, int height)
    {
        using var coverage = Pixels.NewMask(width, height);
        var random = new Random(275);
        var alpha = coverage.GetPixelSpan();
        for (int y = 0; y < height; y++)
            random.NextBytes(alpha.Slice(y * coverage.RowBytes, width));

        using var actual = LayerEffectsRenderer.Ring(coverage, new StrokeEffect { Size = radius });
        using var expected = Pixels.NewMask(width, height);
        using (var canvas = new SKCanvas(expected))
        using (var filter = SKImageFilter.CreateDilate(radius, radius))
        using (var effect = new SKPaint { ImageFilter = filter })
        using (var cut = new SKPaint { BlendMode = SKBlendMode.DstOut })
        {
            canvas.DrawBitmap(coverage, 0, 0, effect);
            canvas.DrawBitmap(coverage, 0, 0, cut);
        }
        for (int y = 0; y < height; y++)
            Assert.Equal(expected.GetPixelSpan().Slice(y * expected.RowBytes, width),
                actual.GetPixelSpan().Slice(y * actual.RowBytes, width));
    }
}
