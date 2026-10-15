using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

internal static class BlendIfPixels
{
    /// <summary>Multiplies an uncommitted source or Alpha8 mixing coverage; the two color inputs stay unchanged otherwise.</summary>
    internal static unsafe void Apply(SKBitmap target, SKBitmap source, SKBitmap backdrop, BlendIfSettings settings)
    {
        if (settings.IsIdentity) return;
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
