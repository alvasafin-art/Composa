using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

public static unsafe partial class ImageFilters
{
    /// <summary>Original minus Gaussian low frequencies around neutral gray. Straight colors prevent the
    /// transparent silhouette from becoming a color edge; the original alpha and pixel grid are retained.</summary>
    private static SKBitmap HighPass(SKBitmap source, double radius)
    {
        radius = double.IsFinite(radius) ? Math.Clamp(radius, .1, 250) : 2;
        var result = Pixels.NewLike(source); var input = new PixelBuffer(source); var output = new PixelBuffer(result);
        try
        {
            int width = input.Width, height = input.Height, halo = (int)Math.Ceiling(radius * 1.5) + 2;
            // Float blur is also necessary for RGB8: dividing a quantized, nearly transparent blur by its alpha
            // creates false colored edges. Banded float scratch avoids allocating a full RGB16 surface for RGB8.
            for (var top = 0; top < height; top += 256)
            {
                var bottom = Math.Min(height, top + 256); var sourceTop = Math.Max(0, top - halo); var sourceBottom = Math.Min(height, bottom + halo);
                using var tile = Pixels.NewWorkingColor(width, sourceBottom - sourceTop, Model.ColorDepth.Sixteen, Pixels.ProfileOf(source));
                using var blurred = Pixels.NewLike(tile);
                using (var canvas = new SKCanvas(tile))
                using (var paint = new SKPaint { BlendMode = SKBlendMode.Src }) canvas.DrawBitmap(source, 0, -sourceTop, paint);
                using (var filter = SKImageFilter.CreateBlur((float)(radius / 2), (float)(radius / 2), SKShaderTileMode.Clamp))
                using (var canvas = new SKCanvas(blurred))
                using (var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src }) canvas.DrawBitmap(tile, 0, 0, paint);
                var soft = new PixelBuffer(blurred);
                Parallel.For(top, bottom, y =>
                {
                    for (var x = 0; x < width; x++)
                    {
                        var original = input.Read(x, y); if (original.A <= 0) continue;
                        var value = original.Straight; var low = soft.Read(x, y - sourceTop); var baseColor = low.A > 0 ? low.Straight : value;
                        output.Write(x, y, new PixelValue(.5f + value.R - baseColor.R, .5f + value.G - baseColor.G, .5f + value.B - baseColor.B, original.A).Premultiplied);
                    }
                });
            }
            Pixels.Invalidate(result); GC.KeepAlive(source); return result;
        }
        catch { result.Dispose(); throw; }
    }
}
