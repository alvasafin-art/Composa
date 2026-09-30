using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Small deterministic finishing passes that make generated pixels agree with their immediate surroundings.</summary>
public static class AiResultPostprocessor
{
    /// <summary>
    /// A stitched image already contains the soft transition. Reveal its changed pixels at full coverage,
    /// and leave identical pixels to the original layer. Multiplying by the soft selection again darkens
    /// or weakens the transition. The editable layer mask represents coverage, not a second blend.
    /// </summary>
    public static SKBitmap CompositedMask(SKBitmap generated, SKBitmap context, SKBitmap? support = null, SKBitmap? alphaEditRegion = null)
    {
        if (generated.Width != context.Width || generated.Height != context.Height)
            throw new ArgumentException("A composited result must match the original canvas dimensions.");
        if (support != null && (support.Width != context.Width || support.Height != context.Height)) throw new ArgumentException("Mask support must match the canvas.");
        if (alphaEditRegion != null && (alphaEditRegion.Width != context.Width || alphaEditRegion.Height != context.Height)) throw new ArgumentException("Alpha edit region must match the canvas.");
        var mask = Pixels.NewMask(context.Width, context.Height);
        unsafe
        {
            var result = (byte*)mask.GetPixels(); var original = (byte*)context.GetPixels(); var output = (byte*)generated.GetPixels();
            var coverage = support == null ? null : (byte*)support.GetPixels();
            var alphaRegion = alphaEditRegion == null ? null : (byte*)alphaEditRegion.GetPixels();
            for (var y = 0; y < context.Height; y++)
                for (var x = 0; x < context.Width; x++)
                {
                    if (coverage != null && coverage[(long)y * support!.RowBytes+x] == 0) continue;
                    var a = original+(long)y*context.RowBytes+x*4; var b = output+(long)y*generated.RowBytes+x*4;
                    var alphaEditable = alphaRegion == null || alphaRegion[(long)y*alphaEditRegion!.RowBytes+x] != 0;
                    // Comfy IMAGE nodes discard alpha. An unchanged transparent black pixel
                    // or unchanged straight color must not become opaque outside the edit.
                    var sameColor = a[3] == b[3] ? (*(uint*)a & 0xFFFFFF) == (*(uint*)b & 0xFFFFFF)
                        : a[3] == 0 ? (*(uint*)b & 0xFFFFFF) == 0 : b[3] == 0 ? (*(uint*)a & 0xFFFFFF) == 0
                        : Math.Abs(a[0]*b[3]-b[0]*a[3]) <= Math.Max(a[3],b[3])
                            && Math.Abs(a[1]*b[3]-b[1]*a[3]) <= Math.Max(a[3],b[3])
                            && Math.Abs(a[2]*b[3]-b[2]*a[3]) <= Math.Max(a[3],b[3]);
                    result[(long)y*mask.RowBytes+x] = !sameColor || alphaEditable && a[3] != b[3] ? (byte)255 : (byte)0;
                }
        }
        Pixels.Invalidate(mask); return mask;
    }

