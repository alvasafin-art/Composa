using SkiaSharp;
using Composa.Rendering;
using Composa.Selections;

namespace Composa.Vision;

internal static class BoxMaskCandidates
{
    // A box around an assembly can produce separate predictions for its rim and
    // its contents. Use the alternatives only when their geometry supports the gesture.
    internal static float[] Resolve(float[] masks, float[] scores, int best, int width, int height,
        SKRectI prompt, CancellationToken cancellation, Func<float[]?>? interiorCandidate = null,
        Func<float[]?>? guidedCandidate = null, Func<SKRectI, float[]?>? reframe = null)
    {
        int size = width * height;
        var result = masks.AsSpan(best * size, size).ToArray();
        var box = SKRectI.Intersect(SKRectI.Create(width, height), prompt);
        if (box.Width < 8 || box.Height < 8 || scores.Length < 2) return result;

        int step = Math.Max(1, Math.Min(box.Width, box.Height) / 64);
        var primary = new Footprint(result, 0, width, box, step, cancellation);
        if (primary.Count == 0) return result;
        bool DescribesAssembly(float[] planes, int offset, Footprint alternative, bool useJointBounds)
        {
            var rim = primary.Count < alternative.Count ? primary : alternative;
            var contents = rim == primary ? alternative : primary;
            // A loose drag includes table/background around the subject. Once a
            // separate point probe confirms its interior, measure occupancy in
            // the hypotheses' joint bounds instead of the pointer rectangle.
            int samples = useJointBounds ? rim.JointBoundingArea(contents) : rim.Samples;
            if (rim.Count >= .45 * samples || contents.Count <= .48 * samples) return false;
            int shared = rim.Shared(contents), added = contents.Count - shared;
            if (alternative.Count - shared < .035 * primary.Count || added <= .55 * rim.Count || shared <= .15 * rim.Count ||
                rim.EnclosedAdditions(contents) <= .86 * added) return false;
            if (OutsideFraction(planes, offset, width, height, box, step, alternative.Count, cancellation) >= .025 ||
                OutsideFraction(result, 0, width, height, box, step, primary.Count, cancellation) >= .025) return false;
            return true;
        }
        void Merge(float[] planes, int offset)
        {
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int j = y * width; j < (y + 1) * width; j++)
                    result[j] = Math.Max(result[j], planes[offset + j]);
            }
        }
        int inside = primary.Count;
        int? tighter = null;
        double bestSpill = -1;
        foreach (int i in Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]))
        {
            if (i == best || scores[i] < scores[best] - .15f) continue;
            int offset = i * size;
            var alternative = new Footprint(masks, offset, width, box, step, cancellation);
            if (DescribesAssembly(masks, offset, alternative, false)) { Merge(masks, offset); return result; }
            int shared = primary.Shared(alternative), area = alternative.Count;
            // Two touching subjects can also be returned as one. A substantially
            // smaller nested alternative must fit the gesture appreciably better.
            if (area < .45 * inside || area > .8 * inside || shared < .95 * area ||
                scores[i] < scores[best] - .07f) continue;
            if (bestSpill < 0) bestSpill = OutsideFraction(masks, best * size, width, height, box, step, inside, cancellation);
            if (bestSpill < .015) continue;
            double spill = OutsideFraction(masks, offset, width, height, box, step, area, cancellation);
            if (spill < bestSpill / 4)
            {
                tighter = i;
                bestSpill = spill;
            }
        }
        if (tighter.HasValue) return masks.AsSpan(tighter.Value * size, size).ToArray();
        // A box with generous margins can favour the empty rim by a wide score
        // margin. Confirm the proposed contents with a second box-and-point prompt.
        if (guidedCandidate != null && inside < .30 * primary.Samples && result[box.MidY * width + box.MidX] <= 0)
        {
            foreach (int i in Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]))
            {
                if (i == best) continue;
                int offset = i * size;
                var alternative = new Footprint(masks, offset, width, box, step, cancellation);
                if (alternative.Count < 2 * inside || !DescribesAssembly(masks, offset, alternative, true)) continue;
                if (guidedCandidate() is not { } guided) break;
                var confirmation = new Footprint(guided, 0, width, box, step, cancellation);
                int overlap = confirmation.Shared(alternative);
                if (overlap >= .75 * (confirmation.Count + alternative.Count - overlap))
                {
                    Merge(masks, offset);
                    return result;
                }
            }
        }
        if (inside < .45 * primary.Samples && primary.SurroundsCentre &&
            result[box.MidY * width + box.MidX] <= 0 && interiorCandidate?.Invoke() is { } whole &&
            DescribesAssembly(whole, 0, new Footprint(whole, 0, width, box, step, cancellation), false))
            Merge(whole, 0);
        // Reframe on the detected rim when a loose box does not include a usable
        // contents hypothesis. Require the new contents to stay inside the rim.
        if (reframe != null && inside < .45 * primary.Samples && primary.SurroundsCentre &&
            result[box.MidY * width + box.MidX] <= 0)
        {
            var extent = BackdropMaskCandidates.Bounds(result, width, height, cancellation);
            if (box.Contains(extent))
            {
                var target = extent;
                target.Inflate(Math.Max(2, extent.Width / 24), Math.Max(2, extent.Height / 24));
                if (reframe(SKRectI.Intersect(target, box)) is { } reframed &&
                    DescribesAssembly(reframed, 0, new Footprint(reframed, 0, width, box, step, cancellation), true))
                    Merge(reframed, 0);
            }
        }
        if (inside > .5 * primary.Samples && result[box.MidY * width + box.MidX] > 0)
            RemoveDisputedNeighbor(result, masks, scores, best, width, height, box, cancellation);
        return result;
    }

    // Compare hypotheses on the same grid, preserving the positions of the first
    // and last rim pixel on every scan line. No convex hull is painted into the mask.
    private sealed class Footprint
    {
        private readonly bool[] occupied;
        private readonly int[] left, right, top, bottom;
        private readonly int cols, rows;
        internal int Count { get; }
        internal int Samples => occupied.Length;
        internal bool SurroundsCentre => left[rows / 2] < cols / 2 && cols / 2 < right[rows / 2] &&
            top[cols / 2] < rows / 2 && rows / 2 < bottom[cols / 2];

        internal int JointBoundingArea(Footprint other)
        {
            int w = Math.Max(right.Max(), other.right.Max()) - Math.Min(left.Min(), other.left.Min()) + 1;
            int h = Math.Max(bottom.Max(), other.bottom.Max()) - Math.Min(top.Min(), other.top.Min()) + 1;
            return w * h;
        }

        internal Footprint(float[] plane, int offset, int width, SKRectI box, int step, CancellationToken cancellation)
        {
            cols = (box.Width + step - 1) / step; rows = (box.Height + step - 1) / step;
            occupied = new bool[cols * rows];
            left = Enumerable.Repeat(cols, rows).ToArray(); right = Enumerable.Repeat(-1, rows).ToArray();
            top = Enumerable.Repeat(rows, cols).ToArray(); bottom = Enumerable.Repeat(-1, cols).ToArray();
            for (int y = 0; y < rows; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                int start = offset + (box.Top + y * step) * width + box.Left;
                for (int x = 0; x < cols; x++)
                {
                    if (plane[start + x * step] <= 0) continue;
                    occupied[y * cols + x] = true;
                    left[y] = Math.Min(left[y], x); right[y] = x;
                    top[x] = Math.Min(top[x], y); bottom[x] = y;
                    Count++;
                }
            }
        }

        internal int Shared(Footprint other)
        {
            int shared = 0;
            for (int i = 0; i < Samples; i++) if (occupied[i] && other.occupied[i]) shared++;
            return shared;
        }

        internal int EnclosedAdditions(Footprint other)
        {
            int enclosed = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    if (!occupied[y * cols + x] && other.occupied[y * cols + x] &&
                        ((x > left[y] && x < right[y]) || (y > top[x] && y < bottom[x]))) enclosed++;
            return enclosed;
        }
    }

    // When the leading mask includes a disconnected neighbour across the box boundary,
    // keep the central subject if two other predictions independently omit the neighbour.
    private static void RemoveDisputedNeighbor(float[] plane, float[] masks, float[] scores, int best,
        int width, int height, SKRectI box, CancellationToken cancellation)
    {
        int size = plane.Length;
        int[] others = Enumerable.Range(0, scores.Length).Where(i => i != best && scores[i] >= scores[best] - .15f).ToArray();
        if (others.Length < 2) return;
        bool Supported(int index)
        {
            foreach (int i in others) if (masks[i * size + index] > 0) return true;
            return false;
        }
        bool Disputed(int x, int y)
        {
            int index = y * width + x;
            return plane[index] > 0 && !Supported(index);
        }
        int crossing = 0;
        for (int x = box.Left; x < box.Right; x++)
        {
            if (box.Top > 0 && Disputed(x, box.Top - 1)) crossing++;
            if (box.Bottom < height && Disputed(x, box.Bottom)) crossing++;
        }
        for (int y = box.Top; y < box.Bottom; y++)
        {
            if (box.Left > 0 && Disputed(box.Left - 1, y)) crossing++;
            if (box.Right < width && Disputed(box.Right, y)) crossing++;
        }
        if (crossing < Math.Max(3, .04 * Math.Min(box.Width, box.Height))) return;
        using var binary = Pixels.NewMask(width, height);
        var bytes = binary.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
                bytes[y * binary.RowBytes + x] = plane[y * width + x] > 0 ? (byte)255 : (byte)0;
        }
        using var main = ObjectSelection.ConnectedPiece(binary, box.MidX, box.MidY, cancellation: cancellation);
        var core = main.GetPixelSpan();
        int attached = 0, detached = 0, outside = 0, supported = 0;
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (plane[index] <= 0) continue;
                if (core[y * main.RowBytes + x] != 0) { attached++; continue; }
                detached++;
                if (!box.Contains(x, y)) outside++;
                if (Supported(index)) supported++;
            }
        }
        if (detached < .02 * attached || detached > .20 * attached || outside < .08 * detached || supported > .05 * detached) return;
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
                if (core[y * main.RowBytes + x] == 0 && plane[y * width + x] > 0)
                    plane[y * width + x] = -plane[y * width + x];
        }
    }

    internal static bool IsContainedPart(float[] masks, int candidate, int width, int height, SKRectI prompt, CancellationToken cancellation)
    {
        var box = SKRectI.Intersect(SKRectI.Create(width, height), prompt);
        int step = Math.Max(1, Math.Min(box.Width, box.Height) / 64);
        int inside = 0, samples = 0, offset = candidate * width * height;
        for (int y = box.Top; y < box.Bottom; y += step)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = box.Left; x < box.Right; x += step)
            {
                samples++;
                if (masks[offset + y * width + x] > 0) inside++;
            }
        }
        return inside > .02 * samples && inside < .5 * samples &&
            OutsideFraction(masks, offset, width, height, box, step, inside, cancellation) < .02;
    }

    private static double OutsideFraction(float[] masks, int offset, int width, int height,
        SKRectI box, int step, int inside, CancellationToken cancellation)
    {
        int outside = 0;
        for (int y = box.Top % step; y < height; y += step)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = box.Left % step; x < width; x += step)
                if (!box.Contains(x, y) && masks[offset + y * width + x] > 0) outside++;
        }
        return (double)outside / Math.Max(1, inside + outside);
    }
}
