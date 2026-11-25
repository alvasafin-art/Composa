using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Vision;

internal static class FineStrandGaps
{
    private const int Reach = 14;
    private const int Pitch = 8;

    internal static void Refine(SKBitmap image, SKBitmap matte, CancellationToken cancellation)
    {
        var bounds = SelectionMask.Bounds(matte, 128);
        // Search at native resolution, where the interior bands span a few pixels.
        if (Math.Min(bounds.Width, bounds.Height) is < 12 or > 128 || Math.Max(bounds.Width, bounds.Height) > 320)
            return;

        var canvas = SKRectI.Create(image.Width, image.Height);
        var area = bounds;
        area.Inflate(2 * Reach, 2 * Reach);
        area = SKRectI.Intersect(area, canvas);
        var rgb = new PixelBuffer(image);
        var colors = new PixelValue[area.Width * area.Height];
        var original = new byte[colors.Length];
        var proposed = new byte[colors.Length];
        var alpha = matte.GetPixelSpan();
        int Index(int x, int y) => (y - area.Top) * area.Width + x - area.Left;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = area.Left; x < area.Right; x++)
            {
                int i = Index(x, y);
                colors[i] = rgb.Read(x, y).Straight;
                proposed[i] = original[i] = alpha[y * matte.RowBytes + x];
            }
        }

        var exterior = new List<PixelValue>();
        var recoverable = new byte[colors.Length];
        for (int cy = bounds.Top; cy < bounds.Bottom; cy += Pitch)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int cx = bounds.Left; cx < bounds.Right; cx += Pitch)
            {
                var tile = SKRectI.Intersect(area, new(cx - Reach, cy - Reach, cx + Reach + 1, cy + Reach + 1));
                var samples = SKRectI.Intersect(area, new(cx - 2 * Reach, cy - 2 * Reach, cx + 2 * Reach + 1, cy + 2 * Reach + 1));
                exterior.Clear();
                for (int y = samples.Top; y < samples.Bottom; y += 2)
                    for (int x = samples.Left; x < samples.Right; x += 2)
                    {
                        int i = Index(x, y);
                        var p = colors[i];
                        if (original[i] <= 6 && p.A >= .99f && Light(p) >= .64f)
                            exterior.Add(p);
                    }
                if (exterior.Count < 20) continue;
                // A textured surface can vary in brightness. Use the lighter samples
                // so excess shadow does not identify the strands as background.
                exterior.Sort((a, b) => Light(b).CompareTo(Light(a)));
                int keep = Math.Max(16, exterior.Count / 2);
                var lightSamples = exterior.GetRange(0, keep);
                if (ForegroundCoverage.ColourPlate(lightSamples, minimumChroma: .10f) is not { } backdrop) continue;

                float GapCoverage(int i)
                {
                    var pixel = colors[i];
                    float light = Light(pixel);
                    float distance = Distance(pixel, backdrop);
                    if (light >= .72f && distance <= .19f)
                        return Math.Max(0, (distance - .07f) / .44f);
                    // Compare hue at the same brightness where a narrow cast
                    // shadow darkens the backdrop between otherwise pale gaps.
                    if (light < .55f || light > Light(backdrop)) return 1;
                    float residual = Distance(pixel, backdrop * (light / Light(backdrop)));
                    return residual <= .067f ? Math.Max(0, (residual - .028f) / .16f) : 1;
                }

                bool IsStrand(int i) => original[i] >= 224 && Light(colors[i]) <= Light(backdrop) - .28f;

                int CountRuns(bool vertical)
                {
                    int consistent = 0;
                    for (int v = vertical ? tile.Left : tile.Top; v < (vertical ? tile.Right : tile.Bottom); v++)
                    {
                        int previousGap = -100, strandWidth = 0, runs = 0;
                        for (int u = vertical ? tile.Top : tile.Left; u < (vertical ? tile.Bottom : tile.Right); u++)
                        {
                            int i = vertical ? Index(v, u) : Index(u, v);
                            if (GapCoverage(i) < 1)
                            {
                                if (strandWidth is >= 1 and <= 7 && u - previousGap <= 10) runs++;
                                previousGap = u;
                                strandWidth = 0;
                            }
                            else if (IsStrand(i)) strandWidth++;
                        }
                        if (runs >= 2) consistent++;
                    }
                    return consistent;
                }

                if (CountRuns(true) < 4 && CountRuns(false) < 4) continue;
                for (int y = tile.Top; y < tile.Bottom; y++)
                    for (int x = tile.Left; x < tile.Right; x++)
                    {
                        int i = Index(x, y);
                        float coverage = GapCoverage(i);
                        if (coverage >= 1) continue;
                        // Keep the strongest local colour match where tiles overlap.
                        recoverable[i] = 1;
                        byte limit = (byte)MathF.Round(255 * coverage);
                        proposed[i] = Math.Min(proposed[i], limit);
                    }
            }
        }

        // Only open narrow gaps reachable from the exterior. Bright markings
        // enclosed by the selected object remain part of the object.
        var queue = new Queue<int>();
        for (int i = 0; i < original.Length; i++)
            if (original[i] <= 6 && recoverable[i] != 0)
            {
                queue.Enqueue(i);
                recoverable[i] = 2;
            }
        while (queue.TryDequeue(out int i))
        {
            int x = i % area.Width, y = i / area.Width;
            if (x > 0) Visit(i - 1);
            if (x < area.Width - 1) Visit(i + 1);
            if (y > 0) Visit(i - area.Width);
            if (y < area.Height - 1) Visit(i + area.Width);
        }

        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                int i = Index(x, y);
                if (recoverable[i] == 2) alpha[y * matte.RowBytes + x] = proposed[i];
            }
        }
        Pixels.Invalidate(matte);

        void Visit(int index)
        {
            if (recoverable[index] != 1) return;
            recoverable[index] = 2;
            queue.Enqueue(index);
        }
    }

    private static float Light(PixelValue p) => Math.Max(p.R, Math.Max(p.G, p.B));
    private static float Distance(PixelValue a, PixelValue b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
}
