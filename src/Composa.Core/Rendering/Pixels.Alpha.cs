using SkiaSharp;

namespace Composa.Rendering;

public static unsafe partial class Pixels
{
    internal static void PreserveAlpha(byte* pixel, byte alpha)
    {
        var current = pixel[3];
        for (var c = 0; c < 3; c++) pixel[c] = current == 0 ? (byte)0 : (byte)Math.Min(alpha, (pixel[c] * alpha + current / 2) / current);
        pixel[3] = alpha;
    }
    public static SKBitmap WithAlphaOf(SKBitmap edited, SKBitmap original)
    {
        var result = Clone(edited);
        var dst = (byte*)result.GetPixels(); var src = (byte*)original.GetPixels();
        for (var y = 0; y < result.Height; y++)
            for (var x = 0; x < result.Width; x++)
                PreserveAlpha(dst + (long)y * result.RowBytes + x * 4,
                    x < original.Width && y < original.Height ? src[(long)y * original.RowBytes + x * 4 + 3] : (byte)0);
        Invalidate(result); return result;
    }
}
