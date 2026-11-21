using Composa.Filters;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Vision;

internal static unsafe class StrandMatting
{
    // A mixed backdrop (for example sky above trees) has no single global colour.
    // Look locally for several thin strands crossing the same verified exterior colour.
    internal static void Refine(SKBitmap image, SKBitmap matte, int radius, CancellationToken cancel)
    {
        var rgb = new PixelBuffer(image);
        int w = matte.Width, h = matte.Height, stride = matte.RowBytes;
        int reach = Math.Clamp(radius, 16, 32);
        var alpha = (byte*)matte.GetPixels();
        byte[]? output = null;
        var samples = new List<PixelValue>();
        int pitch = Math.Max(12, reach * 2 / 3);
        for (int cy = 0; cy < h; cy += pitch)
        {
            cancel.ThrowIfCancellationRequested();
            for (int cx = 0; cx < w; cx += pitch)
            {
                int left = Math.Max(0, cx - reach), right = Math.Min(w - 1, cx + reach);
                int top = Math.Max(0, cy - reach), bottom = Math.Min(h - 1, cy + reach);
                if (alpha[cy * stride + cx] == 0 && alpha[top * stride + left] == 0 &&
                    alpha[top * stride + right] == 0 && alpha[bottom * stride + left] == 0 &&
                    alpha[bottom * stride + right] == 0) continue;
                samples.Clear();
                int spacing = reach / 3;
                for (int y = Math.Max(0, cy - 3 * reach); y <= Math.Min(h - 1, cy + 3 * reach); y += spacing)
                    for (int x = Math.Max(0, cx - 3 * reach) + (y / spacing * 3) % spacing;
                         x <= Math.Min(w - 1, cx + 3 * reach); x += spacing)
                        if (alpha[y * stride + x] < 4) samples.Add(rgb.Read(x, y).Straight);
                if (ForegroundCoverage.ColourPlate(samples, minimumChroma: .12f) is not { } bg ||
                    samples.Count(p => Distance(p, bg) < .045f) < .85 * samples.Count) continue;

                // A solid subject of the same colour, or a lone hard outline, cannot
                // establish strand gaps. Count narrow, interrupted runs in both axes.
                bool Crossings(bool vertical)
                {
                    int lines = 0, suspect = 0;
                    for (int v = vertical ? left : top; v <= (vertical ? right : bottom); v += 3)
                    {
                        int first = -1, width = 0, runs = 0;
                        for (int u = vertical ? top : left; u <= (vertical ? bottom : right); u++)
                        {
                            int x = vertical ? v : u, y = vertical ? u : v;
                            float d = Distance(rgb.Read(x, y).Straight, bg);
                            if (d < .065f)
                            {
                                if (first >= 0 && width >= 1 && u - first <= 10) runs++;
                                if (alpha[y * stride + x] >= 224) suspect++;
                                first = u; width = 0;
                            }
                            else if (d > .18f && alpha[y * stride + x] > 64) width++;
                        }
                        if (runs >= 2) lines++;
                    }
                    return lines >= 3 && suspect >= 18;
                }
                if (!Crossings(false) && !Crossings(true)) continue;
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                    {
                        int index = y * stride + x;
                        if (alpha[index] == 0) continue;
                        float distance = Distance(rgb.Read(x, y).Straight, bg);
                        if (distance >= .22f) continue;
                        float limit = 255 * Math.Clamp((distance - .028f) / (.22f - .028f), 0, 1);
                        float fade = Math.Clamp(2f * (reach - Math.Max(Math.Abs(x - cx), Math.Abs(y - cy))) / reach, 0, 1);
                        if (alpha[index] <= limit) continue;
                        byte corrected = (byte)Math.Round(alpha[index] - (alpha[index] - limit) * fade);
                        if (corrected == alpha[index]) continue;
                        output ??= matte.GetPixelSpan().ToArray();
                        output[index] = Math.Min(output[index], corrected);
                    }
            }
        }
        if (output == null) return;
        RecoverEnclosedGaps(image, matte, output, reach, cancel);
        output.AsSpan().CopyTo(matte.GetPixelSpan());
    }

