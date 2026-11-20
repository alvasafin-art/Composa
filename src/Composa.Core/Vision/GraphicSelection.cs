using Composa.Rendering;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>Recover separate printed marks and their counters on a uniform plate.
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

    internal static SKBitmap? FromBox(SKBitmap image, SKRectI box, CancellationToken cancellation)
    {
        if (box.Width < 12 || box.Height < 12) return null;
        var rgb = new PixelBuffer(image);
        var border = new List<PixelValue>(128);
        for (int i = 0; i < 32; i++)
        {
            int x = box.Left + (box.Width - 1) * i / 31, y = box.Top + (box.Height - 1) * i / 31;
            border.Add(rgb.Read(x, box.Top)); border.Add(rgb.Read(x, box.Bottom - 1));
            border.Add(rgb.Read(box.Left, y)); border.Add(rgb.Read(box.Right - 1, y));
        }
        float Median(int channel) => border.Select(p => p[channel]).Order().ElementAt(border.Count / 2);
        var bg = new PixelValue(Median(0), Median(1), Median(2), 1);
        if (border.Count(p => p.A > .98f && Distance(p, bg) < .07f) < .92 * border.Count) return null;

        int step = Math.Max(1, Math.Max(box.Width, box.Height) / 160);
        var foreground = new List<PixelValue>();
        int rows = 0, repeated = 0, samples = 0;
        for (int y = box.Top; y < box.Bottom; y += step)
        {
            cancellation.ThrowIfCancellationRequested();
            bool previous = false; int runs = 0;
            for (int x = box.Left; x < box.Right; x += step)
            {
                var p = rgb.Read(x, y); samples++;
                if (p.A < .98f) return null;
                bool mark = Distance(p, bg) > .24f;
                if (mark) { foreground.Add(p); if (!previous) runs++; }
                previous = mark;
            }
            rows++; if (runs >= 2) repeated++;
        }
        if (repeated < rows * .12 || foreground.Count < Math.Max(12, samples * .012) || foreground.Count > samples * .68) return null;
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
                    if (Distance(p, bg) < .07f) continue;
                    float best = float.MaxValue, alpha = 0;
                    foreach (var ink in inks)
                    {
                        float a = Coverage(p, bg, ink);
                        float error = Distance(p, bg + (ink - bg) * a);
                        if (error < best) { best = error; alpha = a; }
                    }
                    if (best < .12f) bytes[y * result.RowBytes + x] = (byte)Math.Round(255 * alpha);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
