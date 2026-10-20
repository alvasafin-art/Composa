namespace Composa.Filters;

public sealed partial record HueSaturationAdjustment
{
    public HueBand BandFor(HueRange range)
    {
        if (range is < HueRange.Reds or > HueRange.Magentas) throw new ArgumentOutOfRangeException(nameof(range));
        var index = (int)range - 1;
        return Bands != null && index < Bands.Length ? Bands[index].Normalized() : HueBand.Default(range);
    }
    public HueSaturationAdjustment WithBand(HueRange range, HueBand band)
    {
        var bands = Enumerable.Range(1, 6).Select(i => BandFor((HueRange)i)).ToArray();
        if (range is < HueRange.Reds or > HueRange.Magentas) throw new ArgumentOutOfRangeException(nameof(range));
        bands[(int)range - 1] = band.Normalized(); return this with { Bands = bands };
    }

    internal FloatPixelOp CreateColorOp()
    {
        static double Safe(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : 0;
        var shifts = new HslShift[7];
        for (var i = 0; i < shifts.Length; i++)
        {
            var s = Shifts != null && i < Shifts.Length ? Shifts[i] : default;
            shifts[i] = new(Colorize && i == 0 ? HueBand.Wrap(s.Hue) : Safe(s.Hue, -180, 180), Safe(s.Saturation, -100, 100), Safe(s.Lightness, -100, 100));
        }
        var bands = Enumerable.Range(1, 6).Select(i => BandFor((HueRange)i)).ToArray();
        var active = Enumerable.Range(1, 6).Where(i => !shifts[i].IsZero).ToArray();
        var colorize = Colorize; var master = shifts[0];
        return (ref double r, ref double g, ref double b, int _, int _) =>
        {
            ColorMath.RgbToHsl(r, g, b, out var h, out var s, out var l);
            if (colorize)
            {
                h = master.Hue;
                s = master.Saturation >= 0 ? .25 + master.Saturation / 100 * .75 : .25 * (1 + master.Saturation / 100);
            }
            else
            {
                double hueShift = 0, saturationShift = 0, lightnessShift = 0;
                // All bands classify the original color, before any local shift. Accumulating their shifts also
                // keeps overlapping custom bands independent of their order in the array.
                var chroma = Math.Min(1, s * 4);
                foreach (var i in active)
                {
                    var weight = bands[i - 1].Weight(h) * chroma; var shift = shifts[i];
                    hueShift += shift.Hue * weight; saturationShift += shift.Saturation * weight; lightnessShift += shift.Lightness * weight;
                }
                h = HueBand.Wrap(h + hueShift + master.Hue);
                s = ApplySaturation(ApplySaturation(s, Math.Clamp(saturationShift, -100, 100)), master.Saturation);
                l = ApplyLightness(l, Math.Clamp(lightnessShift, -100, 100));
            }
            l = ApplyLightness(l, master.Lightness); ColorMath.HslToRgb(h, s, l, out r, out g, out b);
        };
    }
}
