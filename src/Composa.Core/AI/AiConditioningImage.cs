using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Color continuation for missing pixels; never an output repair or a document edit.</summary>
public static class AiConditioningImage
{
    public static SKBitmap Continue(SKBitmap reference, SKBitmap excluded)
    {
        if (reference.Width != excluded.Width || reference.Height != excluded.Height) throw new ArgumentException("Image and mask must share coordinates.");
        var scale = Math.Min(1.0, 1024.0 / Math.Max(reference.Width, reference.Height));
        var w = Math.Max(1, (int)Math.Ceiling(reference.Width * scale)); var h = Math.Max(1, (int)Math.Ceiling(reference.Height * scale));
        using var reduced = Pixels.NewColor(w, h);
        using (var canvas = new SKCanvas(reduced)) canvas.DrawImage(Pixels.ImageOf(reference), new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear));
        var blocked = new bool[w * h];
        for (var y = 0; y < reference.Height; y++) for (var x = 0; x < reference.Width; x++)
            if (excluded.GetPixel(x, y).Alpha > 0 || reference.GetPixel(x, y).Alpha < 255)
                blocked[Math.Min(h - 1, (int)(y * scale)) * w + Math.Min(w - 1, (int)(x * scale))] = true;
        // Linear reduction also samples neighbouring cells. Reject that footprint
        // so opaque black damage cannot leak into a supposedly clean color seed.
        var footprint = (bool[])blocked.Clone();
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) if (blocked[y * w + x])
            for (var yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
            for (var xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++) footprint[yy * w + xx] = true;
        if (footprint.Any(value => !value)) blocked = footprint;
        var nearest = new int[w * h]; var distance = new int[w * h];
        for (var i = 0; i < nearest.Length; i++) { nearest[i] = blocked[i] ? -1 : i; distance[i] = blocked[i] ? int.MaxValue / 2 : 0; }
        void Relax(int i, int other)
        { if (nearest[other] >= 0 && distance[other] + 1 < distance[i]) { distance[i] = distance[other] + 1; nearest[i] = nearest[other]; } }
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var i = y * w + x; if (x > 0) Relax(i, i - 1); if (y > 0) Relax(i, i - w); }
        for (var y = h - 1; y >= 0; y--) for (var x = w - 1; x >= 0; x--) { var i = y * w + x; if (x + 1 < w) Relax(i, i + 1); if (y + 1 < h) Relax(i, i + w); }
        var result = Pixels.Clone(reference);
        for (var y = 0; y < result.Height; y++) for (var x = 0; x < result.Width; x++)
        {
            if (excluded.GetPixel(x, y).Alpha == 0 && reference.GetPixel(x, y).Alpha == 255) continue;
            var n = nearest[Math.Min(h - 1, (int)(y * scale)) * w + Math.Min(w - 1, (int)(x * scale))];
            result.SetPixel(x, y, n < 0 ? new SKColor(127, 127, 127) : reduced.GetPixel(n % w, n / w).WithAlpha(255));
        }
        Pixels.Invalidate(result); return result;
    }
}
