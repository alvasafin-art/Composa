using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

/// <summary>Mixes a filter's replacement with its input. Opacity interpolates premultiplied RGBA, allowing
/// a blur to reduce alpha as well as spread it; drawing source-over would leave the sharp original underneath.</summary>
internal static class FilterBlending
{
    public static void Apply(SKBitmap input, SKBitmap result, int offsetX, int offsetY, double opacity, BlendMode blend)
    {
        if (opacity >= 1 && blend == BlendMode.Normal) return;
        var before = new PixelBuffer(input); var after = new PixelBuffer(result);
        var amount = (float)Math.Clamp(opacity, 0, 1);
        void Row(int y)
        {
            for (var x = 0; x < result.Width; x++)
            {
                var bx = x - offsetX; var by = y - offsetY;
                var b = bx >= 0 && by >= 0 && bx < input.Width && by < input.Height ? before.Read(bx, by) : default;
                var s = after.Read(x, y);
                if (blend == BlendMode.Dissolve)
                {
                    var hash = unchecked((uint)bx * 0x1f123bb5u ^ (uint)by * 0x5f356495u);
                    hash ^= hash >> 16; hash *= 0x7feb352du; hash ^= hash >> 15;
                    after.Write(x, y, (hash & 0xffffff) / 16777216f < amount ? s : b); continue;
                }
                if (blend != BlendMode.Normal && b.A > 0 && s.A > 0)
                {
                    var color = Color(b.Straight, s.Straight, blend);
                    s = (color with { A = 1 }) * s.A;
                }
                after.Write(x, y, b * (1 - amount) + s * amount);
            }
        }
        if ((long)result.Width * result.Height > 200_000) Parallel.For(0, result.Height, Row);
        else for (var y = 0; y < result.Height; y++) Row(y);
        Pixels.Invalidate(result);
    }

    internal static PixelValue Color(PixelValue b, PixelValue s, BlendMode mode)
    {
        if (mode == BlendMode.Hue) return SetLum(SetSat(s, Sat(b)), Lum(b));
        if (mode == BlendMode.Saturation) return SetLum(SetSat(b, Sat(s)), Lum(b));
        if (mode == BlendMode.Color) return SetLum(s, Lum(b));
        if (mode == BlendMode.Luminosity) return SetLum(b, Lum(s));
        if (mode == BlendMode.DarkerColor) return b.R + b.G + b.B <= s.R + s.G + s.B ? b : s;
        if (mode == BlendMode.LighterColor) return b.R + b.G + b.B >= s.R + s.G + s.B ? b : s;
        var custom = mode.IsCustom() ? SeparableBlend.FunctionFor(mode) : null;
        float Channel(float cb, float cs) => mode switch
        {
            BlendMode.Normal => cs,
            BlendMode.Multiply => cb * cs,
            BlendMode.Screen => cb + cs - cb * cs,
            BlendMode.Overlay => HardLight(cs, cb),
            BlendMode.HardLight => HardLight(cb, cs),
            BlendMode.Darken => Math.Min(cb, cs), BlendMode.Lighten => Math.Max(cb, cs),
            BlendMode.Difference => Math.Abs(cb - cs), BlendMode.Exclusion => cb + cs - 2 * cb * cs,
            BlendMode.ColorDodge => cb <= 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs)),
            BlendMode.ColorBurn => cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs),
            BlendMode.SoftLight => cs <= .5f ? cb - (1 - 2 * cs) * cb * (1 - cb)
                : cb + (2 * cs - 1) * ((cb <= .25f ? ((16 * cb - 12) * cb + 4) * cb : MathF.Sqrt(cb)) - cb),
            _ => custom?.Invoke(cb, cs) ?? cs
        };
        return new(Channel(b.R, s.R), Channel(b.G, s.G), Channel(b.B, s.B), 1);
    }

    private static float HardLight(float b, float s) => s <= .5f ? 2 * b * s : 1 - 2 * (1 - b) * (1 - s);
    private static float Lum(PixelValue c) => .3f * c.R + .59f * c.G + .11f * c.B;
    private static float Sat(PixelValue c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
    private static PixelValue SetSat(PixelValue c, float saturation)
    {
        var min = Math.Min(c.R, Math.Min(c.G, c.B)); var range = Sat(c);
        if (range <= 0) return new(0, 0, 0, 1);
        return new((c.R - min) * saturation / range, (c.G - min) * saturation / range, (c.B - min) * saturation / range, 1);
    }
    private static PixelValue SetLum(PixelValue c, float luminosity)
    {
        var delta = luminosity - Lum(c); c = new(c.R + delta, c.G + delta, c.B + delta, 1);
        var min = Math.Min(c.R, Math.Min(c.G, c.B)); var max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (min < 0) c = new(luminosity + (c.R - luminosity) * luminosity / (luminosity - min),
            luminosity + (c.G - luminosity) * luminosity / (luminosity - min), luminosity + (c.B - luminosity) * luminosity / (luminosity - min), 1);
        if (max > 1) c = new(luminosity + (c.R - luminosity) * (1 - luminosity) / (max - luminosity),
            luminosity + (c.G - luminosity) * (1 - luminosity) / (max - luminosity), luminosity + (c.B - luminosity) * (1 - luminosity) / (max - luminosity), 1);
        return c;
    }
}
