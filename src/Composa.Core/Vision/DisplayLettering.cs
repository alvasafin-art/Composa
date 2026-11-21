using Composa.Rendering;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>Box selection of display lettering with a common ink and visible paper between strokes.</summary>
internal static class DisplayLettering
{
    private readonly record struct Run(int Left, int Right);
    private readonly record struct Component(SKRectI Bounds, int Count, int Solid);

    internal static SKBitmap? Repair(SKBitmap source, SKRectI box, SKBitmap? original, CancellationToken cancel)
    {
        if (!CandidateSize(box)) return null;
        // Work on display RGB around the gesture even for large RGB16 documents.
        var area = box;
        area.Inflate(3, Math.Max(3, box.Height / 65));
        var bounds = SKRectI.Create(source.Width, source.Height);
        area = SKRectI.Intersect(area, bounds);
        using var crop = Pixels.Subset(source, area);
        using var display = Pixels.IsSrgb8(crop) ? null : Pixels.DisplayCopy(crop);
        var local = box;
        local.Offset(-area.Left, -area.Top);
        using var proposal = FromBox(display ?? crop, local, cancel, out bool clipped);
        if (proposal == null) return null;
        if (!clipped && original != null)
        {
            int intersection = 0, union = 0;
            var before = original.GetPixelSpan();
            var after = proposal.GetPixelSpan();
            for (int y = box.Top; y < box.Bottom; y++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int x = box.Left; x < box.Right; x++)
                {
                    bool wasInk = before[y * original.RowBytes + x] >= 128;
                    bool isInk = after[(y - area.Top) * proposal.RowBytes + x - area.Left] >= 128;
                    if (wasInk || isInk) union++;
                    if (wasInk && isInk) intersection++;
                }
            }
            // Retain the existing antialiasing when both paths already agree on the strokes.
            if (intersection >= .94 * union) return null;
        }
        return PromptSegmentation.Place(proposal, area, bounds);
    }

    private static bool CandidateSize(SKRectI box) => box.Width >= 80 && box.Height >= 32 &&
        box.Width >= .8 * box.Height && (long)box.Width * box.Height <= 5_000_000;

    private static SKBitmap? FromBox(SKBitmap image, SKRectI box, CancellationToken cancel, out bool clipped)
    {
        int w = box.Width, h = box.Height;
        clipped = false;
        if (!CandidateSize(box)) return null;
        var pixels = new PixelBuffer(image);
        if (!TryPalette(pixels, box, cancel, out var paper, out var ink)) return null;
        var caps = FindCaps(pixels, box, ink, paper, cancel);
        // In image-filled type, the clipped photograph can outweigh the exposed ink.
        if (caps.Runs.Count == 0 && w <= 2.2 * h)
        {
            var top = box with { Bottom = box.Top + Math.Max(8, h / 4) };
            if (TryInk(pixels, top, paper, cancel, out var capInk))
            {
                caps = FindCaps(pixels, box, capInk, paper, cancel);
                if (caps.Runs.Count > 0) ink = capInk;
            }
        }
        bool tall = caps.Runs.Count >= 4;
        if (!tall && w < 1.8 * h) return null;
        int floor = tall ? ClippedBaseline(pixels, box, caps.Top, caps.Runs, paper, cancel) : -1;
        clipped = floor >= 0;
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                var p = pixels.Read(x + box.Left, y + box.Top);
                if (p.A < .98f) return null;
                if (floor >= 0)
                {
                    if (y >= Math.Max(0, caps.Top - Math.Max(3, h / 65)) && y <= floor + 1)
                        alpha[y * w + x] = Coverage((Distance(p, paper) - .11f) / .12f);
                }
                else alpha[y * w + x] = InkAlpha(p, paper, ink, tall);
            }
        }
        if (floor >= 0) SolidifyInteriors(alpha, w, h, cancel);
        var labels = new int[alpha.Length];
        var keep = FindLetterComponents(alpha, labels, w, h, caps.Top, tall, floor >= 0, cancel);
        if (keep == null) return null;
        var mask = Pixels.NewMask(image.Width, image.Height);
        try
        {
            var output = mask.GetPixelSpan();
            for (int y = 0; y < h; y++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x++)
                    if (keep[labels[y * w + x]]) output[(y + box.Top) * mask.RowBytes + box.Left + x] = alpha[y * w + x];
            }
            Pixels.Invalidate(mask);
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    private static bool[]? FindLetterComponents(byte[] alpha, int[] labels, int w, int h,
        int capTop, bool tall, bool clipped, CancellationToken cancel)
    {
        var components = Label(alpha, labels, w, h, cancel);
        var keep = new bool[components.Count + 1];
        int accepted = 0, left = w, right = 0;
        for (int i = 0; i < components.Count; i++)
        {
            var c = components[i]; var b = c.Bounds;
            if (c.Solid < Math.Max(5, w / 70) || c.Solid < .08 * c.Count) continue;
            bool belongs = tall
                ? b.Top <= capTop + Math.Max(5, h / 35) && b.Height >= h * .12
                : b.Height >= .10 * h && b.Width >= Math.Max(2, .006 * w) &&
                  b.Height < .98 * h && c.Count < .83 * b.Width * b.Height;
            if (!belongs) continue;
            keep[i + 1] = true; accepted++;
            left = Math.Min(left, b.Left); right = Math.Max(right, b.Right);
        }
        if (accepted < (tall ? 4 : 5) || right - left < .71 * w) return null;
        if (!tall)
        {
            var letters = components.Where((_, i) => keep[i + 1]).ToList();
            // A long open swash, or two distinct baselines supported by several glyphs each.
            bool swash = letters.Any(c => c.Bounds.Width > .32 * w && c.Bounds.Height > .48 * h &&
                c.Count < .25 * c.Bounds.Width * c.Bounds.Height);
            bool baseline(double top, double bottom) => letters.Count(c => c.Bounds.Bottom >= h * top &&
                c.Bounds.Bottom <= h * bottom && c.Bounds.Height >= .18 * h) >= 4;
            if (!swash && !(baseline(.30, .57) && baseline(.76, .99))) return null;
        }
        // An occluder can disconnect the lower half of a stroke. Dots and short visible
        // fragments need a dense core of the same ink, after the line itself is established.
        if (!clipped)
        {
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i]; var b = c.Bounds;
                double density = c.Solid / (double)(b.Width * b.Height);
                if (keep[i + 1] && !tall && c.Solid < .06 * b.Width * b.Height && b.Width < .12 * w)
                    keep[i + 1] = false;
                if (c.Solid >= Math.Max(12, w * h * .00023) && density > (tall ? .44 : .19)
                    && b.Height >= .015 * h && b.Left >= left && b.Right <= right)
                    keep[i + 1] = true;
            }
        }
        return keep;
    }

    private static bool TryPalette(PixelBuffer pixels, SKRectI box, CancellationToken cancel,
        out PixelValue paper, out PixelValue ink)
    {
        ink = default;
        int w = box.Width, h = box.Height;
        var rim = new List<PixelValue>();
        for (int i = 0; i < 64; i++)
        {
            int x = box.Left + i * (w - 1) / 63, y = box.Top + i * (h - 1) / 63;
            rim.Add(pixels.Read(x, box.Top)); rim.Add(pixels.Read(x, box.Bottom - 1));
            rim.Add(pixels.Read(box.Left, y)); rim.Add(pixels.Read(box.Right - 1, y));
        }
        float Median(int k) => rim.Select(p => p[k]).Order().ElementAt(rim.Count / 2);
        paper = new PixelValue(Median(0), Median(1), Median(2), 1);
        int matching = 0;
        foreach (var sample in rim)
            if (sample.A > .98f && Distance(sample, paper) < .16f) matching++;
        if (matching < .70 * rim.Count) return false;
        return TryInk(pixels, box, paper, cancel, out ink);
    }

    private static bool TryInk(PixelBuffer pixels, SKRectI box, PixelValue paper, CancellationToken cancel, out PixelValue ink)
    {
        ink = default;
        var bins = new int[4096];
        var sums = new PixelValue[bins.Length];
        int samples = 0, contrasted = 0, step = Math.Max(1, Math.Max(box.Width, box.Height) / 240);
        for (int y = box.Top; y < box.Bottom; y += step)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = box.Left; x < box.Right; x += step)
            {
                var p = pixels.Read(x, y);
                if (p.A < .98f) return false;
                samples++;
                if (Distance(p, paper) < .38f) continue;
                int Bin(float v) => Math.Clamp((int)(v * 16), 0, 15);
                int bin = Bin(p.R) * 256 + Bin(p.G) * 16 + Bin(p.B);
                bins[bin]++; sums[bin] += p; contrasted++;
            }
        }
        int peak = Array.IndexOf(bins, bins.Max());
        if (bins[peak] < Math.Max(.008 * samples, .12 * contrasted) || contrasted == 0) return false;
        ink = sums[peak] * (1f / bins[peak]);
        return true;
    }

    private static float Distance(PixelValue a, PixelValue b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static byte Coverage(float value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static byte InkAlpha(PixelValue p, PixelValue paper, PixelValue ink, bool tall)
    {
        var d = ink - paper; var v = p - paper;
        float t = (v.R * d.R + v.G * d.G + v.B * d.B) / (d.R * d.R + d.G * d.G + d.B * d.B);
        if (t < .22f) return 0;
        var residual = p - (paper + d * t);
        float chroma = Math.Max(residual.R, Math.Max(residual.G, residual.B)) - Math.Min(residual.R, Math.Min(residual.G, residual.B));
        float tolerance = tall ? .033f : .10f;
        return Coverage(Math.Clamp((t - .18f) / .72f, 0, 1) * Math.Clamp((tolerance - chroma) / (.4f * tolerance), 0, 1));
    }

    // A common cap line supplies the starting strokes, even when an occluder splits them below.
    private static (int Top, List<Run> Runs) FindCaps(PixelBuffer rgb, SKRectI box, PixelValue ink, PixelValue paper, CancellationToken cancel)
    {
        int w = box.Width, h = box.Height, look = Math.Max(3, h / 65);
        if (w > 2.2 * h) return (0, []);
        for (int y = 0; y < h / 4; y += Math.Max(1, look / 4))
        {
            cancel.ThrowIfCancellationRequested();
            var runs = new List<Run>();
            int start = -1, width = 0;
            for (int x = 0; x <= w; x++)
            {
                bool inside = x < w && Distance(rgb.Read(box.Left + x, box.Top + y), ink) < .13f;
                if (inside && start < 0) start = x;
                if (inside || start < 0) continue;
                if (x - start >= w * .026 && x - start <= w * .25)
                {
                    int mid = box.Left + (x + start) / 2;
                    if (Distance(rgb.Read(mid, Math.Max(0, box.Top + y - look)), paper) < .18f &&
                       Distance(rgb.Read(mid, box.Top + y + look), ink) < .18f)
                    { runs.Add(new(start, x)); width += x - start; }
                }
                start = -1;
            }
            if (runs.Count >= 4 && width > .40 * w && runs[^1].Right - runs[0].Left > .78 * w) return (y, runs);
        }
        return (0, []);
    }

    private static int ClippedBaseline(PixelBuffer rgb, SKRectI box, int top, List<Run> runs, PixelValue paper, CancellationToken cancel)
    {
        // When a photograph is clipped into type, the paper gutters continue through it,
        // and all the filled strokes end together on a clear baseline.
        int h = box.Height;
        int floor = -1;
        for (int y = h - 1; y > h * .7; y--)
        {
            cancel.ThrowIfCancellationRequested();
            int marks = 0, below = 0;
            foreach (var r in runs)
            {
                int x = box.Left + (r.Left + r.Right) / 2;
                if (Distance(rgb.Read(x, box.Top + y), paper) > .24f) marks++;
                if (Distance(rgb.Read(x, Math.Min(rgb.Height - 1, box.Top + y + 3)), paper) < .15f) below++;
            }
            if (marks >= .75 * runs.Count && below >= .85 * runs.Count) { floor = y; break; }
        }
        if (floor < 0) return -1;
        int supported = 0;
        for (int i = 1; i < runs.Count; i++)
        {
            bool continuous = false;
            for (int x = runs[i - 1].Right; x < runs[i].Left && !continuous; x++)
            {
                int clear = 0, total = 0;
                for (int y = top + 4; y < floor; y += Math.Max(1, h / 64))
                {
                    total++;
                    if (Distance(rgb.Read(box.Left + x, box.Top + y), paper) < .16f) clear++;
                }
                continuous = clear >= .87 * total;
            }
            if (continuous) supported++;
        }
        return supported >= .85 * (runs.Count - 1) ? floor : -1;
    }

    private static void SolidifyInteriors(byte[] alpha, int w, int h, CancellationToken cancel)
    {
        // Photo colour belongs to the stroke even where it approaches the paper colour.
        // Only samples bordering actual paper need fractional coverage.
        for (int y = 1; y < h - 1; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                if (alpha[i] is 0 or 255) continue;
                if (alpha[i - 1] > 0 && alpha[i + 1] > 0 && alpha[i - w] > 0 && alpha[i + w] > 0)
                    alpha[i] = 255;
            }
        }
    }

    private static List<Component> Label(byte[] alpha, int[] labels, int w, int h, CancellationToken cancel)
    {
        var components = new List<Component>();
        var stack = new Stack<int>();
        for (int seed = 0; seed < alpha.Length; seed++)
        {
            if (seed % w == 0) cancel.ThrowIfCancellationRequested();
            if (alpha[seed] == 0 || labels[seed] != 0) continue;
            int id = components.Count + 1, l = w, r = 0, t = h, b = 0, count = 0, solid = 0;
            labels[seed] = id; stack.Push(seed);
            while (stack.TryPop(out int i))
            {
                if ((count & 4095) == 0) cancel.ThrowIfCancellationRequested();
                count++;
                int x = i % w, y = i / w;
                l = Math.Min(l, x); r = Math.Max(r, x + 1); t = Math.Min(t, y); b = Math.Max(b, y + 1);
                if (alpha[i] >= 210) solid++;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                    {
                        int n = yy * w + xx;
                        if (labels[n] != 0 || alpha[n] == 0) continue;
                        labels[n] = id; stack.Push(n);
                    }
            }
            components.Add(new(new(l, t, r, b), count, solid));
        }
        return components;
    }
}
