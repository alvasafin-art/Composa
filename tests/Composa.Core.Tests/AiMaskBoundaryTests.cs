using Composa.AI;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class AiMaskBoundaryTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(32)]
    [InlineData(64)]
    public void Inward_blend_preserves_exact_support_with_a_soft_edge_and_a_full_core(int blend)
    {
        using var selection = Pixels.NewMask(80, 60);
        for (var y = 20; y < 40; y++) for (var x = 25; x < 55; x++) selection.SetPixel(x, y, new SKColor(0, 0, 0, 255));
        Pixels.Invalidate(selection);
        using var mask = AiResultPostprocessor.EditMask(selection, 0, blend);
        Assert.Equal((byte)255, mask.GetPixel(40, 30).Alpha);
        Assert.InRange(mask.GetPixel(40, 20).Alpha, (byte)1, (byte)64);
        Assert.Equal((byte)0, mask.GetPixel(40, 19).Alpha);
        for (var y = 0; y < 60; y++) for (var x = 0; x < 80; x++)
            if (selection.GetPixel(x, y).Alpha == 0) Assert.Equal((byte)0, mask.GetPixel(x, y).Alpha);
        var edge = Enumerable.Range(20, 10).Select(y => mask.GetPixel(40, y).Alpha).ToArray();
        Assert.True(edge.Zip(edge.Skip(1), (a, b) => a <= b).All(value => value));
    }

    [Fact]
    public void Full_and_faint_masks_do_not_disappear_and_constrain_preserves_transparent_outside()
    {
        using var full = Pixels.NewMask(30, 20, 255); using var fullBlend = AiResultPostprocessor.EditMask(full, 0, 64);
        Assert.Equal((byte)255, fullBlend.GetPixel(0, 0).Alpha);
        using var soft = Pixels.NewMask(30, 20); soft.SetPixel(10, 10, new SKColor(0, 0, 0, 64)); Pixels.Invalidate(soft);
        using var blended = AiResultPostprocessor.EditMask(soft, 0, 32);
        Assert.Equal((byte)64, blended.GetPixel(10, 10).Alpha);
        using var original = Pixels.NewColor(30, 20); original.Erase(new SKColor(80, 120, 170, 128));
        using var generated = Pixels.NewColor(30, 20); generated.Erase(SKColors.OrangeRed);
        using var result = AiResultPostprocessor.Constrain(generated, original, blended);
        Assert.Equal(original.GetPixel(0, 0), result.GetPixel(0, 0));
        Assert.NotEqual(original.GetPixel(10, 10), result.GetPixel(10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiDimensions.FromMinimumSide(-1, 30, 20));
        Assert.Equal((1536, 1024), AiDimensions.FromMinimumSide(1024, 300, 200));
        Assert.Equal((1024, 688), AiDimensions.FromMaximumSide(1024, 300, 200));
        Assert.Equal((688, 1024), AiDimensions.FromMaximumSide(1024, 200, 300));
        Assert.Equal((300, 200), AiDimensions.FromMaximumSide(0, 300, 200));
    }
}
