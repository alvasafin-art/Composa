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
        var maskStride = mask?.Bitmap.RowBytes ?? 0;
        var opacity = (float)layer.Opacity;
        var blendIf = layer.BlendIf;
        for (var y = 0; y < target.Height; y++)
        {
            var maskRow = mask == null ? ReadOnlySpan<byte>.Empty : maskPixels.Slice(y * maskStride, target.Width);
            for (var x = 0; x < target.Width; x++)
            {
                var before = target.Read(x, y);
                var after = source.Read(x, y);
                var amount = opacity * (maskRow.IsEmpty ? 1 : maskRow[x] / 255f);
                amount *= BlendIfPixels.Coverage(after, before, blendIf);
                target.Write(x, y, before * (1 - amount) + after * amount);
            }
        }
    }
}
