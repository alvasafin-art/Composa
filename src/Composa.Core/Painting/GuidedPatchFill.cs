using System.Numerics;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>Use the continued contours as a search guide, reconstructing from overlapping clean source patches.</summary>
internal static unsafe class GuidedPatchFill
{
    private static readonly (int Dx, int Dy)[] Axes = [(-1, 0), (1, 0), (0, -1), (0, 1)];
    private readonly record struct Match(int Dx, int Dy, float Cost);
    private readonly record struct Tap(int X, int Y, int Index, float Weight);

    public static SKBitmap? TryFill(SKBitmap source, SKBitmap mask, SKBitmap? exclusion, SKRectI hole, CancellationToken cancellation)
    {
        // The full-resolution field is local. Very large removals use the bounded pyramid in ExemplarFill.
        if (Math.Min(hole.Width, hole.Height) < 13 || (long)hole.Width * hole.Height > 256_000) return null;
        var area = hole; area.Inflate(112, 112);
        area = Geometry.Intersect(area, new SKRectI(0, 0, source.Width, source.Height));
        int w = area.Width, h = area.Height;
        if ((long)w * h > 512_000) return null;
        var input = new PixelBuffer(source);
        var selection = (byte*)mask.GetPixels(); var ms = mask.RowBytes;
        var banned = (byte*)(exclusion ?? mask).GetPixels(); var bs = (exclusion ?? mask).RowBytes;
        var colors = new Vector3[w * h]; var weights = new float[w * h]; var selected = new bool[w * h];
        var blocked = new int[(w + 1) * (h + 1)]; var values = new float[w * h * 3];
        int left = hole.Left - area.Left, top = hole.Top - area.Top;
        int right = left + hole.Width, bottom = top + hole.Height;
        int selectedCount = 0;
        for (int y = 0; y < h; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int total = 0, yy = y + area.Top;
            for (int x = 0; x < w; x++)
            {
                int xx = x + area.Left, i = y * w + x;
                var c = input.Read(xx, yy);
                colors[i] = new(c.R, c.G, c.B);
                selected[i] = selection[(long)yy * ms + xx] != 0;
                if (selected[i] && (c.A < .999f || ++selectedCount > 96_000)) return null;
                if (c.A < .999f || banned[(long)yy * bs + xx] != 0 || selected[i]) total++;
                else
                {
                    weights[i] = 1; values[3 * i] = c.R; values[3 * i + 1] = c.G; values[3 * i + 2] = c.B;
                }
                blocked[(y + 1) * (w + 1) + x + 1] = blocked[y * (w + 1) + x + 1] + total;
            }
        }
        if (!weights.Contains(1f)) return null;
        PushPull.Fill(values, weights, w, h, 3);
        var guide = (Vector3[])colors.Clone();
        var known = new bool[w * h];
        for (int i = 0; i < guide.Length; i++)
        {
            known[i] = weights[i] > 0;
            if (selected[i]) guide[i] = new(values[3 * i], values[3 * i + 1], values[3 * i + 2]);
        }
        var confidence = new float[w * h];
        DirectionalGuide.Fill(colors, guide, confidence, known, selected, w, h, cancellation);
        var structure = (Vector3[])guide.Clone();

        var region = new SKRectI(left, top, right, bottom);
        var textureDonor = new int[w * h];
        if (!MatchPatches(region, w, h, colors, structure, confidence, selected, known, blocked, guide, textureDonor, cancellation)) return null;
        var residual = BoundaryCorrection(region, w, h, colors, guide, selected, known, cancellation);
        var synthesized = FuseStructure(region, w, h, colors, structure, confidence, selected, known, guide, residual, cancellation);
        return TransferDetail(source, mask, area, region, colors, selected, known, synthesized, textureDonor, cancellation);
    }

