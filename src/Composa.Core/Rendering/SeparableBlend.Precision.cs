using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

public static partial class SeparableBlend
{
    private static void CompositeHighPrecision(SKBitmap destination, SKBitmap source, BlendMode mode, double opacity, SKPoint origin, double step)
    {
        var blend = FunctionFor(mode);
        var input = new PixelBuffer(source); var output = new PixelBuffer(destination);
        var scale = (float)Math.Clamp(opacity, 0, 1);
        void Row(int y)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var s = input.Read(x, y) * scale;
                if (s.A <= 0) continue;
                var d = output.Read(x, y);
                var cs = s.Straight; var cb = d.Straight;
                if (mode == BlendMode.Dissolve)
                {
                    var hash = unchecked((uint)(int)Math.Floor(origin.X + x * step) * 0x1f123bb5u ^ (uint)(int)Math.Floor(origin.Y + y * step) * 0x5f356495u);
                    hash ^= hash >> 16; hash *= 0x7feb352du; hash ^= hash >> 15;
                    if ((hash & 0xffffff) / 16777216f >= s.A) continue;
                    s = cs with { A = 1 };
                }
                var backdrop = mode == BlendMode.DarkerColor ? cb.R + cb.G + cb.B <= cs.R + cs.G + cs.B
                    : mode == BlendMode.LighterColor && cb.R + cb.G + cb.B >= cs.R + cs.G + cs.B;
                float B(float a, float b) => mode is BlendMode.DarkerColor or BlendMode.LighterColor ? backdrop ? a : b : blend(Math.Clamp(a, 0, 1), Math.Clamp(b, 0, 1));
                var both = s.A * d.A;
                output.Write(x, y, new(
                    s.R * (1 - d.A) + d.R * (1 - s.A) + both * B(cb.R, cs.R),
                    s.G * (1 - d.A) + d.G * (1 - s.A) + both * B(cb.G, cs.G),
                    s.B * (1 - d.A) + d.B * (1 - s.A) + both * B(cb.B, cs.B),
                    s.A + d.A - both));
            }
        }
        if ((long)source.Width * source.Height > 200_000) Parallel.For(0, source.Height, Row);
        else for (var y = 0; y < source.Height; y++) Row(y);
    }
}
