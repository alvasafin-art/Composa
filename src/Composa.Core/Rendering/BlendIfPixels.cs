using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

internal static class BlendIfPixels
{
    internal static float Coverage(PixelValue source, PixelValue backdrop, BlendIfSettings settings)
    {
        if (settings.IsIdentity) return 1;
        var s = source.Straight; var d = backdrop.Straight;
        static double Weight(BlendChannelRanges range, double source, double backdrop, float alpha) => range.IsIdentity ? 1
            : Math.Min(range.ThisLayer.Coverage(source * 255), 1 - alpha * (1 - range.Underlying.Coverage(backdrop * 255)));
        var gray = Weight(settings.Gray, .3 * s.R + .59 * s.G + .11 * s.B, .3 * d.R + .59 * d.G + .11 * d.B, d.A);
        return (float)Math.Min(gray, Math.Min(Weight(settings.Red, s.R, d.R, d.A), Math.Min(Weight(settings.Green, s.G, d.G, d.A), Weight(settings.Blue, s.B, d.B, d.A))));
    }

    /// <summary>Multiplies an uncommitted source or Alpha8 mixing coverage; the two color inputs stay unchanged otherwise.</summary>
    internal static unsafe void Apply(SKBitmap target, SKBitmap source, SKBitmap backdrop, BlendIfSettings settings)
    {
        if (settings.IsIdentity) return;
        if (target.ColorType == SKColorType.RgbaF32 || source.ColorType == SKColorType.RgbaF32 || backdrop.ColorType == SKColorType.RgbaF32)
        {
            var colors = new PixelBuffer(source); var below = new PixelBuffer(backdrop);
            if (target.ColorType == SKColorType.Alpha8)
            {
                var data = target.GetPixelSpan();
                for (var y = 0; y < target.Height; y++)
                for (var x = 0; x < target.Width; x++)
                {
                    var at = y * target.RowBytes + x;
                    data[at] = (byte)MathF.Round(data[at] * Coverage(colors.Read(x, y), below.Read(x, y), settings));
                }
            }
            else
            {
                var output = new PixelBuffer(target);
                for (var y = 0; y < target.Height; y++)
                for (var x = 0; x < target.Width; x++)
                    output.Write(x, y, output.Read(x, y) * Coverage(colors.Read(x, y), below.Read(x, y), settings));
            }
            return;
        }
        var active = new[] { settings.Gray, settings.Red, settings.Green, settings.Blue }
            .Select((range, index) => (Range: range, Channel: index)).Where(x => !x.Range.IsIdentity).ToArray();
        var mask = target.ColorType == SKColorType.Alpha8;
        var targetData = (byte*)target.GetPixels();
        var sourceData = (byte*)source.GetPixels();
        var backdropData = (byte*)backdrop.GetPixels();
        static double Value(byte* p, int channel)
        {
            if (p[3] == 0) return 0;
            var scale = 255.0 / p[3];
            return channel == 0 ? (.3 * p[0] + .59 * p[1] + .11 * p[2]) * scale : p[channel - 1] * scale;
        }
        for (var y = 0; y < target.Height; y++)
        {
            var t = targetData + (long)y * target.RowBytes;
            var s = sourceData + (long)y * source.RowBytes;
            var d = backdropData + (long)y * backdrop.RowBytes;
            for (var x = 0; x < target.Width; x++, t += mask ? 1 : 4, s += 4, d += 4)
            {
                double coverage = 1;
                foreach (var (range, channel) in active)
                {
                    coverage = Math.Min(coverage, range.ThisLayer.Coverage(Value(s, channel)));
                    // A missing backdrop cannot knock out a source: fade its restriction by backdrop alpha.
                    coverage = Math.Min(coverage, 1 - d[3] / 255.0 * (1 - range.Underlying.Coverage(Value(d, channel))));
                }
                for (var c = 0; c < (mask ? 1 : 4); c++) t[c] = (byte)Math.Clamp(Math.Round(t[c] * coverage), 0, 255);
            }
        }
    }
}
