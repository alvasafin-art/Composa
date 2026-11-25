using SkiaSharp;
using Composa.Rendering;
using Composa.Selections;

namespace Composa.Vision;

internal static class PointMaskCandidates
{
    // Compare the same connected subject under two different prompt types.
    // A point often ranks icing, a label, or a hole above the object carrying it.
    // A second box on each proposed extent must independently prefer the whole.
    internal static int CheckExtent(float[] masks, float[] scores, int best, int width, int height, SKPointI click,
        Func<SKRectI, (float[] Mask, float Score)?> predictBox, CancellationToken cancel)
    {
        int size = width * height, hit = click.Y * width + click.X;
        var counts = new int[scores.Length];
        for (int i = 0; i < counts.Length; i++)
        {
            if (masks[i * size + hit] <= 0) continue;
            for (int y = 0; y < height; y += 4)
            {
                cancel.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x += 4) if (masks[i * size + y * width + x] > 0) counts[i]++;
            }
        }
        if (counts[best] < 8) return best;
        // Decoder planes can include disconnected neighbours. Derive the box
        // only from the component that will actually be committed for this tap.
        SKBitmap Component(int id)
        {
            using var sampled = Pixels.NewMask((width + 1) / 2, (height + 1) / 2);
            var bytes = sampled.GetPixelSpan();
            for (int y = 0; y < sampled.Height; y++)
            {
                cancel.ThrowIfCancellationRequested();
                int start = id * size + Math.Min(height - 1, 2 * y + (click.Y & 1)) * width;
                for (int x = 0; x < sampled.Width; x++)
                    bytes[y * sampled.RowBytes + x] = masks[start + Math.Min(width - 1, x * 2 + (click.X & 1))] > 0 ? (byte)255 : (byte)0;
            }
            return ObjectSelection.ConnectedPiece(sampled, click.X / 2, click.Y / 2, cancellation: cancel);
        }
        SKRectI Extent(SKBitmap part)
        {
            var r = SelectionMask.Bounds(part);
            r = new(2 * r.Left, 2 * r.Top, 2 * r.Right, 2 * r.Bottom);
            int margin = Math.Max(2, Math.Min(r.Width, r.Height) / 24);
            r.Inflate(margin, margin);
            return SKRectI.Intersect(SKRectI.Create(width, height), r);
        }
        double Agreement(SKBitmap component, float[] prediction)
        {
            int union = 0, overlap = 0;
            var pixels = component.GetPixelSpan();
            for (int y = 0; y < component.Height; y++)
            {
                cancel.ThrowIfCancellationRequested();
                int start = Math.Min(height - 1, y * 2 + (click.Y & 1)) * width;
                for (int x = 0; x < component.Width; x++)
                {
                    bool f = pixels[y * component.RowBytes + x] != 0;
                    bool g = prediction[start + Math.Min(width - 1, 2 * x + (click.X & 1))] > 0;
                    if (f || g) union++;
                    if (f && g) overlap++;
                }
            }
            return (double)overlap / Math.Max(1, union);
        }
        // Sometimes the three point hypotheses are a small item, its group
        // and a container occupying much of the photo. A confident box on the
        // small item can resolve the tap without switching to the container.
        var ranked = Enumerable.Range(0, counts.Length).OrderBy(i => counts[i]).ToArray();
        if (ranked.Length == 3 && ranked[2] == best && counts[best] * 16L > .35 * size &&
            counts[ranked[0]] >= 8 && counts[ranked[1]] > 3 * counts[ranked[0]] &&
            counts[ranked[1]] < .50 * counts[best] && counts[ranked[0]] < .08 * counts[best])
        {
            int item = ranked[0];
            using var part = Component(item);
            if (predictBox(Extent(part)) is { } isolated && isolated.Score >= Math.Max(.88f, scores[best] + .08f) &&
                Agreement(part, isolated.Mask) >= .70) return item;
        }
        float? innerScore = null;
        foreach (int i in Enumerable.Range(0, scores.Length).OrderByDescending(i => counts[i]))
        {
            if (counts[i] < 1.5 * counts[best] || counts[i] > 64 * counts[best] || scores[i] < .40f) continue;
            using var part = Component(best);
            using var outer = Component(i);
            var foreground = outer.GetPixelSpan(); var inner = part.GetPixelSpan();
            int shared = 0, area = 0, small = 0;
            for (int j = 0; j < foreground.Length; j++)
            {
                if (foreground[j] != 0) area++;
                if (inner[j] == 0) continue;
                small++;
                if (foreground[j] != 0) shared++;
            }
            if (shared < .94 * small || area < 1.5 * small || area > .25 * width * height / 4) continue;
            if (predictBox(Extent(outer)) is not { } box || box.Score < .75f) continue;
            if (Agreement(outer, box.Mask) < .90) continue;
            innerScore ??= predictBox(Extent(part))?.Score;
            if (innerScore.HasValue && box.Score >= .95f && box.Score >= innerScore.Value + .025f) return i;
            // A point may strongly recognize the part because it is locally more
            // distinctive. Two prompt types can still agree on a substantial body.
            if (area >= .12 * width * height / 4 && area >= 4 * small &&
                scores[i] >= .75f && scores[i] >= scores[best] - .12f) return i;
        }
        return best;
    }

    // A tap on the end of a solid object may rank the end face above the body.
    // Two agreeing alternatives and a second tap on the body must corroborate it.
    internal static int Resolve(float[] masks, float[] scores, int best, int width, int height, SKPointI click,
        Func<SKPointI, float[]?> confirm, CancellationToken cancel)
    {
        if (scores.Length != 3 || !SKRectI.Create(width, height).Contains(click.X, click.Y)) return best;
        int a = (best + 1) % 3, b = (best + 2) % 3, size = width * height;
        if (Math.Min(scores[a], scores[b]) < scores[best] - .18f) return best;
        int hit = click.Y * width + click.X;
        if (masks[a * size + hit] <= 0 || masks[b * size + hit] <= 0) return best;

        int common = 0, either = 0, small = 0, nested = 0;
        long sumX = 0, sumY = 0;
        int left = width, right = 0, top = height, bottom = 0;
        for (int y = 0; y < height; y += 2)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x += 2)
            {
                int index = y * width + x;
                bool original = masks[best * size + index] > 0;
                bool first = masks[a * size + index] > 0, second = masks[b * size + index] > 0;
                if (original) small++;
                if (first || second) either++;
                if (!first || !second) continue;
                common++;
                if (original) nested++;
                else { sumX += x; sumY += y; }
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        }
        if (small < 8 || common < 2 * small || common > 14 * small || common < .82 * either || nested < .94 * small) return best;
        double cx = (double)sumX / (common - nested), cy = (double)sumY / (common - nested);
        double radius = Math.Max(right - left, bottom - top);
        if (Math.Pow(cx - click.X, 2) + Math.Pow(cy - click.Y, 2) < .08 * radius * radius) return best;

        // The centroid itself may fall in a hole or on an occluder.
        var location = click;
        double nearest = double.MaxValue;
        for (int y = top; y <= bottom; y += 2)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = left; x <= right; x += 2)
            {
                int i = y * width + x;
                if (masks[a * size + i] <= 0 || masks[b * size + i] <= 0 || masks[best * size + i] > 0) continue;
                double distance = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (distance < nearest) { location = new(x, y); nearest = distance; }
            }
        }
        if (confirm(location) is not { } whole || whole[hit] <= 0) return best;
        int candidate = scores[a] >= scores[b] ? a : b;
        int intersection = 0, union = 0;
        for (int y = 0; y < height; y += 2)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x += 2)
            {
                int i = y * width + x;
                bool f = whole[i] > 0, g = masks[candidate * size + i] > 0;
                if (f || g) union++;
                if (f && g) intersection++;
            }
        }
        return intersection >= .85 * union ? candidate : best;
    }
}
