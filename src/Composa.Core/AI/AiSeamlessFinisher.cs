using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Automatic registration and a screened harmonic color residual. No texture synthesis or contrast gain.</summary>
public static class AiSeamlessFinisher
{
    public static SKBitmap Match(SKBitmap generated, SKBitmap reference, SKBitmap coverage, SKRectI decodedArea, int seamWidth, bool matchFlatBackground = true)
    {
        if (generated.Width != reference.Width || generated.Height != reference.Height
            || coverage.Width != reference.Width || coverage.Height != reference.Height)
            throw new ArgumentException("Generated pixels, reference and coverage must share coordinates.");
        var area = SKRectI.Intersect(decodedArea, reference.Info.Rect);
        var result = Pixels.Clone(generated);
        if (area.IsEmpty) return result;
        var (dx, dy) = Registration(generated, reference, coverage, area);
        if (dx != 0 || dy != 0)
        {
            var source = generated.GetPixelSpan(); var target = result.GetPixelSpan();
            for (var y = area.Top; y < area.Bottom; y++) for (var x = area.Left; x < area.Right; x++)
            {
                var sx = Math.Clamp(x + dx, area.Left, area.Right - 1); var sy = Math.Clamp(y + dy, area.Top, area.Bottom - 1);
                source.Slice(sy * generated.RowBytes + sx * 4, 4).CopyTo(target.Slice(y * result.RowBytes + x * 4, 4));
            }
        }
        var flat = matchFlatBackground && IsFlatContinuation(result, reference, coverage, area);
        var field = Residual.Build(result, reference, coverage, area, seamWidth, flat);
        var pixels = result.GetPixelSpan(); var original = reference.GetPixelSpan(); var mask = coverage.GetPixelSpan();
        for (var y = area.Top; y < area.Bottom; y++) for (var x = area.Left; x < area.Right; x++)
        {
            var index = y * result.RowBytes + x * 4;
            if (mask[y * coverage.RowBytes + x] == 0)
            {
                original.Slice(y * reference.RowBytes + x * 4, 4).CopyTo(pixels.Slice(index, 4));
                continue;
            }
            var alpha = pixels[index + 3];
            if (alpha == 0 || field == null) continue;
            for (var c = 0; c < 3; c++)
            {
                var straight = pixels[index + c] * 255.0 / alpha;
                var corrected = Math.Clamp(straight + field.At(x, y, c), 0, 255);
                pixels[index + c] = (byte)Math.Round(corrected * alpha / 255);
            }
        }
        Pixels.Invalidate(result); return result;
    }

    private static double Luma(SKColor c) => (54 * c.Red + 183 * c.Green + 19 * c.Blue) / 256.0;

    private static bool IsFlatContinuation(SKBitmap generated, SKBitmap reference, SKBitmap mask, SKRectI area)
    {
        var sum = new double[6]; var squares = new double[6]; var counts = new int[2];
        var step = Math.Max(1, Math.Max(area.Width, area.Height) / 96);
        for (var y = area.Top; y < area.Bottom; y += step) for (var x = area.Left; x < area.Right; x += step)
        {
            var alpha = mask.GetPixel(x, y).Alpha;
            var group = alpha == 0 ? 0 : alpha == 255 ? 1 : -1;
            if (group < 0) continue;
            var color = group == 0 ? reference.GetPixel(x, y) : generated.GetPixel(x, y);
            if (color.Alpha != 255) continue;
            for (var c = 0; c < 3; c++) { var value = c == 0 ? color.Red : c == 1 ? color.Green : color.Blue;
                var i = group * 3 + c; sum[i] += value; squares[i] += value * value; }
            counts[group]++;
        }
        if (counts.Any(n => n < 64)) return false;
        var delta = new double[3];
        for (var c = 0; c < 3; c++)
        {
            var sourceMean = sum[c] / counts[0]; var generatedMean = sum[c + 3] / counts[1];
            if (squares[c] / counts[0] - sourceMean * sourceMean > 4
                || squares[c + 3] / counts[1] - generatedMean * generatedMean > 64) return false;
            delta[c] = sourceMean - generatedMean;
            if (Math.Abs(delta[c]) > 24) return false;
        }
        // Only low-contrast, nearly neutral exposure drift on an already flat
        // background. Colored objects and intentional relighting stay independent.
        return delta.Max() - delta.Min() < 4;
    }

    private static (int X, int Y) Registration(SKBitmap generated, SKBitmap reference, SKBitmap mask, SKRectI area)
    {
        const int radius = 3;
        var anchors = new List<(int X, int Y, double Gx, double Gy)>();
        var step = Math.Max(2, Math.Max(area.Width, area.Height) / 64);
        for (var y = area.Top + radius + 1; y < area.Bottom - radius - 1; y += step)
        for (var x = area.Left + radius + 1; x < area.Right - radius - 1; x += step)
        {
            if (mask.GetPixel(x, y).Alpha != 0 || reference.GetPixel(x, y).Alpha != 255 || generated.GetPixel(x, y).Alpha != 255) continue;
            var gx = Luma(reference.GetPixel(x + 1, y)) - Luma(reference.GetPixel(x - 1, y));
            var gy = Luma(reference.GetPixel(x, y + 1)) - Luma(reference.GetPixel(x, y - 1));
            if (gx * gx + gy * gy >= 36) anchors.Add((x, y, gx, gy));
        }
        if (anchors.Count < 24) return (0, 0);
        double Error(int dx, int dy)
        {
            double sum = 0;
            foreach (var a in anchors)
            {
                var gx = Luma(generated.GetPixel(a.X + dx + 1, a.Y + dy)) - Luma(generated.GetPixel(a.X + dx - 1, a.Y + dy));
                var gy = Luma(generated.GetPixel(a.X + dx, a.Y + dy + 1)) - Luma(generated.GetPixel(a.X + dx, a.Y + dy - 1));
                sum += Math.Min(4096, (a.Gx - gx) * (a.Gx - gx) + (a.Gy - gy) * (a.Gy - gy));
            }
            return sum / anchors.Count;
        }
        var baseline = Error(0, 0); var best = baseline; var next = double.MaxValue; var shift = (X: 0, Y: 0);
        for (var dy = -radius; dy <= radius; dy++) for (var dx = -radius; dx <= radius; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var error = Error(dx, dy);
            if (error < best) { next = best; best = error; shift = (dx, dy); }
            else next = Math.Min(next, error);
        }
        // Ambiguous periodic/flat backgrounds do not justify moving an edit.
        return best < baseline * .7 && next > best * 1.15 + .5 ? shift : (0, 0);
    }

