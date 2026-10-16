using Composa.Rendering;
using SkiaSharp;

namespace Composa.Selections;

/// <summary>What the next click on the canvas does while Select > Color Range is open: start over from that color, add it, or take it away.</summary>
public enum ColorRangeSample { Sample, Add, Remove }

/// <summary>Select > Color Range: every pixel near the colors clicked on the canvas, anywhere in the image.</summary>
public static unsafe class ColorRange
{
    public const int MinFuzziness = 0, MaxFuzziness = 200, DefaultFuzziness = 40;
    /// <summary>The panel's preview fits in this many pixels, at twice that for a sharp picture on a dense screen.</summary>
    public const int PreviewWidth = 292, PreviewHeight = 200;

    /// <summary>
    /// The pixels within the fuzziness of one of the included colors, on every channel, and not within it of an
    /// excluded one, as a selection mask; inverted when asked. Transparent pixels never match (they are selected
    /// only by Invert). Also says how many pixels were selected.
    /// </summary>
    public static (SKBitmap Mask, long Count) Match(SKBitmap image, IReadOnlyList<SKColor> include, IReadOnlyList<SKColor> exclude, int fuzziness, bool invert)
    {
        int width = image.Width, height = image.Height;
        var mask = Pixels.NewMask(width, height);
        var tolerance = Math.Clamp(fuzziness, MinFuzziness, MaxFuzziness);
        var wanted = include.Select(c => (c.Red, c.Green, c.Blue)).ToArray();
        var unwanted = exclude.Select(c => (c.Red, c.Green, c.Blue)).ToArray();
        byte* pixels = (byte*)image.GetPixels();
        byte* output = (byte*)mask.GetPixels();
        int stride = image.RowBytes, maskStride = mask.RowBytes;
        var counts = new long[height];
        var floating = image.ColorType == SKColorType.RgbaF32;
        var colors = new PixelBuffer(image);
        Parallel.For(0, height, y =>
        {
            var row = pixels + y * stride;
            var outRow = output + y * maskStride;
            long count = 0;
            for (var x = 0; x < width; x++)
            {
                var px = row + x * 4;
                var matches = false;
                if (floating)
                {
                    var p = colors.Read(x, y).Straight;
                    var limit = Math.Max(.5, tolerance); // Sample colors are the dialog's 8-bit RGB values.
                    bool NearHigh((byte R, byte G, byte B)[] samples)
                    {
                        foreach (var c in samples)
                            if (Math.Abs(p.R * 255 - c.R) <= limit && Math.Abs(p.G * 255 - c.G) <= limit && Math.Abs(p.B * 255 - c.B) <= limit) return true;
                        return false;
                    }
                    matches = p.A > 0 && NearHigh(wanted) && !NearHigh(unwanted);
                }
                else if (px[3] != 0)
                {
                    int a = px[3], r = (px[0] * 255 + a / 2) / a, g = (px[1] * 255 + a / 2) / a, b = (px[2] * 255 + a / 2) / a;
                    matches = Near(r, g, b, wanted, tolerance) && !Near(r, g, b, unwanted, tolerance);
                }
                if (invert) matches = !matches;
                outRow[x] = matches ? (byte)255 : (byte)0;
                if (matches) count++;
            }
            counts[y] = count;
        });
        Pixels.Invalidate(mask);
        return (mask, counts.Sum());
    }

    private static bool Near(int r, int g, int b, (byte R, byte G, byte B)[] colors, int tolerance)
    {
        foreach (var (cr, cg, cb) in colors)
            if (Math.Abs(r - cr) <= tolerance && Math.Abs(g - cg) <= tolerance && Math.Abs(b - cb) <= tolerance) return true;
        return false;
    }

    /// <summary>The straight color under a document pixel, averaged over the 3 × 3 pixels around it; null off the image or where they are all transparent.</summary>
    public static SKColor? ColorAt(SKBitmap image, int x, int y)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return null;
        if (image.ColorType == SKColorType.RgbaF32)
        {
            var colors = new PixelBuffer(image);
            double red = 0, green = 0, blue = 0, alpha = 0;
            for (var yy = Math.Max(0, y - 1); yy <= Math.Min(image.Height - 1, y + 1); yy++)
            for (var xx = Math.Max(0, x - 1); xx <= Math.Min(image.Width - 1, x + 1); xx++)
            {
                var p = colors.Read(xx, yy); red += p.R; green += p.G; blue += p.B; alpha += p.A;
            }
            return alpha <= 0 ? null : new SKColor((byte)Math.Clamp(Math.Round(red / alpha * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(green / alpha * 255), 0, 255), (byte)Math.Clamp(Math.Round(blue / alpha * 255), 0, 255));
        }
        byte* pixels = (byte*)image.GetPixels();
        long r = 0, g = 0, b = 0, a = 0;
        for (var yy = Math.Max(0, y - 1); yy <= Math.Min(image.Height - 1, y + 1); yy++)
            for (var xx = Math.Max(0, x - 1); xx <= Math.Min(image.Width - 1, x + 1); xx++)
            {
                var px = pixels + yy * image.RowBytes + xx * 4;
                r += px[0]; g += px[1]; b += px[2]; a += px[3];
            }
        if (a == 0) return null;
        return new SKColor((byte)Math.Min(255, (r * 255 + a / 2) / a), (byte)Math.Min(255, (g * 255 + a / 2) / a), (byte)Math.Min(255, (b * 255 + a / 2) / a));
    }

    /// <summary>The mask shrunk to the panel's preview as a gray picture, white where selected, at twice the panel's size.</summary>
    public static SKBitmap Preview(SKBitmap mask)
    {
        var scale = Math.Min((double)PreviewWidth / mask.Width, (double)PreviewHeight / mask.Height) * 2;
        int width = Math.Max(1, (int)(mask.Width * scale)), height = Math.Max(1, (int)(mask.Height * scale));
        var preview = Pixels.NewColor(width, height);
        byte* source = (byte*)mask.GetPixels();
        byte* target = (byte*)preview.GetPixels();
        int sourceStride = mask.RowBytes, targetStride = preview.RowBytes, sw = mask.Width, sh = mask.Height;
        Parallel.For(0, height, y =>
        {
            int y0 = y * sh / height, y1 = Math.Max(y0 + 1, (y + 1) * sh / height);
            var row = target + y * targetStride;
            for (var x = 0; x < width; x++)
            {
                int x0 = x * sw / width, x1 = Math.Max(x0 + 1, (x + 1) * sw / width);
                long sum = 0, n = 0;
                for (var yy = y0; yy < y1; yy++)
                    for (var xx = x0; xx < x1; xx++) { sum += source[yy * sourceStride + xx]; n++; }
                var value = (byte)(sum / n);
                var px = row + x * 4;
                px[0] = value; px[1] = value; px[2] = value; px[3] = 255;
            }
        });
        Pixels.Invalidate(preview);
        return preview;
    }
}
