using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>Coarse-to-fine coherent donor search followed by voting from original-resolution texture.</summary>
internal static unsafe class PatchMatchRefinement
{
    public static SKBitmap Fill(SKBitmap source, SKBitmap initial, SKBitmap mask, SKBitmap? exclusion, CancellationToken cancellation)
    {
        var hole = SelectionMask.Bounds(mask, 1);
        if (hole.IsEmpty) return Pixels.Clone(initial);
        int[]? field = null; var previousWidth = 0; var previousHeight = 0;
        SKBitmap? guide = null; SKBitmap? avoid = null;
        var random = new Random(314159);
        try
        {
            foreach (var edge in new[] { 128, 256, 512 })
            {
                cancellation.ThrowIfCancellationRequested();
                var scale = Math.Min(1, (double)edge / Math.Max(source.Width, source.Height));
                var w = Math.Max(3, (int)Math.Round(source.Width * scale)); var h = Math.Max(3, (int)Math.Round(source.Height * scale));
                guide?.Dispose(); avoid?.Dispose();
                guide = Resize(source, w, h, mask: false); avoid = Resize(exclusion ?? mask, w, h, mask: true);
                using var selected = Resize(mask, w, h, mask: true);
                using var guess = Resize(initial, w, h, mask: false);
                var data = (byte*)guide.GetPixels(); var banned = (byte*)avoid.GetPixels(); var selection = (byte*)selected.GetPixels(); var estimated = (byte*)guess.GetPixels();
                var valid = new List<int>(); var legal = new bool[w * h]; var repair = new List<int>();
                for (var y = 1; y < h - 1; y++) for (var x = 1; x < w - 1; x++)
                {
                    if (selection[(long)y * selected.RowBytes + x] > 0) repair.Add(y * w + x);
                    var clean = true;
                    for (var dy = -1; dy <= 1 && clean; dy++) for (var dx = -1; dx <= 1; dx++)
                        if (banned[(long)(y + dy) * avoid.RowBytes + x + dx] > 0 || data[(long)(y + dy) * guide.RowBytes + (x + dx) * 4 + 3] < 250) { clean = false; break; }
                    if (clean) { legal[y * w + x] = true; valid.Add(y * w + x); }
                }
                if (valid.Count == 0 || repair.Count == 0) continue;
                var next = Enumerable.Repeat(-1, w * h).ToArray(); var errors = new float[w * h];
                float Cost(int target, int donor)
                {
                    if (donor < 0 || donor >= legal.Length || !legal[donor]) return float.MaxValue;
                    var tx = target % w; var ty = target / w; var sx = donor % w; var sy = donor / w; var total = 0f;
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                    {
                        var a = estimated + (long)(ty + dy) * guess.RowBytes + (tx + dx) * 4;
                        var b = data + (long)(sy + dy) * guide.RowBytes + (sx + dx) * 4;
                        var weight = selection[(long)(ty + dy) * selected.RowBytes + tx + dx] == 0 ? 1f : .25f;
                        for (var c = 0; c < 3; c++) { var difference = a[c] - b[c]; total += difference * difference * weight; }
                    }
                    return total / 27;
                }
                foreach (var i in repair)
                {
                    var donor = -1;
                    if (field != null)
                    {
                        var px = Math.Min(previousWidth - 1, i % w * previousWidth / w); var py = Math.Min(previousHeight - 1, i / w * previousHeight / h);
                        var old = field[py * previousWidth + px];
                        if (old >= 0) donor = Math.Clamp(old / previousWidth * h / previousHeight, 1, h - 2) * w + Math.Clamp(old % previousWidth * w / previousWidth, 1, w - 2);
                    }
                    if (donor < 0 || !legal[donor]) donor = valid[random.Next(valid.Count)];
                    next[i] = donor; errors[i] = Cost(i, donor);
                }
                for (var pass = 0; pass < 5; pass++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var forward = pass % 2 == 0;
                    for (var at = 0; at < repair.Count; at++)
                    {
                        var i = repair[forward ? at : repair.Count - 1 - at];
                        void Try(int candidate) { var error = Cost(i, candidate); if (error < errors[i]) { errors[i] = error; next[i] = candidate; } }
                        var direction = forward ? -1 : 1;
                        var horizontal = i + direction; var vertical = i + direction * w;
                        if (horizontal >= 0 && horizontal < next.Length && horizontal / w == i / w && next[horizontal] >= 0) Try(next[horizontal] - direction);
                        if (vertical >= 0 && vertical < next.Length && next[vertical] >= 0) Try(next[vertical] - direction * w);
                        for (var radius = Math.Max(w, h); radius >= 1; radius /= 2)
                        {
                            var donor = next[i]; var sx = Math.Clamp(donor % w + random.Next(-radius, radius + 1), 1, w - 2); var sy = Math.Clamp(donor / w + random.Next(-radius, radius + 1), 1, h - 2);
                            Try(sy * w + sx);
                        }
                    }
                    // Patch voting updates the estimate, so later passes search against coherent texture.
                    foreach (var i in repair)
                    {
                        var tx = i % w; var ty = i / w; var to = estimated + (long)ty * guess.RowBytes + tx * 4;
                        for (var c = 0; c < 3; c++)
                        {
                            float sum = 0, weight = 0;
                            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                            {
                                var xx = tx + dx; var yy = ty + dy; if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                                var q = yy * w + xx; if (next[q] < 0) continue;
                                var donor = next[q]; var sx = donor % w - dx; var sy = donor / w - dy;
                                var vote = 1 / (1 + errors[q]); sum += data[(long)sy * guide.RowBytes + sx * 4 + c] * vote; weight += vote;
                            }
                            if (weight > 0) to[c] = (byte)(sum / weight);
                        }
                    }
                }
                field = next; previousWidth = w; previousHeight = h;
                if (scale == 1) break;
            }
            if (field == null) return Pixels.Clone(initial);
            var result = Pixels.Clone(initial); var output = (byte*)result.GetPixels(); var original = (byte*)source.GetPixels(); var selectedFull = (byte*)mask.GetPixels();
            var excludedFull = (byte*)(exclusion ?? mask).GetPixels(); var excludeStride = (exclusion ?? mask).RowBytes;
            var fw = previousWidth; var fh = previousHeight;
            Parallel.For(hole.Top, hole.Bottom, new ParallelOptions { CancellationToken = cancellation }, y =>
            {
                for (var x = hole.Left; x < hole.Right; x++)
                {
                    var cover = selectedFull[(long)y * mask.RowBytes + x] / 255f; if (cover <= 0) continue;
                    var gx = Math.Clamp(x * fw / source.Width, 0, fw - 1); var gy = Math.Clamp(y * fh / source.Height, 0, fh - 1);
                    var donor = field[gy * fw + gx]; if (donor < 0) continue;
                    var sx = (int)Math.Round((double)(donor % fw - gx) * source.Width / fw) + x;
                    var sy = (int)Math.Round((double)(donor / fw - gy) * source.Height / fh) + y;
                    if (sx < 1 || sy < 1 || sx >= source.Width - 1 || sy >= source.Height - 1) continue;
                    var clean = true;
                    for (var dy = -1; dy <= 1 && clean; dy++) for (var dx = -1; dx <= 1; dx++)
                        if (excludedFull[(long)(sy + dy) * excludeStride + sx + dx] > 0) { clean = false; break; }
                    if (!clean) continue;
                    // Refine the inherited offset on the full pixel grid, preserving fine texture instead of upscaling a guide.
                    var best = float.MaxValue; var bestX = sx; var bestY = sy;
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = sx + dx; var yy = sy + dy;
                        if (excludedFull[(long)yy * excludeStride + xx] > 0) continue;
                        var error = 0f;
                        for (var c = 0; c < 3; c++) { var d = original[(long)yy * source.RowBytes + xx * 4 + c] - output[(long)y * result.RowBytes + x * 4 + c]; error += d * d; }
                        if (error < best) { best = error; bestX = xx; bestY = yy; }
                    }
                    var sample = original + (long)bestY * source.RowBytes + bestX * 4; var to = output + (long)y * result.RowBytes + x * 4;
                    var initialPixels = (byte*)initial.GetPixels();
                    for (var c = 0; c < 3; c++)
                    {
                        var donorMean = 0f; var targetMean = 0f; var count = 0;
                        for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                        {
                            int tx = Math.Clamp(x + dx, 0, source.Width - 1), ty = Math.Clamp(y + dy, 0, source.Height - 1);
                            int xx = Math.Clamp(bestX + dx, 0, source.Width - 1), yy = Math.Clamp(bestY + dy, 0, source.Height - 1);
                            if (excludedFull[(long)yy * excludeStride + xx] > 0) continue;
                            donorMean += original[(long)yy * source.RowBytes + xx * 4 + c]; targetMean += initialPixels[(long)ty * initial.RowBytes + tx * 4 + c]; count++;
                        }
                        to[c] = (byte)Math.Clamp(sample[c] + (count > 0 ? (targetMean - donorMean) / count : 0), 0, sample[3]);
                    }
                    to[3] = sample[3];
                }
            });
            return result;
        }
        finally { guide?.Dispose(); avoid?.Dispose(); }
    }
    private static SKBitmap Resize(SKBitmap image, int w, int h, bool mask)
    {
        var result = mask ? Pixels.NewMask(w, h) : Pixels.NewColor(w, h);
        using var canvas = new SKCanvas(result); canvas.DrawBitmap(image, new SKRect(0, 0, w, h));
        return result;
    }
}
