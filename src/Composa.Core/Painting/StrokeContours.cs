using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>Shape-based interpolation of two-tone contours across a narrow scratch.</summary>
internal static unsafe class StrokeContours
{
    public static SKBitmap? TryFill(SKBitmap source, SKBitmap mask, SKBitmap? excluded, SKRectI hole)
    {
        bool vertical = hole.Width > hole.Height;
        int width = vertical ? hole.Width : hole.Height, length = vertical ? hole.Height : hole.Width;
        if (length > 12 || width < Math.Max(16, length * 3)) return null;
        var pixels = new PixelBuffer(source);
        var selection = (byte*)mask.GetPixels(); var stride = mask.RowBytes;
        var banned = (byte*)(excluded ?? mask).GetPixels(); var excludeStride = (excluded ?? mask).RowBytes;
        SKPointI Point(int u, int v) => vertical ? new(hole.Left + u, hole.Top + v) : new(hole.Left + v, hole.Top + u);
        var first = new int[width]; var last = new int[width];
        var top = new PixelValue[width]; var bottom = new PixelValue[width];
        var levels = new float[width * 2];
        static float Luma(PixelValue p) => .2126f * p.R + .7152f * p.G + .0722f * p.B;
        for (var u = 0; u < width; u++)
        {
            int start = length, end = -1, count = 0;
            for (var v = 0; v < length; v++)
            {
                var p = Point(u, v);
                if (selection[(long)p.Y * stride + p.X] == 0) continue;
                start = Math.Min(start, v); end = v; count++;
            }
            if (count == 0 || end - start + 1 != count) return null;
            first[u] = start; last[u] = end;
            var a = Point(u, start - 1); var b = Point(u, end + 1);
            if ((uint)a.X >= pixels.Width || (uint)a.Y >= pixels.Height || (uint)b.X >= pixels.Width || (uint)b.Y >= pixels.Height
                || banned[(long)a.Y * excludeStride + a.X] != 0 || banned[(long)b.Y * excludeStride + b.X] != 0) return null;
            top[u] = pixels.Read(a.X, a.Y); bottom[u] = pixels.Read(b.X, b.Y);
            if (top[u].A < .999f || bottom[u].A < .999f) return null;
            levels[u] = Luma(top[u]); levels[u + width] = Luma(bottom[u]);
        }
        var ordered = levels.Order().ToArray();
        float low = ordered[ordered.Length / 10], high = ordered[ordered.Length * 9 / 10];
        if (high - low < .32f) return null;
        var threshold = (low + high) * .5f;
        PixelValue dark = default, light = default;
        int darkCount = 0, lightCount = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            var color = i < width ? top[i] : bottom[i - width];
            if (levels[i] < low + .1f) { dark += color; darkCount++; }
            if (levels[i] > high - .1f) { light += color; lightCount++; }
        }
        if (darkCount + lightCount < .78f * levels.Length) return null;
        dark *= 1f / darkCount; light *= 1f / lightCount;

        // Fractional zero crossings locate the ink edges between pixel centers.
        float[]? Distances(int origin)
        {
            var crossings = new List<float>();
            for (var i = 1; i < width; i++)
            {
                float a = levels[origin + i - 1], b = levels[origin + i];
                if ((a < threshold) != (b < threshold)) crossings.Add(i - 1 + (threshold - a) / (b - a));
            }
            if (crossings.Count < 2) return null;
            var distance = new float[width]; var j = 0;
            for (var i = 0; i < width; i++)
            {
                while (j + 1 < crossings.Count && Math.Abs(crossings[j + 1] - i) < Math.Abs(crossings[j] - i)) j++;
                distance[i] = Math.Abs(crossings[j] - i) * (levels[origin + i] < threshold ? -1 : 1);
            }
            return distance;
        }
        var upper = Distances(0); var lower = Distances(width);
        if (upper == null || lower == null) return null;
        var result = Pixels.Clone(source); var output = new PixelBuffer(result);
        for (var u = 0; u < width; u++)
        for (var v = first[u]; v <= last[u]; v++)
        {
            var t = (float)(v - first[u] + 1) / (last[u] - first[u] + 2);
            var boundary = upper[u] * (1 - t) + lower[u] * t;
            var background = Math.Clamp(boundary + .5f, 0, 1);
            var a = top[u]; var b = bottom[u];
            var ink = (levels[u] <= low + .1f ? a : dark) * (1 - t) + (levels[width + u] <= low + .1f ? b : dark) * t;
            var paper = (levels[u] >= high - .1f ? a : light) * (1 - t) + (levels[width + u] >= high - .1f ? b : light) * t;
            var p = Point(u, v); var coverage = selection[(long)p.Y * stride + p.X] / 255f;
            output.Write(p.X, p.Y, pixels.Read(p.X, p.Y) * (1 - coverage) + (ink * (1 - background) + paper * background) * coverage);
        }
        Pixels.Invalidate(result);
        return result;
    }
}
