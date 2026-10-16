using SkiaSharp;
namespace Composa.Rendering;

public static partial class Pixels
{
    /// <summary>Separates an already feathered repair from its opaque backdrop without applying the feather twice.</summary>
    public static unsafe SKBitmap RepairLayer(SKBitmap original, SKBitmap filled, SKBitmap mask)
    {
        if (original.ColorType == SKColorType.RgbaF32 || filled.ColorType == SKColorType.RgbaF32)
        {
            var highResult = NewColor(filled.Width, filled.Height, Model.ColorDepth.Sixteen, filled.ColorSpace);
            var before = new PixelBuffer(original); var after = new PixelBuffer(filled); var output = new PixelBuffer(highResult);
            var coverage = mask.GetPixelSpan();
            for (var y = 0; y < highResult.Height; y++)
            for (var x = 0; x < highResult.Width; x++)
            {
                var keep = 1 - coverage[y * mask.RowBytes + x] / 255f;
                var oldColor = before.Read(x, y) * keep; var newColor = after.Read(x, y);
                output.Write(x, y, new(newColor.R - oldColor.R, newColor.G - oldColor.G, newColor.B - oldColor.B, newColor.A - oldColor.A));
            }
            Invalidate(highResult); return highResult;
        }
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
