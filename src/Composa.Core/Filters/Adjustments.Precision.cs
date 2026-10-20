using System.Runtime.CompilerServices;
using Composa.ColorManagement;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

internal delegate void FloatPixelOp(ref double r, ref double g, ref double b, int x, int y);

public abstract partial record Adjustment
{
    private static readonly ConditionalWeakTable<Adjustment, FloatPixelOp> FloatOps = new();
    private static readonly ConditionalWeakTable<Adjustment, ConditionalWeakTable<RgbProfile, FloatPixelOp>> ProfileOps = new();

    private void ApplyHighPrecision(SKBitmap bitmap, SKRectI area, double originX, double originY, double step, bool parallel)
    {
        Adjustment settings = step > 1.01 ? this switch
        {
            GrainAdjustment grain => grain with { Amount = grain.Amount * Math.Min(1, Math.Max(grain.Size, 1) / step) },
            AddNoiseAdjustment noise => noise with { Amount = noise.Amount / step },
            _ => this
        } : this;
        var op = settings is ExposureAdjustment or ColorLookupAdjustment
            ? ProfileOps.GetValue(settings, _ => new()).GetValue(Pixels.ProfileOf(bitmap), p => settings is ColorLookupAdjustment lookup ? lookup.CreateColorOp(p) : HighPrecisionAdjustment.Create(settings, p.Transfer))
            : FloatOps.GetValue(settings, s => HighPrecisionAdjustment.Create(s));
        var pixels = new PixelBuffer(bitmap);
        void Row(int y)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                var value = pixels.Read(x, y).Straight;
                if (value.A <= 0) continue;
                double r = value.R, g = value.G, b = value.B;
                op(ref r, ref g, ref b, (int)Math.Floor(originX + x * step), (int)Math.Floor(originY + y * step));
                pixels.Write(x, y, new PixelValue((float)Math.Clamp(r, 0, 1), (float)Math.Clamp(g, 0, 1), (float)Math.Clamp(b, 0, 1), value.A).Premultiplied);
            }
        }
        if (parallel && (long)area.Width * area.Height > 200_000) Parallel.For(area.Top, area.Bottom, Row);
        else for (var y = area.Top; y < area.Bottom; y++) Row(y);
    }
}

/// <summary>Operations on normalized straight colors. No byte LUT, half-float or display copy in this path.</summary>
internal static class HighPrecisionAdjustment
{
    internal static FloatPixelOp Create(Adjustment settings, RgbTransfer? transfer = null) => settings switch
    {
        InvertAdjustment => (ref double r, ref double g, ref double b, int _, int _) => { r = 1 - r; g = 1 - g; b = 1 - b; },
        BrightnessContrastAdjustment s => MapChannels((v, _) =>
        {
            var contrast = Math.Clamp(s.Contrast, -100, 100) / 100;
            var slope = contrast >= 0 ? 1 / Math.Max(.004, 1 - contrast) : 1 + contrast;
            return (v - .5) * slope + .5 + Math.Clamp(s.Brightness, -100, 100) / 200;
        }),
        LevelsAdjustment s => MapChannels((v, channel) => s.Ranges[0].Map(s.Ranges[channel + 1].Map(v * 255)) / 255),
        CurvesAdjustment s => MapChannels((v, channel) => s.Value(s.Value(v * 255, channel + 1), 0) / 255),
        ExposureAdjustment s => MapChannels((v, channel) => (transfer ?? RgbProfile.Srgb.Transfer).Encode(Math.Pow(Math.Max(0,
            (transfer ?? RgbProfile.Srgb.Transfer).Decode(v, channel) * Math.Pow(2, Math.Clamp(s.Exposure, -20, 20)) + s.Offset), 1 / Math.Clamp(s.Gamma, .01, 9.99)), channel)),
        GradientMapAdjustment s => GradientMap(s),
        HueSaturationAdjustment s => HueSaturation(s),
        BlackAndWhiteAdjustment s => BlackAndWhite(s),
        ColorBalanceAdjustment s => ColorBalance(s),
        ChannelMixerAdjustment s => s.CreateColorOp(),
        GrainAdjustment s => Grain(s),
        AddNoiseAdjustment s => Noise(s),
        _ => throw new NotSupportedException($"{settings.DisplayName} does not yet have a high precision pixel operation.")
    };

