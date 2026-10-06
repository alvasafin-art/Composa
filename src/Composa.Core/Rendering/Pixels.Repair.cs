using SkiaSharp;
namespace Composa.Rendering;

public static partial class Pixels
{
    /// <summary>Separates an already feathered repair from its opaque backdrop without applying the feather twice.</summary>
    public static unsafe SKBitmap RepairLayer(SKBitmap original, SKBitmap filled, SKBitmap mask)
    {
        var result = NewColor(filled.Width, filled.Height); var a = (byte*)original.GetPixels(); var b = (byte*)filled.GetPixels(); var m = (byte*)mask.GetPixels(); var dst = (byte*)result.GetPixels();
        for (var y = 0; y < result.Height; y++) for (var x = 0; x < result.Width; x++)
        {
            var keep = 1 - m[(long)y * mask.RowBytes + x] / 255f; var to = dst + (long)y * result.RowBytes + x * 4;
            var before = a + (long)y * original.RowBytes + x * 4; var after = b + (long)y * filled.RowBytes + x * 4;
            to[3] = (byte)Math.Clamp(after[3] - before[3] * keep + .5f, 0, 255);
            for (var c = 0; c < 3; c++) to[c] = (byte)Math.Clamp(after[c] - before[c] * keep + .5f, 0, to[3]);
        }
        return result;
    }
}
