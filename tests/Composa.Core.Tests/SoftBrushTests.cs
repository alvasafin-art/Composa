using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SoftBrushTests
{
    [Fact]
    public void Soft_edge_is_monotonic_symmetric_and_original_is_immutable()
    {
        using var original = Pixels.NewColor(100, 100);
        using var stroke = new BrushStroke(original, new BrushSettings { Size = 60, Hardness = .2 }, BrushMode.Paint, SKColors.Black);
        stroke.AddPoint(new SKPoint(50.5f, 50.5f));
        var previous = 255;
        for (var x = 50; x < 85; x++)
        {
            var alpha = stroke.Working.GetPixel(x, 50).Alpha;
            Assert.True(alpha <= previous); previous = alpha;
            Assert.Equal(alpha, stroke.Working.GetPixel(100 - x, 50).Alpha);
        }
        Assert.Equal(0, original.GetPixel(50, 50).Alpha);
    }

    [Fact]
    public void Tiny_subpixel_stamp_changes_smoothly_and_overlaps_do_not_exceed_stroke_opacity()
    {
        using var target = Pixels.NewColor(20, 20);
        using var stroke = new BrushStroke(target, new BrushSettings { Size = 3, Hardness = .1, Opacity = .4, Spacing = .05 }, BrushMode.Paint, SKColors.White);
        stroke.AddPoint(new SKPoint(10.2f, 10.4f)); stroke.AddPoint(new SKPoint(12.4f, 10.6f)); stroke.AddPoint(new SKPoint(10.2f, 10.4f));
        var alphas = Enumerable.Range(0, 20).SelectMany(y => Enumerable.Range(0, 20).Select(x => stroke.Working.GetPixel(x, y).Alpha)).ToArray();
        Assert.InRange(alphas.Max(), 1, 102); Assert.Contains(alphas, value => value is > 0 and < 30);
        Assert.Equal(0, target.GetPixel(10, 10).Alpha);
    }
}
