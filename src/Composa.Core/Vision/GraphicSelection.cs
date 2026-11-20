using Composa.Rendering;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>Recover separate printed marks and their counters on a flat or smoothly shaded plate.
/// A semantic silhouette alone can bridge letters, digits and the spaces inside them.</summary>
internal static class GraphicSelection
{
    private static float Distance(PixelValue a, PixelValue b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static float Coverage(PixelValue observed, PixelValue background, PixelValue ink)
    {
        var axis = ink - background; var delta = observed - background;
        return Math.Clamp((axis.R * delta.R + axis.G * delta.G + axis.B * delta.B) /
            (axis.R * axis.R + axis.G * axis.G + axis.B * axis.B), 0, 1);
    }

    // A photo can carry a flat printed caption even when the surface behind each letter
    // has a different color. Estimate that local color only along the semantic contour.
    internal static SKBitmap? RefinePrintedEdges(SKBitmap image, SKBitmap matte, SKRectI gesture, CancellationToken cancel)
    {
        var bounds = Selections.SelectionMask.Bounds(matte, 128);
        if (bounds.Width < 12 || bounds.Height < 8 || !gesture.Contains(bounds)) return null;
        var rgb = new PixelBuffer(image);
        var input = matte.GetPixelSpan();
        int step = Math.Max(1, Math.Max(bounds.Width, bounds.Height) / 160);
        var cores = new List<PixelValue>();
        int repeated = 0, rows = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y += step)
        {
            cancel.ThrowIfCancellationRequested();
            int runs = 0; bool previous = false;
            for (int x = bounds.Left; x < bounds.Right; x += step)
            {
                var alpha = input[y * matte.RowBytes + x];
                if (alpha >= 240) cores.Add(rgb.Read(x, y));
                if (alpha >= 128 && !previous) runs++;
                previous = alpha >= 128;
            }
            rows++;
            if (runs >= 3) repeated++;
        }
        if (cores.Count < 24 || repeated < .25 * rows) return null;
        var peak = cores.GroupBy(p => ((int)(p.R * 15), (int)(p.G * 15), (int)(p.B * 15))).MaxBy(g => g.Count())!;
        var ink = new PixelValue(peak.Average(p => p.R), peak.Average(p => p.G), peak.Average(p => p.B), 1);
        if (cores.Count(p => p.A >= .98f && Distance(p, ink) < .06f) < .82 * cores.Count) return null;

        const int radius = 3;
        var region = bounds; region.Inflate(radius, radius);
        region = SKRectI.Intersect(gesture, region);
        var result = Pixels.Clone(matte);
        try
        {
            var output = result.GetPixelSpan();
            for (int y = region.Top; y < region.Bottom; y++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int x = region.Left; x < region.Right; x++)
                {
                    var observed = rgb.Read(x, y);
                    int old = input[y * matte.RowBytes + x];
                    if (observed.A < .98f || old == 255 && Distance(observed, ink) < .025f) continue;
                    float weight = 0; PixelValue background = default; bool hasInk = false;
                    for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx, ny = y + dy, d2 = dx * dx + dy * dy;
                        if (d2 > radius * radius || !gesture.Contains(nx, ny)) continue;
                        byte a = input[ny * matte.RowBytes + nx];
                        if (a >= 176) hasInk = true;
                        if (a > 12) continue;
                        var sample = rgb.Read(nx, ny);
                        if (sample.A < .98f) continue;
                        float w = 1f / (1 + d2);
                        background += sample * w; weight += w;
                    }
                    if (!hasInk || weight == 0) continue;
                    background *= 1 / weight;
                    if (Distance(ink, background) < .22f) continue;
                    float alpha = Coverage(observed, background, ink);
                    float residual = Distance(observed, background + (ink - background) * alpha);
                    // JPEG commonly subsamples chroma. Luma preserves the native edge
                    // position when it separates foreground and background clearly.
                    static float Luma(PixelValue p) => .299f * p.R + .587f * p.G + .114f * p.B;
                    float contrast = Luma(ink - background);
                    if (Math.Abs(contrast) >= .22f)
                        alpha = Math.Clamp(Luma(observed - background) / contrast, 0, 1);
                    float trust = Math.Clamp((.16f - residual) / .10f, 0, 1);
                    output[y * result.RowBytes + x] = (byte)Math.Round(old + trust * (255 * alpha - old));
                }
            }
            Pixels.Invalidate(result);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static SKBitmap? FromBox(SKBitmap image, SKRectI box, CancellationToken cancellation)
    {
        if (box.Width < 12 || box.Height < 12) return null;
        var rgb = new PixelBuffer(image);
        var probes = new List<SKPointI>(128);
        for (int i = 0; i < 32; i++)
        {
            int x = box.Left + (box.Width - 1) * i / 31, y = box.Top + (box.Height - 1) * i / 31;
            probes.Add(new(x, box.Top)); probes.Add(new(x, box.Bottom - 1));
            probes.Add(new(box.Left, y)); probes.Add(new(box.Right - 1, y));
        }
        var border = probes.Select(p => rgb.Read(p.X, p.Y)).ToArray();
        float Median(int channel) => border.Select(p => p[channel]).Order().ElementAt(border.Length / 2);
        var bg = new PixelValue(Median(0), Median(1), Median(2), 1);
        bool uniform = border.Count(p => p.A > .98f && Distance(p, bg) < .07f) >= .92 * border.Length;
        var tl = rgb.Read(box.Left, box.Top); var tr = rgb.Read(box.Right - 1, box.Top);
        var bl = rgb.Read(box.Left, box.Bottom - 1); var br = rgb.Read(box.Right - 1, box.Bottom - 1);
        PixelValue Plate(int x, int y)
        {
            if (uniform) return bg;
            float u = (float)(x - box.Left) / (box.Width - 1), v = (float)(y - box.Top) / (box.Height - 1);
            return (tl * (1 - u) + tr * u) * (1 - v) + (bl * (1 - u) + br * u) * v;
        }
        if (!uniform)
        {
            // Accept changing illumination only if it predicts all four sides, including between corners.
            int matching = 0;
            for (int i = 0; i < probes.Count; i++)
                if (border[i].A > .98f && Distance(border[i], Plate(probes[i].X, probes[i].Y)) < .025f) matching++;
            if (matching < .96 * probes.Count) return null;
        }

        int step = Math.Max(1, Math.Max(box.Width, box.Height) / 160);
        var foreground = new List<PixelValue>();
        int rows = 0, repeated = 0, samples = 0, exposedPlate = 0;
        for (int y = box.Top; y < box.Bottom; y += step)
        {
            cancellation.ThrowIfCancellationRequested();
            bool previous = false; int runs = 0;
            for (int x = box.Left; x < box.Right; x += step)
            {
                var p = rgb.Read(x, y); samples++;
                if (p.A < .98f) return null;
                float contrast = Distance(p, Plate(x, y));
                if (contrast < .025f) exposedPlate++;
                bool mark = contrast > .24f;
                if (mark) { foreground.Add(p); if (!previous) runs++; }
                previous = mark;
            }
            rows++; if (runs >= 2) repeated++;
        }
        if (repeated < rows * .12 || foreground.Count < Math.Max(12, samples * .012) || foreground.Count > samples * .68) return null;
        if (!uniform && exposedPlate < .50 * samples) return null;
        // Illustrations have compact ink clusters. Textured subjects use the semantic matte.
        int Bin(float v) => (int)Math.Round(v * 15);
        var clusters = foreground.GroupBy(p => (Bin(p.R), Bin(p.G), Bin(p.B)))
            .OrderByDescending(g => g.Count())
            .Where(g => g.Count() >= Math.Max(3, foreground.Count * .008))
            .Select(g => new PixelValue(g.Average(p => p.R), g.Average(p => p.G), g.Average(p => p.B), 1)).ToArray();
        var inks = new List<PixelValue>(4);
        foreach (var cluster in clusters)
        {
            if (inks.Any(ink => Distance(cluster, bg + (ink - bg) * Coverage(cluster, bg, ink)) < .065f)) continue;
            inks.Add(cluster);
            if (inks.Count == 4) break;
        }
        if (inks.Count == 0 || foreground.Count(p => inks.Any(ink => Distance(ink, p) < .06f)) < .80 * foreground.Count) return null;

        var result = Pixels.NewMask(image.Width, image.Height);
        try
        {
            var bytes = result.GetPixelSpan();
            for (int y = box.Top; y < box.Bottom; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = box.Left; x < box.Right; x++)
                {
                    var p = rgb.Read(x, y);
                    var background = Plate(x, y);
                    if (Distance(p, background) < (uniform ? .07f : .012f)) continue;
                    float best = float.MaxValue, alpha = 0;
                    foreach (var ink in inks)
                    {
                        if (!uniform && Distance(ink, background) < .08f) continue;
                        float a = Coverage(p, background, ink);
                        float error = Distance(p, background + (ink - background) * a);
                        if (error < best) { best = error; alpha = a; }
                    }
                    if (best < .12f) bytes[y * result.RowBytes + x] = (byte)Math.Round(255 * alpha);
                }
            }
            Pixels.Invalidate(result);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    // The matte can correctly discard the background between letters even if
    // SAM enclosed it. Look for a common ink colour among the remaining cores.
    internal static bool HasDominantInk(SKBitmap image, SKBitmap matte, SKBitmap outline, CancellationToken cancellation)
    {
        var rgb = new PixelBuffer(image);
        var bins = new int[4096];
        bool Concentrated(SKBitmap mask)
        {
            Array.Clear(bins);
            var bytes = mask.GetPixelSpan();
            int total = 0, peak = 0;
            for (int y = 0; y < image.Height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < image.Width; x++)
                {
                    if (bytes[y * mask.RowBytes + x] < 220) continue;
                    var p = rgb.Read(x, y);
                    int Bin(float v) => Math.Clamp((int)(16 * v), 0, 15);
                    int id = (Bin(p.R) << 8) | (Bin(p.G) << 4) | Bin(p.B);
                    if (++bins[id] > bins[peak]) peak = id;
                    total++;
                }
            }
            if (total < 24) return false;
            int nearby = 0;
            for (int i = 0; i < bins.Length; i++)
                if (Math.Abs((i >> 8) - (peak >> 8)) <= 1 && Math.Abs(((i >> 4) & 15) - ((peak >> 4) & 15)) <= 1 &&
                    Math.Abs((i & 15) - (peak & 15)) <= 1) nearby += bins[i];
            return nearby > .68 * total;
        }
        return Concentrated(matte) || Concentrated(outline);
    }
}
