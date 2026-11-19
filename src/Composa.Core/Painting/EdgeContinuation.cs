using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>Match ordered profiles on opposing sides of a gap, then carry their aligned edges through the fill.</summary>
internal static unsafe class EdgeContinuation
{
    private readonly record struct Endpoint(int Start, int End, PixelValue First, PixelValue Last);

    public static void Refine(SKBitmap source, SKBitmap estimate, SKBitmap mask, SKBitmap? excluded,
        SKRectI hole, CancellationToken cancellation)
    {
        if ((long)hole.Width * hole.Height > 512 * 512) return;
        var input = new PixelBuffer(source);
        var output = new PixelBuffer(estimate);
        var selection = (byte*)mask.GetPixels();
        var maskStride = mask.RowBytes;
        var banned = (byte*)(excluded ?? mask).GetPixels();
        var excludeStride = (excluded ?? mask).RowBytes;
        var scores = new float[hole.Width * hole.Height];
        var prediction = new PixelValue[scores.Length];

        static float Error(PixelValue a, PixelValue b)
        {
            float r = a.R - b.R, g = a.G - b.G, blue = a.B - b.B;
            return (r * r + g * g + blue * blue) / 3;
        }

        void Bridge(bool vertical)
        {
            int width = vertical ? hole.Width : hole.Height, length = vertical ? hole.Height : hole.Width;
            if (width < 4 || length > width * 8) return;
            SKPointI Point(int u, int v) => vertical ? new(hole.Left + u, hole.Top + v) : new(hole.Left + v, hole.Top + u);
            bool Read(int u, int v, out PixelValue color)
            {
                var p = Point(u, v);
                color = default;
                if ((uint)p.X >= input.Width || (uint)p.Y >= input.Height || banned[(long)p.Y * excludeStride + p.X] != 0) return false;
                color = input.Read(p.X, p.Y);
                return color.A >= .99f;
            }

            var ends = new Endpoint[width];
            for (var u = 0; u < width; u++)
            {
                int first = length, last = -1, count = 0;
                for (var v = 0; v < length; v++)
                {
                    var p = Point(u, v);
                    if (selection[(long)p.Y * maskStride + p.X] == 0) continue;
                    first = Math.Min(first, v); last = v; count++;
                }
                // A single intact pair must bracket the whole run. Disjoint holes get independent texture synthesis.
                if (count == 0 || count > 192 || last - first + 1 != count ||
                    !Read(u, first - 1, out var a) || !Read(u, last + 1, out var b)) continue;
                ends[u] = new(first - 1, last + 1, a, b);
            }

            // Dynamic programming estimates a smoothly varying displacement between the two profiles.
            // The bound also limits ambiguity when several adjacent letters have similar stems.
            int reach = Math.Min(16, Math.Min(width / 4, length)), states = reach * 2 + 1;
            var previous = new float[states]; var next = new float[states];
            var back = new sbyte[width * states];
            for (var u = 0; u < width; u++)
            {
                if ((u & 31) == 0) cancellation.ThrowIfCancellationRequested();
                for (var state = 0; state < states; state++)
                {
                    int shift = state - reach, left = u - (int)MathF.Floor(shift * .5f), right = left + shift;
                    float loss = 0; int count = 0;
                    for (var k = -2; k <= 2; k++)
                    {
                        int i = left + k, j = right + k;
                        if ((uint)i >= width || (uint)j >= width || ends[i].End == 0 || ends[j].End == 0) continue;
                        loss += Math.Min(.1f, Error(ends[i].First, ends[j].Last)); count++;
                    }
                    loss = (count > 0 ? loss / count : .1f) + .00015f * Math.Abs(shift);
                    var cost = previous[state]; sbyte step = 0;
                    if (state > 0 && previous[state - 1] + .002f < cost) { step = -1; cost = previous[state - 1] + .002f; }
                    if (state + 1 < states && previous[state + 1] + .002f < cost) { step = 1; cost = previous[state + 1] + .002f; }
                    back[u * states + state] = step;
                    next[state] = cost + loss;
                }
                (previous, next) = (next, previous);
            }
            bool EndpointSample(float position, bool first, int outward, out PixelValue color)
            {
                color = default;
                if (position < 0 || position > width - 1) return false;
                int left = (int)position, right = Math.Min(left + 1, width - 1);
                var f = position - left;
                if (ends[left].End == 0 || !Read(left, first ? ends[left].Start - outward : ends[left].End + outward, out color)) return false;
                if (f == 0) return true;
                if (ends[right].End == 0 || !Read(right, first ? ends[right].Start - outward : ends[right].End + outward, out var neighbor)) return false;
                color = color * (1 - f) + neighbor * f;
                return true;
            }
            var best = Array.IndexOf(previous, previous.Min());
            var shifts = new int[width];
            for (var u = width - 1; u >= 0; u--)
            {
                shifts[u] = best - reach;
                best += back[u * states + best];
            }
            for (var u = 0; u < width; u++)
            {
                float shift = (shifts[Math.Max(0, u - 1)] + 2 * shifts[u] + shifts[Math.Min(width - 1, u + 1)]) * .25f;
                var chord = ends[u]; if (chord.End == 0) continue;
                var span = chord.End - chord.Start;
                for (var v = chord.Start + 1; v < chord.End; v++)
                {
                    var t = (float)(v - chord.Start) / span;
                    float i = u - t * shift, j = u + (1 - t) * shift;
                    if (!EndpointSample(i, true, 0, out var a) || !EndpointSample(j, false, 0, out var b)) continue;
                    // Unequal colors can describe a shading ramp. Abrupt changes immediately outside either
                    // endpoint instead make extrapolation unreliable, particularly across long gaps.
                    if (!EndpointSample(i - 2 * shift / span, true, 2, out var outerA) || !EndpointSample(j + 2 * shift / span, false, 2, out var outerB)) continue;
                    float roughness = Error(a, outerA) + Error(b, outerB);
                    float confidence = 1 / (1 + (Error(a, b) * 12 + roughness * 34 + .0018f * span) * Math.Max(.35f, span / 45f));
                    var p = Point(u, v);
                    var at = (p.Y - hole.Top) * hole.Width + p.X - hole.Left;
                    if (confidence <= .55f) continue;
                    var fromA = a; var fromB = b;
                    if (span > 12)
                    {
                        var depthA = v - chord.Start - 1; var depthB = chord.End - v - 1;
                        if (!EndpointSample(i - depthA * shift / span, true, depthA, out fromA) || Error(fromA, a) > .006f) fromA = a;
                        if (!EndpointSample(j + depthB * shift / span, false, depthB, out fromB) || Error(fromB, b) > .006f) fromB = b;
                    }
                    // Continuous confidence weights avoid a seam when two orientations are equally plausible.
                    var vote = MathF.Pow((confidence - .55f) / .45f, 4);
                    var blend = t * t * (3 - 2 * t);
                    prediction[at] += (fromA * (1 - blend) + fromB * blend) * vote;
                    scores[at] += vote;
                }
            }
        }
        Bridge(true); Bridge(false);
        for (var y = 0; y < hole.Height; y++)
        for (var x = 0; x < hole.Width; x++)
        {
            int i = y * hole.Width + x;
            if (scores[i] == 0) continue;
            var weight = Math.Min(1, MathF.Sqrt(MathF.Sqrt(scores[i])) * 1.26f);
            var current = output.Read(hole.Left + x, hole.Top + y);
            output.Write(hole.Left + x, hole.Top + y, current * (1 - weight) + prediction[i] * (weight / scores[i]));
        }
        Pixels.Invalidate(estimate);
    }
}