    // Extend the trimap around verified strand gaps. A second colour in a mixed
    // backdrop can show through the same tuft even far from the outer silhouette.
    private static void RecoverEnclosedGaps(SKBitmap image, SKBitmap matte, byte[] corrected, int reach, CancellationToken cancel)
    {
        var rgb = new PixelBuffer(image);
        var original = matte.GetPixelSpan();
        int w = matte.Width, h = matte.Height, stride = matte.RowBytes;
        int left = w, right = -1, top = h, bottom = -1, evidence = 0;
        for (int y = 0; y < h; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x;
                if (original[i] - corrected[i] <= 60) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = y; evidence++;
            }
        }
        if (evidence < reach * reach / 3 || !TrimapMatting.IsAvailable) return;
        var canvas = SKRectI.Create(w, h);
        var holes = new SKRectI(left, top, right + 1, bottom + 1);
        SKRectI Surround(int distance)
        {
            var r = holes; r.Inflate(distance, distance);
            return SKRectI.Intersect(r, canvas);
        }
        var nearby = Surround(5 * reach);
        // Widely separated patches cannot establish a shared palette.
        if ((long)nearby.Width * nearby.Height > 2_000_000) return;
        var field = Surround(10 * reach);
        var inner = Surround(9 * reach / 2);
        var fore = new Palette(); var back = new Palette();
        int step = Math.Max(1, Math.Max(field.Width, field.Height) / 350);
        for (int y = field.Top; y < field.Bottom; y += step)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = field.Left; x < field.Right; x += step)
            {
                int a = original[y * stride + x];
                if (a < 3) back.Add(rgb.Read(x, y).Straight);
                else if (a > 250 && !inner.Contains(x, y)) fore.Add(rgb.Read(x, y).Straight);
            }
        }
        if (fore.Coloured < 64 || back.Coloured < 64) return;
        using var seeds = Pixels.NewMask(nearby.Width, nearby.Height);
        using var certain = Pixels.NewMask(nearby.Width, nearby.Height);
        var seed = seeds.GetPixelSpan(); var hard = certain.GetPixelSpan();
        for (int y = nearby.Top; y < nearby.Bottom; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = nearby.Left; x < nearby.Right; x++)
            {
                int i = (y - nearby.Top) * seeds.RowBytes + x - nearby.Left;
                var p = rgb.Read(x, y).Straight;
                var hue = Palette.Hue(p);
                if (hue < 0) continue;
                double bh = back.Frequency(hue), fh = fore.Frequency(hue);
                if (bh < .012 || bh <= 2.5 * fh) continue;
                int bin = Palette.Bin(p);
                double b = back.Density(bin), f = fore.Density(bin);
                if (b < .0005 || b < .11 * f && bh < 8 * fh) continue;
                seed[i] = 255;
                if (b > f || bh > 6 * fh) hard[i] = 255;
            }
        }
        Pixels.Invalidate(seeds); Pixels.Invalidate(certain);
        using var core = SelectionMask.Contract(seeds, 1);
        if (core == null) return;
        using var band = SelectionMask.Expand(core, 8);
        // Context outside the edited band remains available to ViTMatte. Only edited
        // pixels are copied back, so other edges keep their original alpha exactly.
        var area = Surround(7 * reach);
        using var localImage = Pixels.Subset(image, area);
        using var prior = Pixels.NewMask(area.Width, area.Height);
        using var marks = Pixels.NewMask(area.Width, area.Height);
        var edit = marks.GetPixelSpan();
        var before = prior.GetPixelSpan();
        for (int y = area.Top; y < area.Bottom; y++)
        {
            cancel.ThrowIfCancellationRequested();
            corrected.AsSpan(y * stride + area.Left, area.Width).CopyTo(before.Slice((y - area.Top) * prior.RowBytes));
        }
        var possible = band.GetPixelSpan();
        for (int y = nearby.Top; y < nearby.Bottom; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = nearby.Left; x < nearby.Right; x++)
            {
                int i = y * stride + x, local = (y - nearby.Top) * band.RowBytes + x - nearby.Left;
                if (possible[local] == 0 || corrected[i] == 0) continue;
                int support = 0, samples = 0;
                if (hard[local] > 0 && original[i] >= 200)
                {
                    for (int yy = Math.Max(y - 1, nearby.Top); yy <= Math.Min(y + 1, nearby.Bottom - 1); yy++)
                        for (int xx = Math.Max(x - 1, nearby.Left); xx <= Math.Min(x + 1, nearby.Right - 1); xx++)
                        {
                            samples++;
                            if (hard[(yy - nearby.Top) * certain.RowBytes + xx - nearby.Left] > 0) support++;
                        }
                }
                edit[(y - area.Top) * marks.RowBytes + x - area.Left] = (byte)(
                    samples > 0 && support >= .75 * samples ? MattingMark.Background : MattingMark.Unknown);
            }
        }
        Pixels.Invalidate(prior); Pixels.Invalidate(marks);
        using var refined = TrimapMatting.Refine(localImage, prior, 0, marks, cancel, fineDetail: true);
        var result = refined.GetPixelSpan();
        for (int y = nearby.Top; y < nearby.Bottom; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = nearby.Left; x < nearby.Right; x++)
            {
                int i = y * stride + x, local = (y - area.Top) * marks.RowBytes + x - area.Left;
                if (edit[local] == 0) continue;
                // Keep previously isolated flyaways. The erroneous fill is opaque.
                float weight = Math.Clamp((original[i] - 72) / 120f, 0, 1);
                int bin = Palette.Bin(rgb.Read(x, y).Straight);
                if (edit[local] == (byte)MattingMark.Unknown && fore.Density(bin) > 8 * back.Density(bin))
                    weight *= .35f;
                corrected[i] -= (byte)Math.Round(weight * Math.Max(0, corrected[i] - result[local]));
            }
        }
    }

    private sealed class Palette
    {
        private readonly int[] hues = new int[36];
        private readonly double[] colours = new double[24 * 24 * 16];
        private int count;
        internal int Coloured { get; private set; }

        internal static int Hue(PixelValue p)
        {
            ColorMath.RgbToHsv(p.R, p.G, p.B, out double h, out double s, out _);
            return s < .13 ? -1 : Math.Clamp((int)(h / 10), 0, 35);
        }

        internal static int Bin(PixelValue p)
        {
            float level = (p.R + p.G + p.B) / 3, scale = Math.Max(.17f, level);
            int Chroma(float delta) => Math.Clamp((int)((delta / scale + .7f) * (24 / 1.4f)), 0, 23);
            return (Chroma(p.R - p.G) * 24 + Chroma(p.G - p.B)) * 16 + Math.Clamp((int)(level * 16), 0, 15);
        }

        internal void Add(PixelValue p)
        {
            if (p.A < .98f) return;
            count++;
            int hue = Hue(p);
            if (hue >= 0) { hues[hue]++; Coloured++; }
            int bin = Bin(p), a = bin / 384, b = bin / 16 % 24, l = bin % 16;
            for (int i = Math.Max(0, a - 1); i <= Math.Min(23, a + 1); i++)
                for (int j = Math.Max(0, b - 1); j <= Math.Min(23, b + 1); j++)
                    for (int k = Math.Max(0, l - 1); k <= Math.Min(15, l + 1); k++)
                        colours[(i * 24 + j) * 16 + k] += (i == a ? 1 : .45) * (j == b ? 1 : .45) * (k == l ? 1 : .45);
        }

        internal double Density(int bin) => colours[bin] / count;
        internal double Frequency(int hue) => hues[hue] / (double)Coloured;
    }

    private static float Distance(PixelValue a, PixelValue b) => Math.Max(Math.Abs(a.R - b.R),
        Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
}
