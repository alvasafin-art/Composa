using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>Detect a tightly framed group of similar dark or light details against a busier surface.</summary>
internal static class RepeatedDetailSelection
{
    private readonly record struct Component(int Label, SKRectI Bounds, int Area);

    private static bool Similar(int a, int b) => Math.Min(a, b) >= .60 * Math.Max(a, b);

    private static bool SameSize(Component a, Component b) =>
        Similar(a.Bounds.Width, b.Bounds.Width) && Similar(a.Bounds.Height, b.Bounds.Height) && Similar(a.Area, b.Area);

    // In a loose box the metal/background can occupy the tenth percentile. A smaller
    // ink peak is credible when independent components repeat at regular intervals.
    private static Component[] RegularDetails(Component[] parts)
    {
        var confirmed = new HashSet<int>();
        foreach (var middle in parts)
        {
            var peers = parts.Where(p => p.Label != middle.Label && SameSize(p, middle)).ToArray();
            for (int i = 0; i < peers.Length; i++)
                for (int j = i + 1; j < peers.Length; j++)
                {
                    var a = peers[i].Bounds; var b = middle.Bounds; var c = peers[j].Bounds;
                    if (Math.Abs(a.MidX - c.MidX) < 2 * b.Width && Math.Abs(a.MidY - c.MidY) < 2 * b.Height) continue;
                    if (Math.Abs(a.MidX + c.MidX - 2 * b.MidX) > .6 * b.Width ||
                        Math.Abs(a.MidY + c.MidY - 2 * b.MidY) > .6 * b.Height) continue;
                    confirmed.Add(peers[i].Label); confirmed.Add(middle.Label); confirmed.Add(peers[j].Label);
                }
        }
        return parts.Where(p => confirmed.Contains(p.Label)).ToArray();
    }

    internal static SKBitmap? AtPoint(SKBitmap image, SKPointI seed, CancellationToken cancel)
    {
        int reach = Math.Clamp(Math.Max(image.Width, image.Height) / 4, 48, 180);
        foreach (var (rx, ry) in new[] { (reach, reach / 2), (reach / 2, reach) })
        {
            var box = SKRectI.Intersect(SKRectI.Create(image.Width, image.Height),
                new(seed.X - rx, seed.Y - ry, seed.X + rx + 1, seed.Y + ry + 1));
            using var group = FromBox(image, box, cancel);
            if (group != null && ObjectSelection.FromMatte(group, seed.X, seed.Y, 0) is { } hit) return hit;
        }
        return null;
    }

    internal static SKBitmap? FromBox(SKBitmap image, SKRectI box, CancellationToken cancel) =>
        FromBox(image, box, cancel, out _);

