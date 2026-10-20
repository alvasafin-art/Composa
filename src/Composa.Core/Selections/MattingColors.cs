using Composa.Rendering;
using SkiaSharp;

namespace Composa.Selections;

/// <summary>Continuous foreground/background color estimates from exact Euclidean seed fields.</summary>
internal static unsafe class MattingColors
{
    internal static SKBitmap Decontaminate(SKBitmap image, SKBitmap mask, double amount, CancellationToken cancellation)
    {
        var source = new PixelBuffer(image); var result = Pixels.Clone(image); if (amount == 0) return result;
        var output = new PixelBuffer(result); var coverage = (byte*)mask.GetPixels(); var width = image.Width; var height = image.Height; var stride = mask.RowBytes;
        try
        {
            var foreground = SeedField(source, coverage, stride, true, cancellation);
            var background = SeedField(source, coverage, stride, false, cancellation);
            PixelValue? Around(int[] field, int x, int y)
            {
                var sum = default(PixelValue); double weight = 0;
                for (var dy = -2; dy <= 2; dy += 2) for (var dx = -2; dx <= 2; dx += 2)
                {
                    var id = field[Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)]; if (id < 0) continue;
                    var sx = id % width; var sy = id / width; var distance = (double)(sx - x) * (sx - x) + (double)(sy - y) * (sy - y);
                    var w = 1 / (1 + distance); sum += source.Read(sx, sy).Straight * (float)w; weight += w;
                }
                return weight > 0 ? sum * (float)(1 / weight) : null;
            }
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var a = coverage[(long)y * stride + x]; var color = source.Read(x, y);
                    if (a is 0 or 255 || color.A < .999f || Around(foreground, x, y) is not { } fg) continue;
                    var alpha = a / 255f; var straight = color.Straight; var estimated = fg;
                    if (Around(background, x, y) is { } bg)
                    {
                        var candidate = new PixelValue((straight.R - (1 - alpha) * bg.R) / alpha,
                            (straight.G - (1 - alpha) * bg.G) / alpha, (straight.B - (1 - alpha) * bg.B) / alpha, 1);
                        var error = MathF.Sqrt(MathF.Pow(candidate.R - fg.R, 2) + MathF.Pow(candidate.G - fg.G, 2) + MathF.Pow(candidate.B - fg.B, 2));
                        // Wrong alpha or a different background texture can make unmixing explode. Such colors
                        // must fall back to the smooth foreground prior, rather than clamping into bright fringes.
                        var reliable = Math.Clamp(1 - error / .3f, 0, 1) * Math.Min(1, alpha / .2f);
                        estimated = candidate * reliable + fg * (1 - reliable);
                    }
                    estimated = new(Math.Clamp(estimated.R, 0, 1), Math.Clamp(estimated.G, 0, 1), Math.Clamp(estimated.B, 0, 1), 1);
                    var blended = straight * (float)(1 - amount) + estimated * (float)amount;
                    output.Write(x, y, (blended with { A = color.A }).Premultiplied);
                }
            });
            Pixels.Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }

    // A separable squared Euclidean distance transform. The map stores the seed's index, not a rounded color,
    // and has no directional ray boundaries. One int per pixel per field; per-column scratch is bounded by height.
    private static int[] SeedField(PixelBuffer image, byte* mask, int stride, bool foreground, CancellationToken cancellation)
    {
        var width = image.Width; var height = image.Height; var result = new int[checked(width * height)];
        var options = new ParallelOptions { CancellationToken = cancellation };
        Parallel.For(0, height, options, y =>
        {
            var last = -1;
            for (var x = 0; x < width; x++)
            {
                var alpha = mask[(long)y * stride + x];
                if ((foreground ? alpha == 255 : alpha == 0) && image.Read(x, y).A >= .999f) last = x;
                result[y * width + x] = last < 0 ? -1 : y * width + last;
            }
            last = -1;
            for (var x = width - 1; x >= 0; x--)
            {
                var alpha = mask[(long)y * stride + x];
                if ((foreground ? alpha == 255 : alpha == 0) && image.Read(x, y).A >= .999f) last = x;
                var id = result[y * width + x];
                if (last >= 0 && (id < 0 || last - x < x - id % width)) result[y * width + x] = y * width + last;
            }
        });
        Parallel.For(0, width, options, () => (Labels: new int[height], F: new double[height], Sites: new int[height], Cuts: new double[height + 1]), (x, _, scratch) =>
        {
            var (labels, f, sites, cuts) = scratch; var count = -1;
            for (var y = 0; y < height; y++)
            {
                var id = labels[y] = result[y * width + x]; if (id < 0) continue;
                f[y] = Math.Pow(x - id % width, 2); double cut = double.NegativeInfinity;
                while (count >= 0)
                {
                    var previous = sites[count]; cut = (f[y] + (double)y * y - f[previous] - (double)previous * previous) / (2 * (y - previous));
                    if (cut > cuts[count]) break; count--;
                }
                count++; sites[count] = y; cuts[count] = count == 0 ? double.NegativeInfinity : cut; cuts[count + 1] = double.PositiveInfinity;
            }
            if (count >= 0)
            {
                var k = 0;
                for (var y = 0; y < height; y++) { while (cuts[k + 1] < y) k++; result[y * width + x] = labels[sites[k]]; }
            }
            return scratch;
        }, _ => { });
        return result;
    }
}
