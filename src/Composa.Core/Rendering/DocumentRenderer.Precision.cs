using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

public static partial class DocumentRenderer
{
    private static void MixHighPrecision(Tile tile, SKBitmap replacement, Layer layer, bool hasMask)
    {
        using var mask = hasMask ? tile.Sibling(mask: true) : null;
        if (mask != null)
        {
            mask.Canvas.Clear(SKColors.White);
            MultiplyByMask(layer, mask);
            mask.Canvas.Flush();
        }
        var source = new PixelBuffer(replacement); var target = new PixelBuffer(tile.Bitmap);
        ReadOnlySpan<byte> maskPixels = mask == null ? ReadOnlySpan<byte>.Empty : mask.Bitmap.GetPixelSpan();
        for (var y = 0; y < tile.Bitmap.Height; y++)
        for (var x = 0; x < tile.Bitmap.Width; x++)
        {
            var before = target.Read(x, y); var after = source.Read(x, y);
            var amount = (float)layer.Opacity * (mask == null ? 1 : maskPixels[y * mask.Bitmap.RowBytes + x] / 255f);
            amount *= BlendIfPixels.Coverage(after, before, layer.BlendIf);
            target.Write(x, y, before * (1 - amount) + after * amount);
        }
    }
}
