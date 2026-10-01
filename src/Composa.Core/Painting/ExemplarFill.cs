using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>
/// Bounded, boundary-first exemplar synthesis. Donors are immutable 5x5 patches, never selected pixels.
/// Neighbor offsets propagate good matches; a fixed-seed shrinking search refines them (PatchMatch principle).
/// Matching runs on a bounded guide, then offsets sample the ORIGINAL full-resolution pixels to retain detail.
/// This is an independent implementation, not code from Photoshop or a third-party inpainting library.
/// </summary>
internal static unsafe class ExemplarFill
{
    private const int Radius = 2;
    private const int GuideSide = 384;

    public static SKBitmap? TryFill(SKBitmap source, SKBitmap mask, SKRectI hole, SKBitmap? donorExclusion)
    {
        var margin = Math.Clamp(Math.Min(hole.Width, hole.Height), 24, 128);
        var bounds = Geometry.Intersect(new SKRectI(hole.Left - margin, hole.Top - margin, hole.Right + margin, hole.Bottom + margin),
            new SKRectI(0, 0, source.Width, source.Height));
        var scale = Math.Min(1.0, (double)GuideSide / Math.Max(bounds.Width, bounds.Height));
        int w = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var count = w * h;
        var original = new float[count * 4]; var samples = new int[count]; var selected = new bool[count]; var excluded = new bool[count];
        var src = (byte*)source.GetPixels(); var coverage = (byte*)mask.GetPixels();
        var avoidance = donorExclusion == null ? null : (byte*)donorExclusion.GetPixels();
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
        {
            int gx = (int)((long)(x - bounds.Left) * w / bounds.Width), gy = (int)((long)(y - bounds.Top) * h / bounds.Height);
            var i = gy * w + gx; var p = src + (long)y * source.RowBytes + x * 4;
            samples[i]++; selected[i] |= coverage[(long)y * mask.RowBytes + x] != 0;
            excluded[i] |= coverage[(long)y * mask.RowBytes + x] != 0
                || avoidance != null && avoidance[(long)y * donorExclusion!.RowBytes + x] != 0;
            for (var c = 0; c < 4; c++) original[i * 4 + c] += p[c];
        }
        var known = new float[count];
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < 4; c++) original[i * 4 + c] /= Math.Max(1, samples[i]);
            if (!excluded[i]) known[i] = original[i * 4 + 3] / 255;
        }

        // Integral exclusion mask: all donor pixels must be original, unselected, nontransparent samples.
        var integral = new int[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            var sum = 0;
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (excluded[i] || original[i * 4 + 3] <= 0) sum++;
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + sum;
            }
        }
        var valid = new bool[count]; var donors = new List<int>();
        for (var y = Radius; y < h - Radius; y++)
        for (var x = Radius; x < w - Radius; x++)
        {
            int l = x - Radius, r = x + Radius + 1, t = y - Radius, b = y + Radius + 1;
            if (integral[b * (w + 1) + r] - integral[t * (w + 1) + r]
                - integral[b * (w + 1) + l] + integral[t * (w + 1) + l] != 0) continue;
            valid[y * w + x] = true; donors.Add(y * w + x);
        }
        if (donors.Count == 0)
            return known.Any(value => value > 0) ? SmoothFallback(source, mask, bounds, w, h, original, known) : null;

        var current = (float[])original.Clone();
        var shifts = new float[count * 3];
        var matches = new int[count]; Array.Fill(matches, -1);
        var versions = new int[count];
        var front = new PriorityQueue<(int Pixel, int Version), float>();

        float Luma(int x, int y)
        {
            var i = (Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)) * 4;
            return current[i] * 0.2126f + current[i + 1] * 0.7152f + current[i + 2] * 0.0722f;
        }
        bool IsKnown(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && known[y * w + x] > 0;
        void Enqueue(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            var i = y * w + x;
            if (!selected[i] || known[i] > 0 || !(IsKnown(x - 1, y) || IsKnown(x + 1, y) || IsKnown(x, y - 1) || IsKnown(x, y + 1))) return;
            float confidence = 0, structure = 0;
            var nx = (IsKnown(x - 1, y) ? 1 : 0) - (IsKnown(x + 1, y) ? 1 : 0);
            var ny = (IsKnown(x, y - 1) ? 1 : 0) - (IsKnown(x, y + 1) ? 1 : 0);
            for (var dy = -Radius; dy <= Radius; dy++)
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                int px = x + dx, py = y + dy;
                if (!IsKnown(px, py)) continue;
                confidence += known[py * w + px];
                if (!IsKnown(px - 1, py) || !IsKnown(px + 1, py) || !IsKnown(px, py - 1) || !IsKnown(px, py + 1)) continue;
                var gx = Luma(px + 1, py) - Luma(px - 1, py);
                var gy = Luma(px, py + 1) - Luma(px, py - 1);
                structure = Math.Max(structure, Math.Abs(-gy * nx + gx * ny) / 255);
            }
            front.Enqueue((i, ++versions[i]), -confidence * (0.1f + structure));
        }
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) Enqueue(x, y);

        while (front.TryDequeue(out var entry, out _))
        {
            var target = entry.Pixel;
            if (entry.Version != versions[target] || known[target] > 0) continue;
            int tx = target % w, ty = target / w;
            var context = new List<(int X, int Y, float Weight)>(25);
            float confidence = 0;
            for (var dy = -Radius; dy <= Radius; dy++)
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                int x = tx + dx, y = ty + dy;
                if (!IsKnown(x, y)) continue;
                var weight = known[y * w + x]; confidence += weight;
                context.Add((dx, dy, weight));
            }
            if (context.Count == 0) continue;
            confidence /= 25;
            var winner = -1; var best = float.MaxValue;
            float bestR = 0, bestG = 0, bestB = 0;
            void Candidate(int x, int y)
            {
                if (x < 0 || x >= w || y < 0 || y >= h || !valid[y * w + x]) return;
                // Locality is a tie-breaker, not permission to use a mismatching neighboring object.
                float dr = 0, dg = 0, db = 0, total = 0;
                foreach (var (dx, dy, weight) in context)
                {
                    int a = ((ty + dy) * w + tx + dx) * 4, b = ((y + dy) * w + x + dx) * 4;
                    dr += (current[a] - original[b]) * weight;
                    dg += (current[a + 1] - original[b + 1]) * weight;
                    db += (current[a + 2] - original[b + 2]) * weight; total += weight;
                }
                dr = Math.Clamp(dr / total, -24, 24); dg = Math.Clamp(dg / total, -24, 24); db = Math.Clamp(db / total, -24, 24);
                // Modest color adaptation continues gradients without replacing texture by a smooth wash.
                float cost = 0.003f * ((x - tx) * (x - tx) + (y - ty) * (y - ty))
                    + 0.1f * total * (dr * dr + dg * dg + db * db);
                foreach (var (dx, dy, weight) in context)
                {
                    int a = ((ty + dy) * w + tx + dx) * 4, b = ((y + dy) * w + x + dx) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        var d = current[a + c] - original[b + c] - (c == 0 ? dr : c == 1 ? dg : c == 2 ? db : 0);
                        cost += d * d * weight;
                    }
                    if (cost >= best) return;
                }
                best = cost; winner = y * w + x; bestR = dr; bestG = dg; bestB = db;
            }
            // Propagate offsets from already synthesized neighbors to continue their texture/lines.
            for (var dy = -Radius - 1; dy <= Radius + 1; dy++)
            for (var dx = -Radius - 1; dx <= Radius + 1; dx++)
            {
                int x = tx + dx, y = ty + dy;
                if (x < 0 || x >= w || y < 0 || y >= h) continue;
                var match = matches[y * w + x];
                if (match < 0) continue;
                Candidate(match % w - dx, match / w - dy);
            }
            uint state = (uint)(target + 1) * 747796405u + 2891336453u;
            int Pick(int max) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (int)(state % (uint)max); }
            for (var attempt = 0; attempt < 48; attempt++) { var donor = donors[Pick(donors.Count)]; Candidate(donor % w, donor / w); }
            for (var radius = Math.Max(w, h); radius >= 1; radius /= 2)
            {
                if (winner < 0) break;
                for (var attempt = 0; attempt < 3; attempt++)
                    Candidate(winner % w + Pick(radius * 2 + 1) - radius, winner / w + Pick(radius * 2 + 1) - radius);
            }
            if (winner < 0) continue;
            int sx = winner % w, sy = winner / w;
            for (var dy = -Radius; dy <= Radius; dy++)
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                int x = tx + dx, y = ty + dy;
                if (x < 0 || x >= w || y < 0 || y >= h) continue;
                var i = y * w + x;
                if (!selected[i] || known[i] > 0) continue;
                var donor = (sy + dy) * w + sx + dx;
                current[i * 4 + 3] = original[donor * 4 + 3];
                shifts[i * 3] = bestR; shifts[i * 3 + 1] = bestG; shifts[i * 3 + 2] = bestB;
                for (var c = 0; c < 3; c++) current[i * 4 + c] = Math.Clamp(original[donor * 4 + c] + shifts[i * 3 + c], 0, current[i * 4 + 3]);
                matches[i] = donor; known[i] = Math.Max(confidence, 0.01f);
            }
            for (var dy = -Radius - 1; dy <= Radius + 1; dy++)
            for (var dx = -Radius - 1; dx <= Radius + 1; dx++) Enqueue(tx + dx, ty + dy);
        }

        // Disconnected/downsampled cells must not leave remnants of the removed object.
        if (selected.Where((value, i) => value && known[i] == 0).Any()) PushPull.Fill(current, known, w, h, 4);
        var result = Pixels.Clone(source); var dst = (byte*)result.GetPixels();
        double scaleX = (double)bounds.Width / w, scaleY = (double)bounds.Height / h;
        for (var y = hole.Top; y < hole.Bottom; y++)
        for (var x = hole.Left; x < hole.Right; x++)
        {
            var amount = coverage[(long)y * mask.RowBytes + x];
            if (amount == 0) continue;
            int gx = (int)((long)(x - bounds.Left) * w / bounds.Width), gy = (int)((long)(y - bounds.Top) * h / bounds.Height);
            var donor = matches[gy * w + gx];
            int sx = donor < 0 ? x : Math.Clamp(x + (int)Math.Round((donor % w - gx) * scaleX), bounds.Left, bounds.Right - 1),
                sy = donor < 0 ? y : Math.Clamp(y + (int)Math.Round((donor / w - gy) * scaleY), bounds.Top, bounds.Bottom - 1);
            var clean = donor >= 0 && coverage[(long)sy * mask.RowBytes + sx] == 0
                && (avoidance == null || avoidance[(long)sy * donorExclusion!.RowBytes + sx] == 0);
            var a = src + (long)y * source.RowBytes + x * 4; var b = src + (long)sy * source.RowBytes + sx * 4;
            var p = dst + (long)y * result.RowBytes + x * 4;
            for (var c = 0; c < 4; c++)
            {
                var value = clean ? c == 3 ? b[c] : (int)Math.Round(Math.Clamp(b[c] + shifts[(gy * w + gx) * 3 + c], 0, b[3]))
                    : (int)Math.Round(Math.Clamp(current[(gy * w + gx) * 4 + c], 0, c == 3 ? 255 : current[(gy * w + gx) * 4 + 3]));
                p[c] = (byte)((a[c] * (255 - amount) + value * amount + 127) / 255);
            }
        }
        Pixels.Invalidate(result);
        return result;
    }

    private static SKBitmap SmoothFallback(SKBitmap source, SKBitmap mask, SKRectI bounds, int w, int h, float[] values, float[] known)
    {
        // A very narrow/transparent source may have usable boundary colors but no complete donor patch.
        // Keep this rare fallback bounded too: do not allocate full-resolution float buffers for a big hole.
        PushPull.Fill(values, known, w, h, 4);
        var result = Pixels.Clone(source); var dst = (byte*)result.GetPixels(); var coverage = (byte*)mask.GetPixels();
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
        {
            var amount = coverage[(long)y * mask.RowBytes + x];
            if (amount == 0) continue;
            var fx = Math.Clamp((x - bounds.Left + 0.5) * w / bounds.Width - 0.5, 0, w - 1);
            var fy = Math.Clamp((y - bounds.Top + 0.5) * h / bounds.Height - 0.5, 0, h - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            var p = dst + (long)y * result.RowBytes + x * 4;
            for (var c = 0; c < 4; c++)
            {
                var top = values[(y0 * w + x0) * 4 + c] * (1 - (fx - x0)) + values[(y0 * w + x1) * 4 + c] * (fx - x0);
                var bottom = values[(y1 * w + x0) * 4 + c] * (1 - (fx - x0)) + values[(y1 * w + x1) * 4 + c] * (fx - x0);
                var value = Math.Clamp((int)Math.Round(top * (1 - (fy - y0)) + bottom * (fy - y0)), 0, 255);
                p[c] = (byte)((p[c] * (255 - amount) + value * amount + 127) / 255);
            }
        }
        Pixels.Invalidate(result); return result;
    }
}