    private static bool MatchPatches(SKRectI region, int w, int h, Vector3[] colors, Vector3[] structure, float[] confidence,
        bool[] selected, bool[] known, int[] blocked, Vector3[] guide, int[] textureDonor, CancellationToken cancellation)
    {
        int Blocked(int x0, int y0, int x1, int y1) => blocked[y1 * (w + 1) + x1] - blocked[y0 * (w + 1) + x1]
            - blocked[y1 * (w + 1) + x0] + blocked[y0 * (w + 1) + x0];

        var matches = new Match[w * h];
        var sum = new Vector3[w * h]; var votes = new float[w * h];
        var strongest = new float[w * h];
        var taps = new Tap[130];
        foreach (int radius in new[] { 12, 6 })
        {
            int step = radius, sampleStep = radius / 3;
            for (int pass = 0; pass < 2; pass++)
            {
                bool reverse = pass == 1;
                int nx = (region.Width + 2 * step - 1) / step, ny = (region.Height + 2 * step - 1) / step;
                for (int row = 0; row < ny; row++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (int col = 0; col < nx; col++)
                    {
                        int cx = region.Left + (reverse ? nx - 1 - col : col) * step, cy = region.Top + (reverse ? ny - 1 - row : row) * step;
                        if (cx >= w || cy >= h) continue;
                        int center = cy * w + cx;
                        int x0 = Math.Max(0, cx - radius), x1 = Math.Min(w - 1, cx + radius);
                        int y0 = Math.Max(0, cy - radius), y1 = Math.Min(h - 1, cy + radius);
                        if (Blocked(x0, y0, x1 + 1, y1 + 1) == 0) continue;
                        int count = 0; float total = 0;
                        for (int dy = -radius; dy <= radius; dy += sampleStep)
                            for (int dx = -radius; dx <= radius; dx += sampleStep)
                            {
                                int x = cx + dx, y = cy + dy;
                                if ((uint)x >= w || (uint)y >= h) continue;
                                int i = y * w + x;
                                if (!selected[i] && !known[i]) continue;
                                float weight = selected[i] ? .5f : 4f;
                                taps[count++] = new(x, y, i, weight); total += weight;
                            }
                        int innerCount = count;
                        for (int dy = -4 * radius; dy <= 4 * radius; dy += radius)
                            for (int dx = -4 * radius; dx <= 4 * radius; dx += radius)
                            {
                                int x = cx + dx, y = cy + dy;
                                if ((uint)x >= w || (uint)y >= h || Math.Max(Math.Abs(dx), Math.Abs(dy)) <= radius) continue;
                                int i = y * w + x; if (!known[i]) continue;
                                float weight = 3f * radius * radius / (dx * dx + dy * dy);
                                taps[count++] = new(x, y, i, weight); total += weight;
                            }
                        if (count == 0) continue;
                        var best = new Match(0, 0, float.MaxValue);
                        void Consider(int dx, int dy)
                        {
                            if (x0 + dx < 0 || y0 + dy < 0 || x1 + dx >= w || y1 + dy >= h || Blocked(x0 + dx, y0 + dy, x1 + dx + 1, y1 + dy + 1) != 0) return;
                            int delta = dy * w + dx;
                            float cost = total * 1e-8f * (dx * dx + dy * dy);
                            for (int k = 0; k < count; k++)
                            {
                                var tap = taps[k];
                                int donor = tap.Index + delta;
                                if (k >= innerCount && ((uint)(tap.X + dx) >= w || (uint)(tap.Y + dy) >= h || !known[donor]))
                                    cost += tap.Weight * .012f;
                                else cost += tap.Weight * Math.Min(.25f, Vector3.DistanceSquared(guide[tap.Index], colors[donor]));
                                if (cost >= best.Cost) return;
                            }
                            best = new(dx, dy, cost);
                        }
                        void Propagate(int x, int y)
                        {
                            if ((uint)x >= w || (uint)y >= h) return;
                            var m = matches[y * w + x];
                            if (m.Dx != 0 || m.Dy != 0) Consider(m.Dx, m.Dy);
                        }
                        Propagate(cx, cy); Propagate(cx - step, cy); Propagate(cx + step, cy); Propagate(cx, cy - step); Propagate(cx, cy + step);
                        if (!reverse)
                        {
                            for (int dy = -96; dy <= 96; dy += 12)
                                for (int dx = -96; dx <= 96; dx += 12) Consider(dx, dy);
                        }
                        for (int stride = 6; stride >= 1; stride /= 2)
                        {
                            var seed = best;
                            for (int iy = -1; iy <= 1; iy++) for (int ix = -1; ix <= 1; ix++) Consider(seed.Dx + ix * stride, seed.Dy + iy * stride);
                        }
                        matches[center] = best;
                        if (!reverse || best.Cost == float.MaxValue) continue;
                        float quality = 1 / (.0005f + best.Cost / total);
                        int shift = best.Dy * w + best.Dx;
                        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                        {
                            int i = y * w + x;
                            float weight = quality * (radius + 1 - Math.Abs(x - cx)) * (radius + 1 - Math.Abs(y - cy));
                            var diff = colors[i + shift] - structure[i];
                            float luma = Vector3.Dot(diff, new Vector3(.2126f, .7152f, .0722f));
                            float chroma = (diff - new Vector3(luma)).LengthSquared();
                            weight /= 1 + chroma * 1000 + confidence[i] * 90 * luma * luma;
                            weight *= weight;
                            sum[i] += colors[i + shift] * weight; votes[i] += weight;
                            if (radius == 6 && weight > strongest[i])
                            {
                                strongest[i] = weight;
                                textureDonor[i] = i + shift;
                            }
                        }
                    }
                }
            }
            for (int y = Math.Max(0, region.Top - 1); y < Math.Min(h, region.Bottom + 1); y++) for (int x = Math.Max(0, region.Left - 1); x < Math.Min(w, region.Right + 1); x++)
            {
                int i = y * w + x;
                if (votes[i] > 0 && (selected[i] || radius == 6)) guide[i] = sum[i] / votes[i];
                else if (selected[i] && radius == 6) return false;
            }
            Array.Clear(sum); Array.Clear(votes);
        }
        return true;
    }

