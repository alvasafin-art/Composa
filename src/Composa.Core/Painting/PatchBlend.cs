using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>Transfers donor texture, solving the boundary color correction over the repair region.</summary>
public static unsafe class PatchBlend
{
    public static SKBitmap Blend(SKBitmap original, SKBitmap donor, SKBitmap mask, SKPointI offset, int iterations = 48, bool preserveAlpha = true, CancellationToken cancellation = default, SKBitmap? boundaryExclusion = null)
    {
        var bounds = SelectionMask.Bounds(mask, 1); bounds.Inflate(2, 2);
        bounds = Geometry.Intersect(bounds, new SKRectI(0, 0, original.Width, original.Height));
        if (bounds.IsEmpty) return Pixels.Clone(original);
        if ((long)bounds.Width * bounds.Height > DocumentLimits.MaxRetouchPixels) throw new InvalidOperationException("Repair area exceeds the retouching limit.");
        var w = bounds.Width; var h = bounds.Height; var correction = new float[w * h * 3]; var weights = new float[w * h];
        var src = (byte*)original.GetPixels(); var sample = (byte*)donor.GetPixels(); var selected = (byte*)mask.GetPixels();
        var excluded = boundaryExclusion == null ? null : (byte*)boundaryExclusion.GetPixels();
        bool Valid(int x, int y) => x + offset.X >= 0 && x + offset.X < donor.Width && y + offset.Y >= 0 && y + offset.Y < donor.Height;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var xx = x + bounds.Left; var yy = y + bounds.Top; var i = y * w + x;
            // A blemish outside an active selection stays untouched, but must not pull
            // its dark color into the repaired portion as a boundary constraint.
            if (excluded != null && excluded[(long)yy * boundaryExclusion!.RowBytes + xx] > 0) continue;
            if (!Valid(xx, yy) || selected[(long)yy * mask.RowBytes + xx] > 0 && x > 0 && y > 0 && x < w - 1 && y < h - 1) continue;
            var a = src + (long)yy * original.RowBytes + xx * 4; var b = sample + (long)(yy + offset.Y) * donor.RowBytes + (xx + offset.X) * 4;
            if (a[3] == 0 || b[3] == 0) continue;
            weights[i] = 1;
            for (var c = 0; c < 3; c++) correction[i * 3 + c] = a[c] * 255f / a[3] - b[c] * 255f / b[3];
        }
        PushPull.Fill(correction, weights, w, h, 3);
        // Red/black Gauss–Seidel: same-color cells have no shared edge, so rows can solve in parallel safely.
        for (var pass = 0; pass < iterations; pass++) for (var parity = 0; parity < 2; parity++)
        {
            cancellation.ThrowIfCancellationRequested();
            var color = parity;
            Parallel.For(1, h - 1, y =>
            {
                for (var x = 1 + ((y + color + 1) & 1); x < w - 1; x += 2)
                {
                    var i = y * w + x; if (weights[i] > 0) continue;
                    for (var c = 0; c < 3; c++) correction[i * 3 + c] = (correction[(i - 1) * 3 + c] + correction[(i + 1) * 3 + c] + correction[(i - w) * 3 + c] + correction[(i + w) * 3 + c]) * .25f;
                }
            });
        }
        var result = Pixels.Clone(original); var dst = (byte*)result.GetPixels();
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var xx = x + bounds.Left; var yy = y + bounds.Top; var cover = selected[(long)yy * mask.RowBytes + xx] / 255f;
            if (cover <= 0 || !Valid(xx, yy)) continue;
            var from = sample + (long)(yy + offset.Y) * donor.RowBytes + (xx + offset.X) * 4;
            if (from[3] == 0) continue;
            var to = dst + (long)yy * result.RowBytes + xx * 4; var alpha = preserveAlpha ? to[3] : from[3];
            for (var c = 0; c < 3; c++)
            {
                var value = Math.Clamp(from[c] * 255f / from[3] + correction[(y * w + x) * 3 + c], 0, 255) * alpha / 255f;
                to[c] = (byte)Math.Clamp(to[c] * (1 - cover) + value * cover + .5f, 0, alpha);
            }
            to[3] = (byte)Math.Clamp(to[3] * (1 - cover) + alpha * cover + .5f, 0, 255);
        }
        return result;
    }
}
