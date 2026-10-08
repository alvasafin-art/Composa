using Composa.AI;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class AiConditioningImageTests
{
    [Fact]
    public void Automatic_uniform_strip_continuation_rejects_hallucinations_without_changing_a_prompted_fill()
    {
        using var reference = Pixels.NewColor(328, 240); var blue = new SKColor(90, 165, 205); reference.Erase(blue);
        using var mask = SelectionMask.FromRect(328, 240, new SKRectI(320, 0, 328, 240));
        using var generated = Pixels.NewColor(328, 240); generated.Erase(SKColors.White);
        using var continued = AiSeamlessFinisher.Match(generated, reference, mask, reference.Info.Rect, 3, continueUniformStrip: true);
        Assert.Equal(blue, continued.GetPixel(324, 100)); Assert.Equal(blue, continued.GetPixel(50, 100));
        using var prompted = AiSeamlessFinisher.Match(generated, reference, mask, reference.Info.Rect, 3);
        Assert.NotEqual(blue, prompted.GetPixel(324, 100)); Assert.Equal(SKColors.White, generated.GetPixel(324, 100));
        reference.SetPixel(100, 100, SKColors.Black); Pixels.Invalidate(reference);
        using var textured = AiSeamlessFinisher.Match(generated, reference, mask, reference.Info.Rect, 3, continueUniformStrip: true);
        Assert.NotEqual(blue, textured.GetPixel(324, 100));
    }

    [Fact]
    public void Large_black_damage_cannot_desaturate_the_reference_and_known_pixels_are_exact()
    {
        using var reference = Pixels.NewColor(320, 240); reference.Erase(new SKColor(93, 174, 213));
        using var mask = SelectionMask.FromRect(320, 240, new SKRectI(35, 25, 285, 215));
        using (var canvas = new SKCanvas(reference)) using (var paint = new SKPaint { Color = SKColors.Black }) canvas.DrawRect(new SKRect(35, 25, 285, 215), paint);
        var original = reference.GetPixelSpan().ToArray(); using var conditioning = AiConditioningImage.Continue(reference, mask);
        Assert.Equal(new SKColor(93, 174, 213), conditioning.GetPixel(160, 120));
        Assert.Equal(reference.GetPixel(10, 10), conditioning.GetPixel(10, 10)); Assert.Equal(original, reference.GetPixelSpan().ToArray());
    }

    [Fact]
    public void One_pixel_transparent_stripe_keeps_color_when_reduced_to_the_conditioning_grid()
    {
        using var reference = Pixels.NewColor(2048, 80); reference.Erase(new SKColor(85, 163, 212));
        using var mask = SelectionMask.FromRect(2048, 80, new SKRectI(1001, 0, 1002, 80));
        for (var y = 0; y < 80; y++) reference.SetPixel(1001, y, SKColors.Transparent);
        using var filled = AiConditioningImage.Continue(reference, mask);
        Assert.Equal(new SKColor(85, 163, 212), filled.GetPixel(1001, 40));
        Assert.Equal(reference.GetPixel(1000, 40), filled.GetPixel(1000, 40));
        var geometry = AutomaticAiMask.Geometry(new(1001, 0, 1002, 80), reference.Info.Rect);
        Assert.True(geometry.Bounds.Width > 100);
    }
}
