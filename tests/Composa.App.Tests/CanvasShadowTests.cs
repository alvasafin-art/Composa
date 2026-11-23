using Avalonia;
using Composa.App.Controls;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class CanvasShadowTests
{
    [Theory]
    [InlineData(1f, 28f, 25f, 280f, 170f)]
    [InlineData(1.25f, 19.4f, 17.6f, 311.7f, 205.4f)]
    [InlineData(1.5f, -300f, 26.2f, 790f, 280f)]
    [InlineData(2f, 73.2f, -420f, 185.7f, 523f)]
    [InlineData(1f, 450f, 45f, 600f, 187f)]
    public void Page_shadow_matches_a_full_blur_where_it_is_visible(
        float scale, float left, float top, float right, float bottom)
    {
        var viewport = new Size(340, 225);
        var page = new SKRect(left, top, right, bottom);
        using var expected = Render(clipped: false);
        using var actual = Render(clipped: true);
        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));

        SKBitmap Render(bool clipped)
        {
            var pixels = new SKBitmap((int)(viewport.Width * scale), (int)(viewport.Height * scale));
            using var canvas = new SKCanvas(pixels);
            canvas.Clear(SKColors.Gray);
            canvas.Scale(scale);
            // Match the final viewport clip and cover the shadow with an opaque page fill.
            canvas.ClipRect(SKRect.Create((float)viewport.Width, (float)viewport.Height));
            if (clipped) CanvasView.DrawPageShadow(canvas, page, viewport);
            else
            {
                using var blur = SKImageFilter.CreateBlur(8, 8);
                using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), ImageFilter = blur };
                canvas.DrawRect(page, shadow);
            }
            using var fill = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(page, fill);
            return pixels;
        }
    }
}