    internal static SKBitmap? FromBox(SKBitmap image, SKRectI box, CancellationToken cancel, out bool needsShapeCheck)
    {
        needsShapeCheck = false;
        int step = Math.Max(1, (Math.Max(box.Width, box.Height) + 319) / 320);
        int w = (box.Width + step - 1) / step, h = (box.Height + step - 1) / step;
        if (Math.Min(w, h) < 18) return null;
        var rgb = new PixelBuffer(image);
        var values = new byte[w * h]; var histogram = new int[256];
        for (int y = 0; y < h; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                var p = rgb.Read(box.Left + x * step, box.Top + y * step);
                if (p.A < .98f) return null;
                var v = Luma(p); values[y * w + x] = v; histogram[v]++;
            }
        }
        int Percentile(double fraction)
        {
            int sum = 0;
            for (int i = 0; i < 255; ++i) { sum += histogram[i]; if (sum >= values.Length * fraction) return i; }
            return 255;
        }
        int median = Percentile(.5);
        foreach (bool dark in new[] { true, false })
        {
            int ink = dark ? Percentile(.04) : Percentile(.96);
            // The detail itself should have a stable tone, even when the surrounding material varies.
            bool sparse = Math.Abs(ink - (dark ? Percentile(.10) : Percentile(.90))) > 6;
            if (Math.Abs(median - ink) < 16 || sparse && Math.Abs(ink - (dark ? Percentile(.02) : Percentile(.98))) > 2) continue;
            int threshold = (int)Math.Round(ink + (median - ink) * .48);
            var labels = new int[values.Length]; var queue = new int[values.Length];
            var parts = new List<Component>(); int nextLabel = 0;
            bool IsInk(int index) => dark ? values[index] <= threshold : values[index] >= threshold;
            for (int i = 0; i < values.Length; i++)
            {
                if (labels[i] != 0 || !IsInk(i)) continue;
                cancel.ThrowIfCancellationRequested();
                int label = ++nextLabel; int front = 0, count = 1; queue[0] = i; labels[i] = label;
                int l = w, r = 0, t = h, b = 0;
                while (front < count)
                {
                    int at = queue[front++], x = at % w, y = at / w;
                    l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y);
                    void Visit(int n) { if (labels[n] == 0 && IsInk(n)) { labels[n] = label; queue[count++] = n; } }
                    if (x > 0) Visit(at - 1); if (x + 1 < w) Visit(at + 1);
                    if (y > 0) Visit(at - w); if (y + 1 < h) Visit(at + w);
                }
                var bounds = new SKRectI(l, t, r + 1, b + 1);
                if (count < Math.Max(10, w * h / 800) || bounds.Width >= .80 * w || bounds.Height >= .87 * h ||
                    count < bounds.Width * bounds.Height * .35) continue;
                parts.Add(new(label, bounds, count));
            }
            if (parts.Count is < 3 or > 64) continue;
            var grouped = parts.Where(p => parts.Count(q => SameSize(p, q)) >= 3).ToArray();
            if (grouped.Length < 3) continue;
            if (sparse)
            {
                grouped = RegularDetails(grouped);
                if (grouped.Length < 5) continue;
            }
            int area = grouped.Sum(p => p.Area);
            if (area < w * h * .035 || area > w * h * .46) continue;
            double coverX = (grouped.Max(p => p.Bounds.Right) - grouped.Min(p => p.Bounds.Left)) / (double)w;
            double coverY = (grouped.Max(p => p.Bounds.Bottom) - grouped.Min(p => p.Bounds.Top)) / (double)h;
            if (sparse ? Math.Max(coverX, coverY) < .61 || Math.Min(coverX, coverY) < .40
                       : coverX < .61 || coverY < .54) continue;
            if (grouped.Count(p => p.Bounds.Left > 0 && p.Bounds.Top > 0 && p.Bounds.Right < w && p.Bounds.Bottom < h) < 2) continue;
            var keep = new bool[nextLabel + 1];
            foreach (var part in grouped) keep[part.Label] = true;
            var mask = Pixels.NewMask(image.Width, image.Height);
            try
            {
                var bytes = mask.GetPixelSpan();
                for (int y = box.Top; y < box.Bottom; y++)
                {
                    cancel.ThrowIfCancellationRequested();
                    for (int x = box.Left; x < box.Right; x++)
                    {
                        int u = (x - box.Left) / step, v = (y - box.Top) / step;
                        // Use the original pixel at an accepted component or its one-cell edge.
                        if (!keep[labels[v*w+u]] && !keep[labels[v*w+Math.Max(0,u-1)]] && !keep[labels[v*w+Math.Min(w-1,u+1)]]
                            && !keep[labels[Math.Max(0,v-1)*w+u]] && !keep[labels[Math.Min(h-1,v+1)*w+u]]) continue;
                        float a = (Luma(rgb.Read(x, y)) - median) / (float)(ink - median);
                        bytes[y * mask.RowBytes + x] = (byte)Math.Round(255 * Math.Clamp(a, 0, 1));
                    }
                }
                needsShapeCheck = sparse;
                return mask;
            }
            catch { mask.Dispose(); throw; }
        }
        return null;
    }

    private static byte Luma(PixelValue p) => (byte)Math.Round(255 * (.2126 * p.R + .7152 * p.G + .0722 * p.B));
}
