using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Vision;

internal static class GroupedObjectSelection
{
    // A single box over separate objects can give a valid joint silhouette while
    // missing the background inside one object. Reuse the embedding to check each.
    internal static void RefineHoles(SKBitmap mask, SKRectI gesture, ImageEmbedding embedding, PromptModel model, CancellationToken cancel)
    {
        var bounds = SelectionMask.Bounds(mask, 128);
        if (bounds.Width < 20 || bounds.Height < 20 || !gesture.Contains(bounds)) return;
        int w = Math.Min(96, bounds.Width), h = Math.Min(96, bounds.Height);
        var columns = new bool[w]; var rows = new bool[h];
        var alpha = mask.GetPixelSpan();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int px = bounds.Left + (2 * x + 1) * bounds.Width / (2 * w);
                int py = bounds.Top + (2 * y + 1) * bounds.Height / (2 * h);
                if (alpha[py * mask.RowBytes + px] < 128) continue;
                columns[x] = rows[y] = true;
            }
        static int Split(bool[] occupied)
        {
            int split = -1;
            for (int i = 0; i < occupied.Length;)
            {
                if (occupied[i]) { i++; continue; }
                int start = i;
                while (i < occupied.Length && !occupied[i]) i++;
                if (i - start < .10 * occupied.Length) continue;
                if (start < .16 * occupied.Length || occupied.Length - i < .16 * occupied.Length || split >= 0) return -1;
                split = (start + i) / 2;
            }
            return split;
        }
        var first = gesture; var second = gesture;
        int xSplit = Split(columns), ySplit = Split(rows);
        if (xSplit >= 0) first.Right = second.Left = bounds.Left + bounds.Width * xSplit / w;
        else if (ySplit >= 0) first.Bottom = second.Top = bounds.Top + bounds.Height * ySplit / h;
        else return;
        foreach (var part in new[] { first, second })
        {
            using var crop = Pixels.Subset(mask, part);
            var target = SelectionMask.Bounds(crop, 128);
            if (target.Width < 12 || target.Height < 12) continue;
            target.Offset(part.Left, part.Top);
            target.Inflate(Math.Max(3, target.Width / 12), Math.Max(3, target.Height / 12));
            target = SKRectI.Intersect(target, part);
            using var local = PromptSegmentation.Decode(embedding, model, mask.Width, mask.Height, null, target, cancel);
            using var silhouette = Pixels.Subset(local, target);
            ApplyAgreedHoles(mask, silhouette, target, cancel);
        }
    }

    internal static void ApplyAgreedHoles(SKBitmap mask, SKBitmap silhouette, SKRectI target, CancellationToken cancel)
    {
        var alpha = mask.GetPixelSpan();
        // Pad with reachable background even if the subject touches the gesture.
        using var inverse = Pixels.NewMask(target.Width + 2, target.Height + 2, 255);
        var inv = inverse.GetPixelSpan(); var localAlpha = silhouette.GetPixelSpan();
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
                inv[(y + 1) * inverse.RowBytes + x + 1] = (byte)(255 - localAlpha[y * silhouette.RowBytes + x]);
        using var exterior = ObjectSelection.ConnectedPiece(inverse, 0, 0, cancellation: cancel);
        var outside = exterior.GetPixelSpan();
        int union = 0, mismatch = 0, holes = 0;
        for (int y = 0; y < target.Height; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = 0; x < target.Width; x++)
            {
                bool joint = alpha[(y + target.Top) * mask.RowBytes + x + target.Left] >= 128;
                bool single = localAlpha[y * silhouette.RowBytes + x] >= 128;
                bool enclosed = !single && outside[(y + 1) * exterior.RowBytes + x + 1] == 0;
                if (joint || single) union++;
                if (joint && enclosed) holes++;
                else if (joint != single) mismatch++;
            }
        }
        if (holes < Math.Max(12, .01 * union) || mismatch > .06 * union) return;
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
                if (localAlpha[y * silhouette.RowBytes + x] < 128 && outside[(y + 1) * exterior.RowBytes + x + 1] == 0)
                    alpha[(y + target.Top) * mask.RowBytes + x + target.Left] = 0;
        Pixels.Invalidate(mask);
    }
}
