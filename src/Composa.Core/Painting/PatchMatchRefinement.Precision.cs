using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

internal static unsafe partial class PatchMatchRefinement
{
    private static SKBitmap FinishHighPrecision(SKBitmap source, SKBitmap initial, SKBitmap mask, SKBitmap? exclusion,
        int[] field, int fw, int fh, SKRectI hole, CancellationToken cancellation)
    {
        if (initial.ColorType != SKColorType.RgbaF32) throw new InvalidOperationException("A display guide cannot replace high precision repair pixels.");
        var result = Pixels.Clone(initial);
        var output = new PixelBuffer(result); var original = new PixelBuffer(source); var initialColors = new PixelBuffer(initial);
        var selected = (byte*)mask.GetPixels();
        var excluded = (byte*)(exclusion ?? mask).GetPixels(); var excludeStride = (exclusion ?? mask).RowBytes;
        Parallel.For(hole.Top, hole.Bottom, new ParallelOptions { CancellationToken = cancellation }, y =>
        {
            for (var x = hole.Left; x < hole.Right; x++)
            {
                if (selected[(long)y * mask.RowBytes + x] == 0) continue;
                int gx = Math.Clamp(x * fw / source.Width, 0, fw - 1), gy = Math.Clamp(y * fh / source.Height, 0, fh - 1);
                var donor = field[gy * fw + gx];
                if (donor < 0) continue;
                var sx = (int)Math.Round((double)(donor % fw - gx) * source.Width / fw) + x;
                var sy = (int)Math.Round((double)(donor / fw - gy) * source.Height / fh) + y;
                if (sx < 1 || sy < 1 || sx >= source.Width - 1 || sy >= source.Height - 1) continue;
                var clean = true;
                for (var dy = -1; dy <= 1 && clean; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    if (excluded[(long)(sy + dy) * excludeStride + sx + dx] > 0) { clean = false; break; }
                if (!clean) continue;
                var target = output.Read(x, y);
                var best = float.MaxValue; var bestX = sx; var bestY = sy;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int xx = sx + dx, yy = sy + dy;
                    if (excluded[(long)yy * excludeStride + xx] > 0) continue;
                    var color = original.Read(xx, yy);
                    var error = 0f;
                    for (var c = 0; c < 3; c++) { var delta = color[c] - target[c]; error += delta * delta; }
                    if (error < best) { best = error; bestX = xx; bestY = yy; }
                }
                var sample = original.Read(bestX, bestY);
                float Corrected(int c)
                {
                    float donorMean = 0, targetMean = 0;
                    var count = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int tx = Math.Clamp(x + dx, 0, source.Width - 1), ty = Math.Clamp(y + dy, 0, source.Height - 1);
                        int xx = Math.Clamp(bestX + dx, 0, source.Width - 1), yy = Math.Clamp(bestY + dy, 0, source.Height - 1);
                        if (excluded[(long)yy * excludeStride + xx] > 0) continue;
                        donorMean += original.Read(xx, yy)[c]; targetMean += initialColors.Read(tx, ty)[c]; count++;
                    }
                    return Math.Clamp(sample[c] + (count > 0 ? (targetMean - donorMean) / count : 0), 0, sample.A);
                }
                output.Write(x, y, new(Corrected(0), Corrected(1), Corrected(2), sample.A));
            }
        });
        Pixels.Invalidate(result);
        return result;
    }
}