    private static Vector3[] BoundaryCorrection(SKRectI region, int w, int h, Vector3[] colors, Vector3[] guide,
        bool[] selected, bool[] known, CancellationToken cancellation)
    {
        // Fit a screened harmonic residual from the patch prediction on the intact rim.
        // The residual decays into the interior, leaving the donor's grain in place.
        var residual = new Vector3[w * h];
        for (int y = Math.Max(0, region.Top - 1); y < Math.Min(h, region.Bottom + 1); y++) for (int x = Math.Max(0, region.Left - 1); x < Math.Min(w, region.Right + 1); x++)
        {
            int i = y * w + x;
            if (known[i]) residual[i] = colors[i] - guide[i];
        }
        for (int iter = 0; iter < 45; iter++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int parity = 0; parity < 2; parity++)
                for (int y = region.Top; y < region.Bottom; y++)
                    for (int x = region.Left + ((y + region.Left + parity) & 1); x < region.Right; x += 2)
                    {
                        int i = y * w + x; if (!selected[i]) continue;
                        Vector3 delta = default; int n = 0;
                        void Add(int j) { if (!selected[j] && !known[j]) return; delta += residual[j]; n++; }
                        if (x > 0) Add(i - 1); if (x < w - 1) Add(i + 1); if (y > 0) Add(i - w); if (y < h - 1) Add(i + w);
                        residual[i] = delta / (n + .035f);
                    }
        }
        return residual;
    }

    private static Vector3[] FuseStructure(SKRectI region, int w, int h, Vector3[] colors, Vector3[] structure, float[] confidence,
        bool[] selected, bool[] known, Vector3[] guide, Vector3[] residual, CancellationToken cancellation)
    {
        var synthesized = (Vector3[])colors.Clone();
        for (int y = region.Top; y < region.Bottom; y++) for (int x = region.Left; x < region.Right; x++)
        {
            if (x == region.Left) cancellation.ThrowIfCancellationRequested();
            int i = y * w + x; if (!selected[i]) continue;
            var c = Vector3.Lerp(guide[i] + residual[i], structure[i], Math.Clamp(1.4f * confidence[i], 0, 1));
            var diff = c - structure[i];
            float luma = Vector3.Dot(diff, new Vector3(.2126f, .7152f, .0722f));
            var chroma = diff - new Vector3(luma);
            c -= chroma * Math.Max(0, 1 - .030f / (chroma.Length() + .00001f));
            c = Vector3.Clamp(c, Vector3.Zero, Vector3.One);
            Vector3 rim = default; float rimWeight = 0; int nearest = 10;
            foreach (var (dx, dy) in Axes)
            {
                for (int k = 1; k < 9; k++)
                {
                    int sx = x + k * dx, sy = y + k * dy;
                    if ((uint)sx >= w || (uint)sy >= h) break;
                    int j = sy * w + sx;
                    if (selected[j]) continue;
                    if (!known[j]) break;
                    int ex = sx + dx * Math.Min(k, 4), ey = sy + dy * Math.Min(k, 4);
                    var v = colors[j];
                    if ((uint)ex < w && (uint)ey < h && known[ey * w + ex])
                    {
                        var change = colors[ey * w + ex] - v;
                        v += change * Math.Min(1, .06f / (.00001f + change.Length()));
                    }
                    float wt = 1f / (k * k * (1 + 100 * Vector3.DistanceSquared(v, c)));
                    rim += wt * v; rimWeight += wt; nearest = Math.Min(nearest, k); break;
                }
            }
            if (rimWeight > 0) c = Vector3.Lerp(c, rim / rimWeight, .82f * (1 - nearest / 9f) * (1 - nearest / 9f));
            synthesized[i] = c;
        }
        return synthesized;
    }

    private static SKBitmap TransferDetail(SKBitmap source, SKBitmap mask, SKRectI area, SKRectI region, Vector3[] colors,
        bool[] selected, bool[] known, Vector3[] synthesized, int[] textureDonor, CancellationToken cancellation)
    {
        int w = area.Width, h = area.Height;
        var input = new PixelBuffer(source); var selection = (byte*)mask.GetPixels(); int ms = mask.RowBytes;
        cancellation.ThrowIfCancellationRequested();
        var result = Pixels.Clone(source); var dst = new PixelBuffer(result);
        for (int y = region.Top; y < region.Bottom; y++) for (int x = region.Left; x < region.Right; x++)
        {
            int i = y * w + x; if (!selected[i]) continue;
            int donor = textureDonor[i], sx = donor % w, sy = donor / w;
            Vector3 candidate = synthesized[i], sumTarget = default, sumSource = default; float total = 0;
            for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
            {
                if ((uint)(sx + dx) >= w || (uint)(sy + dy) >= h || (uint)(x + dx) >= w || (uint)(y + dy) >= h) continue;
                int j = i + dy * w + dx, k = donor + dy * w + dx;
                if (!known[k] || !selected[j] && !known[j]) continue;
                float weight = (3 - Math.Abs(dx)) * (3 - Math.Abs(dy)) / (1 + 220 * Vector3.DistanceSquared(candidate, synthesized[j]));
                sumTarget += weight * synthesized[j]; sumSource += weight * colors[k]; total += weight;
            }
            if (total > 0)
            {
                var detail = colors[donor] - sumSource / total;
                candidate = Vector3.Lerp(candidate, sumTarget / total + .85f * detail * Math.Min(1, .055f / (.00001f + detail.Length())), .78f);
            }
            int xx = x + area.Left, yy = y + area.Top;
            float coverage = selection[(long)yy * ms + xx] / 255f;
            var c = Vector3.Clamp(candidate, Vector3.Zero, Vector3.One);
            dst.Write(xx, yy, input.Read(xx, yy) * (1 - coverage) + new PixelValue(c.X, c.Y, c.Z, 1) * coverage);
        }
        Pixels.Invalidate(result); return result;
    }
}
