using Composa.Rendering;
using SkiaSharp;

namespace Composa.Selections;

/// <summary>Values in the optional local edit plane. Unpainted pixels use the automatic trimap.</summary>
public enum MattingMark : byte { None = 0, Foreground = 85, Unknown = 170, Background = 255 }

/// <summary>
/// RGB matting Laplacian (Levin, Lischinski and Weiss, equation 12), with hard trimap constraints.
/// Only unknowns are solved. Overlapping patches bound scratch memory and retain the original pixel grid.
/// A small prior regularizes regions where foreground and background cannot be distinguished from the image.
/// </summary>
public static unsafe class AlphaMatting
{
    private const int Tile = 192, Halo = 48, CoarseSide = 1024;
    private const float Prior = .00001f;

    public static SKBitmap Trimap(SKBitmap mask, int radius, SKBitmap? marks = null)
    {
        ValidateMask(mask, mask.Width, mask.Height); if (marks != null) ValidateMask(marks, mask.Width, mask.Height);
        radius = Math.Clamp(radius, 0, 100);
        using var inner = radius > 0 ? SelectionMask.Contract(mask, radius) : Pixels.Clone(mask);
        using var outer = radius > 0 ? SelectionMask.Expand(mask, radius) : Pixels.Clone(mask);
        var result = Pixels.NewMask(mask.Width, mask.Height);
        var dst = (byte*)result.GetPixels(); var outside = (byte*)outer.GetPixels(); var inside = inner == null ? null : (byte*)inner.GetPixels();
        var edits = marks == null ? null : (byte*)marks.GetPixels();
        var width = mask.Width; var height = mask.Height; var stride = result.RowBytes; var outerStride = outer.RowBytes; var innerStride = inner?.RowBytes ?? 0; var editStride = marks?.RowBytes ?? 0;
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var value = outside[y * outerStride + x] == 0 ? 0 : inside != null && inside[y * innerStride + x] == 255 ? 255 : 128;
                if (edits != null) value = (MattingMark)edits[y * editStride + x] switch
                { MattingMark.Foreground => 255, MattingMark.Background => 0, MattingMark.Unknown => 128, _ => value };
                dst[y * stride + x] = (byte)value;
            }
        });
        return result;
    }

    public static SKBitmap Refine(SKBitmap image, SKBitmap mask, int radius = 8, SKBitmap? marks = null, CancellationToken cancellation = default)
    {
        ValidateMask(mask, image.Width, image.Height);
        radius = Math.Clamp(radius, 0, 100);
        cancellation.ThrowIfCancellationRequested();
        using var trimap = Trimap(mask, radius, marks);
        if (image.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32)) throw new ArgumentException("Matting requires a working RGB image.");
        if (trimap.GetPixelSpan().IndexOf((byte)128) < 0) return Pixels.Clone(trimap);
        var source = new PixelBuffer(image); var result = Pixels.Clone(mask);
        var tri = (byte*)trimap.GetPixels(); var input = (byte*)mask.GetPixels(); var output = (byte*)result.GetPixels();
        var width = image.Width; var height = image.Height; var triStride = trimap.RowBytes; var inStride = mask.RowBytes; var outStride = result.RowBytes;
        try
        {
            if (width <= CoarseSide && height <= CoarseSide)
            {
                var values = Solve(source, tri, triStride, input, inStride, new(0, 0, width, height), cancellation);
                for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) output[y * outStride + x] = (byte)Math.Clamp((int)MathF.Round(values[y * width + x] * 255), 0, 255);
                Pixels.Invalidate(result); return result;
            }
            using var globalPrior = GlobalPrior(image, trimap, mask, cancellation);
            var guidePrior = (byte*)globalPrior.GetPixels(); var guideStride = globalPrior.RowBytes;
            // Hard constraints are exact, also for locally painted corrections far from the old boundary.
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
            { for (var x = 0; x < width; x++) if (tri[y * triStride + x] != 128) output[y * outStride + x] = tri[y * triStride + x]; });
            var columns = (width + Tile - 1) / Tile; var rows = (height + Tile - 1) / Tile;
            Parallel.For(0, columns * rows, new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = 2 }, tile =>
            {
                var left = tile % columns * Tile; var top = tile / columns * Tile;
                var right = Math.Min(width, left + Tile); var bottom = Math.Min(height, top + Tile);
                var needed = false;
                for (var y = top; y < bottom && !needed; y++) for (var x = left; x < right; x++) if (tri[y * triStride + x] == 128) { needed = true; break; }
                if (!needed) return;
                var halo = Math.Max(Halo, radius + 4);
                var bounds = new SKRectI(Math.Max(0, left - halo), Math.Max(0, top - halo), Math.Min(width, right + halo), Math.Min(height, bottom + halo));
                var values = Solve(source, tri, triStride, guidePrior, guideStride, bounds, cancellation);
                for (var y = top; y < bottom; y++) for (var x = left; x < right; x++)
                {
                    if (tri[y * triStride + x] != 128) continue;
                    var alpha = values[(y - bounds.Top) * bounds.Width + x - bounds.Left];
                    output[y * outStride + x] = (byte)Math.Clamp((int)MathF.Round(alpha * 255), 0, 255);
                }
            });
            Pixels.Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static SKBitmap GlobalPrior(SKBitmap image, SKBitmap trimap, SKBitmap original, CancellationToken cancellation)
    {
        var scale = (double)CoarseSide / Math.Max(image.Width, image.Height);
        int width = Math.Max(1, (int)Math.Round(image.Width * scale)), height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var small = Pixels.NewLike(image, width, height); using var initial = Pixels.NewMask(width, height); using var reducedTrimap = Pixels.NewMask(width, height);
        using (var canvas = new SKCanvas(small)) canvas.DrawImage(Pixels.ImageOf(image), new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        using (var canvas = new SKCanvas(initial)) canvas.DrawImage(Pixels.ImageOf(original), new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        var source = (byte*)trimap.GetPixels(); var dst = (byte*)reducedTrimap.GetPixels(); var stride = trimap.RowBytes; var dstStride = reducedTrimap.RowBytes;
        var sourceWidth = image.Width; var sourceHeight = image.Height;
        // Conservative labels: a fine unknown or a mix of F/B must not become a false certain coarse label.
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
        {
            int top = (int)Math.Floor((double)y * sourceHeight / height), bottom = Math.Min(sourceHeight, (int)Math.Ceiling((double)(y + 1) * sourceHeight / height));
            for (var x = 0; x < width; x++)
            {
                int left = (int)Math.Floor((double)x * sourceWidth / width), right = Math.Min(sourceWidth, (int)Math.Ceiling((double)(x + 1) * sourceWidth / width));
                byte low = 255, high = 0;
                for (var yy = top; yy < bottom; yy++) for (var xx = left; xx < right; xx++)
                { var a = source[yy * stride + xx]; low = Math.Min(low, a); high = Math.Max(high, a); }
                dst[y * dstStride + x] = low == high ? low : (byte)128;
            }
        });
        var values = Solve(new PixelBuffer(small), dst, dstStride, (byte*)initial.GetPixels(), initial.RowBytes, new(0, 0, width, height), cancellation);
        using var solved = Pixels.NewMask(width, height); var pixels = (byte*)solved.GetPixels(); var solvedStride = solved.RowBytes;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) pixels[y * solvedStride + x] = (byte)Math.Clamp((int)MathF.Round(values[y * width + x] * 255), 0, 255);
        var prior = Pixels.NewMask(sourceWidth, sourceHeight);
        using (var canvas = new SKCanvas(prior)) canvas.DrawImage(Pixels.ImageOf(solved), new SKRect(0, 0, sourceWidth, sourceHeight), new SKSamplingOptions(SKFilterMode.Linear));
        Pixels.Invalidate(solved); return prior;
    }

    private static float[] Solve(PixelBuffer image, byte* trimap, int triStride, byte* prior, int priorStride, SKRectI bounds, CancellationToken cancellation)
    {
        int w = bounds.Width, h = bounds.Height, size = w * h;
        var ids = new int[size]; Array.Fill(ids, -1);
        var colors = new float[size * 3]; var values = new float[size]; var positions = new List<int>();
        var low = new[] { 1f, 1f, 1f }; var high = new float[3];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var i = y * w + x; var p = image.Read(bounds.Left + x, bounds.Top + y).Straight;
            colors[i * 3] = p.R; colors[i * 3 + 1] = p.G; colors[i * 3 + 2] = p.B;
            for (var c = 0; c < 3; c++) { low[c] = Math.Min(low[c], p[c]); high[c] = Math.Max(high[c], p[c]); }
            var mark = trimap[(y + bounds.Top) * triStride + x + bounds.Left];
            values[i] = mark == 128 ? prior[(y + bounds.Top) * priorStride + x + bounds.Left] / 255f : mark / 255f;
            if (mark == 128) { ids[i] = positions.Count; positions.Add(i); }
        }
        // With indistinguishable F/B colors the image supplies no evidence. Keep the user's prior instead of
        // replacing a deliberate hard contour with a harmonic blur. Painted hard constraints still hold.
        if (Enumerable.Range(0, 3).All(c => high[c] - low[c] < 1f / 255)) return values;
        var count = positions.Count;
        var matrix = new float[count * 25]; var rhs = new float[count]; var solution = new float[count];
        for (var i = 0; i < count; i++) { solution[i] = values[positions[i]]; matrix[i * 25 + 12] = Prior; rhs[i] = Prior * solution[i]; }
        Span<int> window = stackalloc int[9]; Span<double> centered = stackalloc double[27];
        for (var y = 0; y < h; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < w; x++)
            {
                int n = 0; var hasUnknown = false; double r = 0, g = 0, b = 0;
                for (var yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                for (var xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                {
                    var p = yy * w + xx; window[n++] = p; hasUnknown |= ids[p] >= 0;
                    r += colors[p * 3]; g += colors[p * 3 + 1]; b += colors[p * 3 + 2];
                }
                if (!hasUnknown) continue;
                r /= n; g /= n; b /= n;
                double rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;
                for (var j = 0; j < n; j++)
                {
                    var p = window[j]; var cr = colors[p * 3] - r; var cg = colors[p * 3 + 1] - g; var cb = colors[p * 3 + 2] - b;
                    centered[j * 3] = cr; centered[j * 3 + 1] = cg; centered[j * 3 + 2] = cb;
                    rr += cr * cr; rg += cr * cg; rb += cr * cb; gg += cg * cg; gb += cg * cb; bb += cb * cb;
                }
                // Invert covariance + epsilon/n I in double precision; no grayscale edge shortcut.
                const double epsilon = 1e-5;
                rr = (rr + epsilon) / n; rg /= n; rb /= n; gg = (gg + epsilon) / n; gb /= n; bb = (bb + epsilon) / n;
                var ar = gg * bb - gb * gb; var ag = rb * gb - rg * bb; var ab = rg * gb - rb * gg;
                var bg = rr * bb - rb * rb; var bc = rg * rb - rr * gb; var cc = rr * gg - rg * rg;
                var det = rr * ar + rg * ag + rb * ab;
                for (var i = 0; i < n; i++)
                {
                    var row = ids[window[i]]; if (row < 0) continue;
                    var cr = centered[i * 3]; var cg = centered[i * 3 + 1]; var cb = centered[i * 3 + 2];
                    var vr = (ar * cr + ag * cg + ab * cb) / det;
                    var vg = (ag * cr + bg * cg + bc * cb) / det;
                    var vb = (ab * cr + bc * cg + cc * cb) / det;
                    for (var j = 0; j < n; j++)
                    {
                        var weight = (float)((i == j ? 1 : 0) - (1 + vr * centered[j * 3] + vg * centered[j * 3 + 1] + vb * centered[j * 3 + 2]) / n);
                        var p = window[j];
                        if (ids[p] < 0) rhs[row] -= weight * values[p];
                        else
                        {
                            var dx = p % w - window[i] % w; var dy = p / w - window[i] / w;
                            matrix[row * 25 + (dy + 2) * 5 + dx + 2] += weight;
                        }
                    }
                }
            }
        }
        var neighbors = new int[count * 25]; Array.Fill(neighbors, -1);
        for (var i = 0; i < count; i++)
        {
            var p = positions[i]; var x = p % w; var y = p / w;
            for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++)
            { if (x + dx >= 0 && x + dx < w && y + dy >= 0 && y + dy < h) neighbors[i * 25 + (dy + 2) * 5 + dx + 2] = ids[p + dy * w + dx]; }
        }
        ConjugateGradient(matrix, neighbors, rhs, solution, cancellation);
        for (var i = 0; i < count; i++) values[positions[i]] = float.IsFinite(solution[i]) ? Math.Clamp(solution[i], 0, 1) : values[positions[i]];
        return values;
    }

    private static void ConjugateGradient(float[] matrix, int[] neighbors, float[] rhs, float[] x, CancellationToken cancellation)
    {
        var n = x.Length; var residual = new float[n]; var z = new float[n]; var direction = new float[n]; var product = new float[n];
        void Multiply(float[] input, float[] output)
        {
            for (var i = 0; i < n; i++)
            {
                double sum = 0; var offset = i * 25;
                for (var k = 0; k < 25; k++) if (neighbors[offset + k] is var id && id >= 0) sum += matrix[offset + k] * input[id];
                output[i] = (float)sum;
            }
        }
        static double Dot(float[] a, float[] b) { double sum = 0; for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i]; return sum; }
        Multiply(x, product);
        for (var i = 0; i < n; i++) { residual[i] = rhs[i] - product[i]; direction[i] = z[i] = residual[i] / Math.Max(1e-8f, matrix[i * 25 + 12]); }
        var rz = Dot(residual, z); var tolerance = Math.Max(1e-12, Dot(rhs, rhs) * 1e-10);
        for (var iteration = 0; iteration < 640 && Dot(residual, residual) > tolerance; iteration++)
        {
            cancellation.ThrowIfCancellationRequested(); Multiply(direction, product);
            var denom = Dot(direction, product); if (denom <= 1e-20 || !double.IsFinite(denom)) break;
            var step = rz / denom;
            for (var i = 0; i < n; i++) { x[i] += (float)(step * direction[i]); residual[i] -= (float)(step * product[i]); z[i] = residual[i] / Math.Max(1e-8f, matrix[i * 25 + 12]); }
            var next = Dot(residual, z); var beta = next / Math.Max(rz, 1e-30); rz = next;
            for (var i = 0; i < n; i++) direction[i] = z[i] + (float)(beta * direction[i]);
        }
    }

    private static void ValidateMask(SKBitmap mask, int width, int height)
    { if (mask.ColorType != SKColorType.Alpha8 || mask.Width != width || mask.Height != height) throw new ArgumentException("Matting requires matching image and Alpha8 grids."); }
}
