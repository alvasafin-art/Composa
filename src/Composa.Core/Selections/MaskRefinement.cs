using Composa.Rendering;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Selections;

public sealed record MaskRefinementSettings(double Smooth = 0, double Feather = 0, double Contrast = 0, int Shift = 0, bool EdgeAware = true);

public static unsafe class MaskRefinement
{
    public static SKBitmap Refine(SKBitmap mask, SKBitmap image, MaskRefinementSettings settings, CancellationToken cancellation = default)
    {
        var working = Pixels.Clone(mask);
        try
        {
        if (settings.Smooth > 0)
        {
            var next = SelectionMask.Feather(working, (float)Math.Clamp(settings.Smooth, 0, 20)); working.Dispose(); working = next;
        }
        if (settings.EdgeAware)
        {
            // Fit on the existing fast guided-filter working grid; no model or full-frame float pyramid needed.
            var scale = Math.Min(1, 512.0 / Math.Max(mask.Width, mask.Height));
            var w = Math.Max(1, (int)Math.Round(mask.Width * scale)); var h = Math.Max(1, (int)Math.Round(mask.Height * scale));
            var coarse = new float[w * h]; var input = (byte*)working.GetPixels();
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
                coarse[y * w + x] = input[(long)Math.Min(mask.Height - 1, (int)(y / scale)) * working.RowBytes + Math.Min(mask.Width - 1, (int)(x / scale))] / 255f;
            var guided = GuidedFilter.Upsample(coarse, w, h, image, cancellation);
            // Preserve certain interiors and backgrounds. Only refine the uncertain boundary band.
            using var outer = SelectionMask.Expand(working, 8);
            using var inner = SelectionMask.Contract(working, 8);
            var data = (byte*)guided.GetPixels(); var outData = (byte*)outer.GetPixels(); var inData = inner == null ? null : (byte*)inner.GetPixels();
            var guidePixels = (byte*)image.GetPixels();
            var probes = new[] { (-4, 0), (4, 0), (0, -4), (0, 4) };
            for (var y = 0; y < mask.Height; y++) for (var x = 0; x < mask.Width; x++)
            {
                var i = (long)y * guided.RowBytes + x;
                var edge = false;
                for (var channel = 0; channel < 3 && !edge; channel++)
                {
                    var lo = 255; var hi = 0;
                    foreach (var (dx, dy) in probes)
                    {
                        var value = guidePixels[(long)Math.Clamp(y + dy, 0, image.Height - 1) * image.RowBytes + Math.Clamp(x + dx, 0, image.Width - 1) * 4 + channel];
                        lo = Math.Min(lo, value); hi = Math.Max(hi, value);
                    }
                    edge = hi - lo > 12;
                }
                if (!edge || outData[(long)y * outer.RowBytes + x] == 0 || inData != null && inData[(long)y * inner!.RowBytes + x] == 255) data[i] = input[(long)y * working.RowBytes + x];
            }
            working.Dispose(); working = guided;
        }
        cancellation.ThrowIfCancellationRequested();
        if (settings.Shift != 0)
        {
            var shifted = settings.Shift > 0 ? SelectionMask.Expand(working, Math.Min(100, settings.Shift)) : SelectionMask.Contract(working, Math.Min(100, -settings.Shift));
            working.Dispose(); working = shifted ?? Pixels.NewMask(mask.Width, mask.Height);
        }
        if (settings.Feather > 0) { var softened = SelectionMask.Feather(working, (float)Math.Clamp(settings.Feather, 0, 100)); working.Dispose(); working = softened; }
        var contrast = Math.Clamp(settings.Contrast, 0, 100) / 100;
        var pixels = (byte*)working.GetPixels();
        for (var y = 0; y < working.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < working.Width; x++)
            {
                var i = (long)y * working.RowBytes + x;
                pixels[i] = (byte)Math.Clamp((pixels[i] - 127.5) / Math.Max(.01, 1 - contrast) + 127.5, 0, 255);
            }
        }
        Pixels.Invalidate(working); return working;
        }
        catch { working.Dispose(); throw; }
    }

    public static SKBitmap Cutout(SKBitmap image, SKBitmap mask, bool decontaminate)
    {
        var result = Pixels.Clone(image); var src = (byte*)image.GetPixels(); var dst = (byte*)result.GetPixels(); var alpha = (byte*)mask.GetPixels();
        Parallel.For(0, image.Height, y =>
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = (long)y * result.RowBytes + x * 4; var a = alpha[(long)y * mask.RowBytes + x];
                if (decontaminate && a is > 0 and < 250)
                {
                    long nearest = -1; var best = int.MaxValue;
                    for (var dy = -6; dy <= 6; dy++) for (var dx = -6; dx <= 6; dx++)
                    {
                        int xx = x + dx, yy = y + dy, d = dx * dx + dy * dy;
                        if (xx < 0 || yy < 0 || xx >= image.Width || yy >= image.Height || d >= best || alpha[(long)yy * mask.RowBytes + xx] < 250) continue;
                        nearest = (long)yy * image.RowBytes + xx * 4; best = d;
                    }
                    if (nearest >= 0 && src[nearest + 3] > 0)
                        for (var c = 0; c < 3; c++) dst[i + c] = (byte)(src[nearest + c] * dst[i + 3] / src[nearest + 3]);
                }
                for (var c = 0; c < 4; c++) dst[i + c] = (byte)((dst[i + c] * a + 127) / 255);
            }
        });
        return result;
    }
}
