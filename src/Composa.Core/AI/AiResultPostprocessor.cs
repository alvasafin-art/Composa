using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Small deterministic finishing passes that make generated pixels agree with their immediate surroundings.</summary>
public static class AiResultPostprocessor
{
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
        var inside = StatsOf(generated, mask, bounds, selected: true);
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
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var contrast = Math.Clamp(outside.Deviation(channel) / Math.Max(inside.Deviation(channel), 1), 0.72, 1.38);
                        var matched = outside.Mean(channel) + (sourceRow[offset + channel] - inside.Mean(channel)) * contrast;
                        var noise = Math.Max(0, outside.Detail - inside.Detail) * HashNoise(x, y, seed) * 0.45;
                        var adjusted = Math.Clamp((int)Math.Round(matched + noise), 0, 255);
                        destinationRow[offset + channel] = Blend(sourceRow[offset + channel], (byte)adjusted, amount);
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