    private sealed record Residual(SKRectI Area, int Step, int Width, int Height, double[] Values)
    {
        internal static Residual? Build(SKBitmap generated, SKBitmap reference, SKBitmap mask, SKRectI area, int seam, bool flat)
        {
            var step = Math.Max(1, (Math.Max(area.Width, area.Height) + 127) / 128);
            if (flat) step = Math.Max(8, step);
            var w = (area.Width + step - 1) / step; var h = (area.Height + step - 1) / step;
            var values = new double[w * h * 3]; var counts = new int[w * h]; long total = 0;
            var flatTone = flat ? FlatSource(reference, mask, area) : SKColors.Transparent;
            var sampleStep = Math.Max(1, step / 4);
            for (var y = area.Top; y < area.Bottom; y += sampleStep) for (var x = area.Left; x < area.Right; x += sampleStep)
            {
                var inside = mask.GetPixel(x, y).Alpha != 0;
                if (inside && !flat) continue;
                var a = reference.GetPixel(x, y); var b = generated.GetPixel(x, y);
                if (inside && flat)
                {
                    // The source inside a removal may contain the old object: use
                    // the verified uniform surrounding tone, never its old pixels.
                    a = flatTone;
                }
                if (a.Alpha != 255 || b.Alpha != 255) continue;
                var cell = ((y - area.Top) / step * w + (x - area.Left) / step) * 3;
                var dr = a.Red - b.Red; var dg = a.Green - b.Green; var db = a.Blue - b.Blue;
                values[cell] += dr; values[cell + 1] += dg; values[cell + 2] += db;
                counts[cell / 3]++; total++;
            }
            if (total == 0) return null;
            for (var i = 0; i < counts.Length; i++) for (var c = 0; c < 3; c++)
                values[i * 3 + c] = counts[i] > 0 ? values[i * 3 + c] / counts[i] : 0;
            // Screen the harmonic residual towards zero away from the boundary so
            // intentional object colors/relighting survive in the interior.
            var falloff = Math.Max(8, seam * 4); var lambda = Math.Pow((double)step / falloff, 2);
            for (var pass = 0; pass < 180; pass++)
            {
                double changed = 0;
                for (var row = 0; row < h; row++) for (var col = 0; col < w; col++)
                {
                    var y = pass % 2 == 0 ? row : h - row - 1; var x = pass % 2 == 0 ? col : w - col - 1; var i = y * w + x;
                    if (counts[i] != 0) continue;
                    var n = (x > 0 ? 1 : 0) + (x + 1 < w ? 1 : 0) + (y > 0 ? 1 : 0) + (y + 1 < h ? 1 : 0);
                    for (var c = 0; c < 3; c++)
                    {
                        var next = ((x > 0 ? values[(i - 1) * 3 + c] : 0) + (x + 1 < w ? values[(i + 1) * 3 + c] : 0)
                            + (y > 0 ? values[(i - w) * 3 + c] : 0) + (y + 1 < h ? values[(i + w) * 3 + c] : 0)) / (n + lambda);
                        changed = Math.Max(changed, Math.Abs(next - values[i * 3 + c])); values[i * 3 + c] = next;
                    }
                }
                if (changed < .01) break;
            }
            return new(area, step, w, h, values);
        }

        private static SKColor FlatSource(SKBitmap reference, SKBitmap mask, SKRectI area)
        {
            long r = 0, g = 0, b = 0, count = 0;
            var step = Math.Max(1, Math.Max(area.Width, area.Height) / 96);
            for (var y = area.Top; y < area.Bottom; y += step) for (var x = area.Left; x < area.Right; x += step)
            {
                var color = reference.GetPixel(x, y);
                if (mask.GetPixel(x, y).Alpha != 0 || color.Alpha != 255) continue;
                r += color.Red; g += color.Green; b += color.Blue; count++;
            }
            return count == 0 ? SKColors.Transparent : new SKColor((byte)(r / count), (byte)(g / count), (byte)(b / count));
        }

        internal double At(int x, int y, int channel)
        {
            var px = Math.Clamp((x - Area.Left + .5) / Step - .5, 0, Width - 1); var py = Math.Clamp((y - Area.Top + .5) / Step - .5, 0, Height - 1);
            var left = (int)px; var top = (int)py; var right = Math.Min(left + 1, Width - 1); var bottom = Math.Min(top + 1, Height - 1);
            var dx = px - left; var dy = py - top;
            return (Values[(top * Width + left) * 3 + channel] * (1 - dx) + Values[(top * Width + right) * 3 + channel] * dx) * (1 - dy)
                + (Values[(bottom * Width + left) * 3 + channel] * (1 - dx) + Values[(bottom * Width + right) * 3 + channel] * dx) * dy;
        }
    }
}
