using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

/// <summary>Full float RGB blur. Skia image-filter intermediates may quantize to eight bits even on a float canvas.</summary>
internal static class FloatGaussian
{
    internal static void ApplyInPlace(SKBitmap bitmap, double sigma, bool clamp = true)
    {
        if (sigma <= 0) return;
        using var temporary = Pixels.NewLike(bitmap);
        if (sigma <= 2)
        {
            var radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var kernel = Enumerable.Range(-radius, radius * 2 + 1).Select(x => Math.Exp(-x * x / (2 * sigma * sigma))).ToArray();
            var sum = kernel.Sum();
            for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;
            Convolve(bitmap, temporary, kernel, horizontal: true, clamp);
            Convolve(temporary, bitmap, kernel, horizontal: false, clamp);
        }
        else
        {
            // Three odd-width box convolutions approximate a Gaussian with the requested variance, in O(pixels).
            // The sum of their radii stays inside the renderer's three-sigma halo.
            var ideal = Math.Sqrt(4 * sigma * sigma + 1);
            var lower = (int)Math.Floor(ideal);
            if ((lower & 1) == 0) lower--;
            var upper = lower + 2;
            var lowerVariance = (lower * (double)lower - 1) / 12;
            var upperVariance = (upper * (double)upper - 1) / 12;
            var lowerCount = Math.Clamp((int)Math.Round((3 * upperVariance - sigma * sigma) / (upperVariance - lowerVariance)), 0, 3);
            for (var i = 0; i < 3; i++)
            {
                var radius = ((i < lowerCount ? lower : upper) - 1) / 2;
                Box(bitmap, temporary, radius, horizontal: true, clamp);
                Box(temporary, bitmap, radius, horizontal: false, clamp);
            }
        }
        Pixels.Invalidate(bitmap);
    }

    private static PixelValue Sample(PixelBuffer source, int x, int y, bool clamp)
    {
        if (clamp) return source.Read(Math.Clamp(x, 0, source.Width - 1), Math.Clamp(y, 0, source.Height - 1));
        return x < 0 || y < 0 || x >= source.Width || y >= source.Height ? default : source.Read(x, y);
    }

    private static void Convolve(SKBitmap input, SKBitmap output, double[] kernel, bool horizontal, bool clamp)
    {
        var source = new PixelBuffer(input); var target = new PixelBuffer(output);
        var radius = kernel.Length / 2;
        Parallel.For(0, input.Height, y =>
        {
            for (var x = 0; x < input.Width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var i = -radius; i <= radius; i++)
                {
                    var p = Sample(source, x + (horizontal ? i : 0), y + (horizontal ? 0 : i), clamp);
                    var weight = kernel[i + radius];
                    r += p.R * weight; g += p.G * weight; b += p.B * weight; a += p.A * weight;
                }
                target.Write(x, y, new((float)r, (float)g, (float)b, (float)a));
            }
        });
    }

    private static void Box(SKBitmap input, SKBitmap output, int radius, bool horizontal, bool clamp)
    {
        var source = new PixelBuffer(input); var target = new PixelBuffer(output);
        var length = horizontal ? input.Width : input.Height;
        var lines = horizontal ? input.Height : input.Width;
        var divisor = radius * 2 + 1;
        Parallel.For(0, lines, line =>
        {
            double r = 0, g = 0, b = 0, a = 0;
            PixelValue At(int at) => Sample(source, horizontal ? at : line, horizontal ? line : at, clamp);
            for (var i = -radius; i <= radius; i++)
            {
                var p = At(i); r += p.R; g += p.G; b += p.B; a += p.A;
            }
            for (var i = 0; i < length; i++)
            {
                target.Write(horizontal ? i : line, horizontal ? line : i, new((float)(r / divisor), (float)(g / divisor), (float)(b / divisor), (float)(a / divisor)));
                var remove = At(i - radius); var add = At(i + radius + 1);
                r += (double)add.R - remove.R; g += (double)add.G - remove.G; b += (double)add.B - remove.B; a += (double)add.A - remove.A;
            }
        });
    }
}
