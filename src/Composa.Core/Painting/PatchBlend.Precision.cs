using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Painting;

public static unsafe partial class PatchBlend
{
    private static SKBitmap BlendHighPrecision(SKBitmap original, SKBitmap donor, SKBitmap mask, SKPointI offset,
        int iterations, bool preserveAlpha, CancellationToken cancellation, SKBitmap? boundaryExclusion)
    {
        var bounds = SelectionMask.Bounds(mask, 1); bounds.Inflate(2, 2);
        bounds = Geometry.Intersect(bounds, new(0, 0, original.Width, original.Height));
        if (bounds.IsEmpty) return Pixels.Clone(original);
        if ((long)bounds.Width * bounds.Height > DocumentLimits.MaxRetouchPixels) throw new InvalidOperationException("Repair area exceeds the retouching limit.");
        int w = bounds.Width, h = bounds.Height;
        var correction = new float[w * h * 3]; var weights = new float[w * h];
        var source = new PixelBuffer(original); var sample = new PixelBuffer(donor);
        var selected = mask.GetPixelSpan();
        ReadOnlySpan<byte> excluded = boundaryExclusion == null ? default : boundaryExclusion.GetPixelSpan();
        bool Valid(int x, int y) => x + offset.X >= 0 && x + offset.X < donor.Width && y + offset.Y >= 0 && y + offset.Y < donor.Height;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int xx = x + bounds.Left, yy = y + bounds.Top, i = y * w + x;
            if (boundaryExclusion != null && excluded[yy * boundaryExclusion.RowBytes + xx] > 0) continue;
            if (!Valid(xx, yy) || selected[yy * mask.RowBytes + xx] > 0 && x > 0 && y > 0 && x < w - 1 && y < h - 1) continue;
            var a = source.Read(xx, yy).Straight; var b = sample.Read(xx + offset.X, yy + offset.Y).Straight;
            if (a.A <= 0 || b.A <= 0) continue;
            weights[i] = 1;
            for (var c = 0; c < 3; c++) correction[i * 3 + c] = a[c] - b[c];
        }
        PushPull.Fill(correction, weights, w, h, 3);
        for (var pass = 0; pass < iterations; pass++)
        for (var parity = 0; parity < 2; parity++)
        {
            cancellation.ThrowIfCancellationRequested();
            var color = parity;
            Parallel.For(1, h - 1, y =>
            {
                for (var x = 1 + ((y + color + 1) & 1); x < w - 1; x += 2)
                {
                    var i = y * w + x;
                    if (weights[i] > 0) continue;
                    for (var c = 0; c < 3; c++) correction[i * 3 + c] = (correction[(i - 1) * 3 + c] + correction[(i + 1) * 3 + c] + correction[(i - w) * 3 + c] + correction[(i + w) * 3 + c]) * .25f;
                }
            });
        }
        var result = Pixels.Clone(original);
        var output = new PixelBuffer(result);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int xx = x + bounds.Left, yy = y + bounds.Top;
            var coverage = selected[yy * mask.RowBytes + xx] / 255f;
            if (coverage <= 0 || !Valid(xx, yy)) continue;
            var from = sample.Read(xx + offset.X, yy + offset.Y).Straight;
            if (from.A <= 0) continue;
            var before = output.Read(xx, yy);
            var alpha = preserveAlpha ? before.A : from.A;
            var at = (y * w + x) * 3;
            var repaired = new PixelValue(Math.Clamp(from.R + correction[at], 0, 1), Math.Clamp(from.G + correction[at + 1], 0, 1),
                Math.Clamp(from.B + correction[at + 2], 0, 1), alpha).Premultiplied;
            output.Write(xx, yy, before * (1 - coverage) + repaired * coverage);
        }
        Pixels.Invalidate(result);
        return result;
    }
}
