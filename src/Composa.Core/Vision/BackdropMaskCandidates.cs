using SkiaSharp;

namespace Composa.Vision;

internal static class BackdropMaskCandidates
{
    // The full-image decoder can distinguish the backdrop more clearly than the
    // tipped or boxed subject. Trust its inverse only with positive prompt evidence.
    internal static float[]? Recover(float[] backdrop, float[] scores, int width, int height,
        float[]? foreground, SKRectI? requested, Func<SKPointI, (float[] Planes, float[] Scores)> confirm,
        CancellationToken cancellation)
    {
        int size = width * height;
        var frame = SKRectI.Create(width, height);
        int step = Math.Max(1, Math.Min(width, height) / 100);
        var positive = foreground;
        var extent = positive == null ? frame : Bounds(positive, width, height, cancellation);
        if (extent.Width < .30 * width || extent.Height < .36 * height) return null;
        float[]? best = null;
        float bestSupport = float.NegativeInfinity;

        foreach (int candidate in Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]))
        {
            if (scores[candidate] < .70f) continue;
            int offset = candidate * size;
            int margin = step * 3;
            int CountSide(bool vertical, int v)
            {
                int hits = 0, samples = 0;
                for (int u = margin; u < (vertical ? height : width) - margin; u += step)
                {
                    int x = vertical ? v : u, y = vertical ? u : v;
                    if (backdrop[offset + y * width + x] > 0) hits++;
                    samples++;
                }
                return hits >= .78 * samples ? 1 : 0;
            }
            if (CountSide(false, margin) + CountSide(false, height - 1 - margin) +
                CountSide(true, margin) + CountSide(true, width - 1 - margin) < 3) continue;

            if (foreground != null)
            {
                Evaluate(foreground, extent, offset, scores[candidate]);
                continue;
            }
            // Aim into a broad interior of the excluded silhouette. A thin logo
            // or border has no such interior. The second prompt decides its extent.
            SKPointI? seed = null;
            float bestDistance = float.MaxValue;
            for (int y = margin * 2; y < height - margin * 2; y += step * 2)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = margin * 2; x < width - margin * 2; x += step * 2)
                {
                    int p = offset + y * width + x;
                    if (backdrop[p] >= 0 || backdrop[p - margin] >= 0 || backdrop[p + margin] >= 0 ||
                        backdrop[p - margin * width] >= 0 || backdrop[p + margin * width] >= 0) continue;
                    float distance = MathF.Pow((float)x / width - .5f, 2) + MathF.Pow((float)y / height - .55f, 2);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    seed = new(x, y);
                }
            }
            if (seed is not { } point) continue;
            var (planes, confidence) = confirm(point);
            foreach (int i in Enumerable.Range(0, confidence.Length).OrderByDescending(i => confidence[i]))
            {
                if (confidence[i] < .66f || planes[i * size + point.Y * width + point.X] <= 0) continue;
                var proposed = planes.AsSpan(i * size, size).ToArray();
                var bounds = Bounds(proposed, width, height, cancellation);
                if (bounds.Width >= .24 * width && bounds.Height >= .30 * height &&
                    Evaluate(proposed, bounds, offset, scores[candidate])) break;
            }
        }
        return best;

        bool Evaluate(float[] anchor, SKRectI bounds, int offset, float confidence)
        {
            int a = 0, b = 0, both = 0;
            var region = bounds;
            region.Inflate(Math.Max(3, bounds.Width / 9), Math.Max(3, bounds.Height / 5));
            region = SKRectI.Intersect(region, frame);
            if (requested is { } box) region = SKRectI.Intersect(region, SKRectI.Union(bounds, box));
            for (int y = 0; y < height; y += step)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x += step)
                {
                    int p = y * width + x;
                    bool f = anchor[p] > 0;
                    bool g = backdrop[offset + p] < 0 && region.Contains(x, y);
                    if (f) a++;
                    if (g) b++;
                    if (f && g) both++;
                }
            }
            int added = b - both;
            if (a * step * step < .06 * size || a * step * step > .50 * size ||
                both < .87 * a || b > 1.31 * a || foreground != null && added < .012 * a) return false;
            // Prefer corroborated coverage. Penalize a large expansion that may
            // include surrounding graphics or floor along the edge of the image.
            float growth = (float)added / a;
            float support = confidence + .22f * Math.Min(growth, .17f) - .48f * Math.Max(0, growth - .17f);
            if (support <= bestSupport) return true;
            bestSupport = support;

            var result = best = (float[])anchor.Clone();
            for (int y = region.Top; y < region.Bottom; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                // An inferred extension should have an edge within the search
                // region. Keep open runs on the original mask, for example when
                // the inverse includes a strip of floor touching the subject.
                int row = y * width;
                int left = region.Left, right = region.Right;
                if (left > 0 && backdrop[offset + row + left - 1] < 0)
                    while (left < right && anchor[row + left] <= 0 && backdrop[offset + row + left] < 0) left++;
                if (right < width && backdrop[offset + row + right] < 0)
                    while (left < right && anchor[row + right - 1] <= 0 && backdrop[offset + row + right - 1] < 0) right--;
                for (int x = left; x < right; x++)
                {
                    int p = row + x;
                    result[p] = Math.Max(result[p], -backdrop[offset + p]);
                }
            }
            return true;
        }
    }

    internal static SKRectI Bounds(float[] mask, int width, int height, CancellationToken cancellation)
    {
        int left = width, top = height, right = 0, bottom = 0;
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                if (mask[y * width + x] <= 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }
        return right == 0 ? SKRectI.Empty : new(left, top, right, bottom);
    }
}