    private static FloatPixelOp MapChannels(Func<double, int, double> map)
    {
        const int last = ushort.MaxValue;
        var tables = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            tables[c] = new float[last + 1];
            for (var i = 0; i <= last; i++) tables[c][i] = (float)Math.Clamp(map(i / (double)last, c), 0, 1);
        }
        double Sample(double value, int channel)
        {
            var at = Math.Clamp(value, 0, 1) * last;
            var index = (int)at;
            return index >= last ? tables[channel][last] : tables[channel][index] + (tables[channel][index + 1] - tables[channel][index]) * (at - index);
        }
        return (ref double r, ref double g, ref double b, int _, int _) => { r = Sample(r, 0); g = Sample(g, 1); b = Sample(b, 2); };
    }

    private static FloatPixelOp GradientMap(GradientMapAdjustment s)
    {
        var dark = new SKColor(s.Reversed ? s.Highlights : s.Shadows);
        var light = new SKColor(s.Reversed ? s.Shadows : s.Highlights);
        return (ref double r, ref double g, ref double b, int _, int _) =>
        {
            var luma = (r * 54 + g * 183 + b * 19) / 256;
            r = (dark.Red + (light.Red - dark.Red) * luma) / 255;
            g = (dark.Green + (light.Green - dark.Green) * luma) / 255;
            b = (dark.Blue + (light.Blue - dark.Blue) * luma) / 255;
        };
    }

    private static FloatPixelOp HueSaturation(HueSaturationAdjustment settings) => settings.CreateColorOp();

    private static FloatPixelOp BlackAndWhite(BlackAndWhiteAdjustment settings)
    {
        var weights = settings.Weights.Select(v => (float)(Math.Clamp(v, BlackAndWhiteAdjustment.MinWeight, BlackAndWhiteAdjustment.MaxWeight) / 100)).ToArray();
        return (ref double r, ref double g, ref double b, int _, int _) =>
        {
            var gray = BlackAndWhiteAdjustment.Gray((float)r, (float)g, (float)b, weights);
            if (!settings.Tint || settings.TintSaturation <= 0) { r = g = b = gray; return; }
            ColorMath.HslToRgb(((settings.TintHue % 360) + 360) % 360, Math.Clamp(settings.TintSaturation, 0, 100) / 100, gray, out r, out g, out b);
        };
    }

    private static FloatPixelOp ColorBalance(ColorBalanceAdjustment settings)
    {
        float[] Unit(double[] values) => values.Select(v => (float)(Math.Clamp(v, ColorBalanceAdjustment.MinShift, ColorBalanceAdjustment.MaxShift) / 100)).ToArray();
        var shadows = Unit(settings.Shadows); var midtones = Unit(settings.Midtones); var highlights = Unit(settings.Highlights);
        return (ref double r, ref double g, ref double b, int _, int _) =>
        {
            Span<float> c = [(float)r, (float)g, (float)b];
            var before = .299f * c[0] + .587f * c[1] + .114f * c[2];
            for (var i = 0; i < 3; i++)
            {
                ColorBalanceAdjustment.TonalWeights(c[i], out var s, out var m, out var h);
                c[i] = Math.Clamp(c[i] + shadows[i] * s + midtones[i] * m + highlights[i] * h, 0, 1);
            }
            var after = .299f * c[0] + .587f * c[1] + .114f * c[2];
            if (settings.PreserveLuminosity && after > .0001f)
                for (var i = 0; i < 3; i++) c[i] = Math.Clamp(c[i] * before / after, 0, 1);
            r = c[0]; g = c[1]; b = c[2];
        };
    }

    private static FloatPixelOp Grain(GrainAdjustment settings)
    {
        var amount = (float)(Math.Clamp(settings.Amount, 0, 100) / 100 * 96 / 255);
        var size = (float)Math.Clamp(settings.Size, .5, 20);
        var detail = Math.Max(.5f, size * .35f);
        var rough = (float)(Math.Clamp(settings.Roughness, 0, 100) / 100);
        return (ref double r, ref double g, ref double b, int x, int y) =>
        {
            var smooth = GrainAdjustment.Smooth(x / size, y / size, settings.Seed) * 1.6f;
            var fine = GrainAdjustment.Smooth(x / detail, y / detail, settings.Seed ^ 0x9E3779B9u) * 1.6f;
            var luma = (r * 54 + g * 183 + b * 19) / 256;
            var delta = (smooth + (fine - smooth) * rough) * amount * (4 * luma * (1 - luma) * .75 + .25);
            r = Math.Clamp(r + delta, 0, 1); g = Math.Clamp(g + delta, 0, 1); b = Math.Clamp(b + delta, 0, 1);
        };
    }

    private static FloatPixelOp Noise(AddNoiseAdjustment settings)
    {
        var spread = (float)(Math.Clamp(settings.Amount, 0, AddNoiseAdjustment.MaxAmount) / 200);
        return (ref double r, ref double g, ref double b, int x, int y) =>
        {
            var nr = ImageFilters.Noise(x, y, settings.Seed, 0, settings.Gaussian) * spread;
            var ng = settings.Monochromatic ? nr : ImageFilters.Noise(x, y, settings.Seed, 1, settings.Gaussian) * spread;
            var nb = settings.Monochromatic ? nr : ImageFilters.Noise(x, y, settings.Seed, 2, settings.Gaussian) * spread;
            r = Math.Clamp(r + nr, 0, 1); g = Math.Clamp(g + ng, 0, 1); b = Math.Clamp(b + nb, 0, 1);
        };
    }
}