    public static SKBitmap MatchRemoval(SKBitmap generated, SKBitmap context, SKBitmap mask, long seed)
    {
        if (generated.Width != context.Width || generated.Height != context.Height || mask.Width != context.Width || mask.Height != context.Height)
            return Pixels.Clone(generated);

        var bounds = SelectionBounds(mask);
        if (bounds.IsEmpty) return Pixels.Clone(generated);
        var radius = Math.Clamp(Math.Max(bounds.Width, bounds.Height) / 3, 8, 96);
        var ring = new SKRectI(Math.Max(0, bounds.Left - radius), Math.Max(0, bounds.Top - radius),
            Math.Min(context.Width, bounds.Right + radius), Math.Min(context.Height, bounds.Bottom + radius));
        var outside = StatsOf(context, mask, ring, selected: false);
        // Compare the SAME surrounding pixels, not the reconstructed patch with an unrelated
        // scene average. A stitched result already matches there; shifting it creates a seam.
        var inside = StatsOf(generated, mask, ring, selected: false);
        if (outside.Count == 0 || inside.Count == 0) return Pixels.Clone(generated);

        var result = Pixels.Clone(generated);
        unsafe
        {
            var source = (byte*)generated.GetPixels();
            var destination = (byte*)result.GetPixels();
            var coverage = (byte*)mask.GetPixels();
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                var sourceRow = source + (long)y * generated.RowBytes;
                var destinationRow = destination + (long)y * result.RowBytes;
                var maskRow = coverage + (long)y * mask.RowBytes;
                for (var x = bounds.Left; x < bounds.Right; x++)
                {
                    var amount = maskRow[x];
                    if (amount == 0) continue;
                    var offset = x * 4;
                    var alpha = sourceRow[offset + 3];
                    if (alpha == 0) continue;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var contrast = outside.Deviation(channel) < 1 && inside.Deviation(channel) < 1 ? 1
                            : Math.Clamp(outside.Deviation(channel) / Math.Max(inside.Deviation(channel), 1), 0.72, 1.38);
                        var shift = Math.Clamp(outside.Mean(channel) - inside.Mean(channel), -18, 18);
                        var straight = sourceRow[offset + channel] * 255.0 / alpha;
                        var matched = inside.Mean(channel) + shift + (straight - inside.Mean(channel)) * contrast;
                        var noise = Math.Max(0, outside.Detail - inside.Detail) * HashNoise(x, y, seed) * 0.45;
                        var adjusted = Math.Clamp((int)Math.Round(matched + noise), 0, 255);
                        destinationRow[offset + channel] = Blend(sourceRow[offset + channel], (byte)((adjusted * alpha + 127) / 255), amount);
                    }
                }
            }
        }
        Pixels.Invalidate(result);
        return result;
    }

    private readonly record struct Statistics(long Count, double Red, double Green, double Blue,
        double RedSquared, double GreenSquared, double BlueSquared, double Detail)
    {
        public double Mean(int channel) => channel switch { 0 => Red / Count, 1 => Green / Count, _ => Blue / Count };
        public double Deviation(int channel)
        {
            var sum = channel switch { 0 => RedSquared, 1 => GreenSquared, _ => BlueSquared };
            var mean = Mean(channel);
            return Math.Sqrt(Math.Max(0, sum / Count - mean * mean));
        }
    }

    private static Statistics StatsOf(SKBitmap image, SKBitmap mask, SKRectI area, bool selected)
    {
        long count = 0;
        double r = 0, g = 0, b = 0, rr = 0, gg = 0, bb = 0, detail = 0;
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                var covered = mask.GetPixel(x, y).Alpha >= 128;
                if (covered != selected) continue;
                var color = image.GetPixel(x, y);
                if (color.Alpha == 0) continue;
                count++; r += color.Red; g += color.Green; b += color.Blue;
                rr += color.Red * color.Red; gg += color.Green * color.Green; bb += color.Blue * color.Blue;
                if (x > area.Left)
                {
                    var left = image.GetPixel(x - 1, y);
                    detail += Math.Abs(Luma(color) - Luma(left));
                }
            }
        return new(count, r, g, b, rr, gg, bb, count == 0 ? 0 : detail / count);
    }

    private static SKRectI SelectionBounds(SKBitmap mask)
    {
        int left = mask.Width, top = mask.Height, right = 0, bottom = 0;
        for (var y = 0; y < mask.Height; y++)
            for (var x = 0; x < mask.Width; x++)
                if (mask.GetPixel(x, y).Alpha > 0)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                }
        return left >= right || top >= bottom ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

    private static double Luma(SKColor color) => (color.Red * 54 + color.Green * 183 + color.Blue * 19) / 256.0;

    private static double HashNoise(int x, int y, long seed)
    {
        unchecked
        {
            var value = (uint)x * 0x85EBCA6Bu ^ (uint)y * 0xC2B2AE35u ^ (uint)seed;
            value ^= value >> 15; value *= 0x2C1B3C6Du; value ^= value >> 12;
            return (value & 0xFFFF) / 32767.5 - 1;
        }
    }

    private static byte Blend(byte from, byte to, byte amount) => (byte)((from * (255 - amount) + to * amount + 127) / 255);
}
