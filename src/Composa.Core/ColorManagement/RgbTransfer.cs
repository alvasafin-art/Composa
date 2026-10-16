using Composa.Filters;
using SkiaSharp;

namespace Composa.ColorManagement;

/// <summary>Channel TRCs in the working RGB, including profiles with separate sampled curves.
/// Tables come from the same CMM as document conversion, with no gamut or byte intermediate.</summary>
internal sealed class RgbTransfer
{
    private readonly float[][]? decode, encode;
    private readonly float[]? curve;
    internal RgbTransfer(RgbProfile profile)
    {
        if (RgbProfile.SameSpace(profile.Space, RgbProfile.Srgb.Space)) return;
        if (profile.Space.GetNumericalTransferFunction(out var function) && function.G > 0 && function.A > 0)
        { curve = function.Values; return; }
        using var linear = profile.Space.ToLinearGamma();
        decode = Tables(profile.Space, linear); encode = Tables(linear, profile.Space);
    }

    private static unsafe float[][] Tables(SKColorSpace from, SKColorSpace to)
    {
        const int side = 256;
        using var source = new SKBitmap(new SKImageInfo(side, side, SKColorType.RgbaF32, SKAlphaType.Premul, from));
        using var target = new SKBitmap(source.Info.WithColorSpace(to));
        var input = (float*)source.GetPixels();
        for (var i = 0; i <= ushort.MaxValue; i++) { input[i * 4] = input[i * 4 + 1] = input[i * 4 + 2] = i / 65535f; input[i * 4 + 3] = 1; }
        using var pixmap = source.PeekPixels();
        if (!pixmap.ReadPixels(target.Info, target.GetPixels(), target.RowBytes)) throw new InvalidOperationException("The RGB profile's tone curves could not be evaluated.");
        var output = (float*)target.GetPixels(); var values = new[] { new float[65536], new float[65536], new float[65536] };
        for (var c = 0; c < 3; c++) for (var i = 0; i <= ushort.MaxValue; i++) values[c][i] = Math.Clamp(output[i * 4 + c], 0, 1);
        return values;
    }

    private static double Sample(float[] table, double value)
    {
        var at = Math.Clamp(value, 0, 1) * ushort.MaxValue; var i = (int)at;
        return i == ushort.MaxValue ? table[i] : table[i] + (table[i + 1] - table[i]) * (at - i);
    }
    internal double Decode(double value, int channel = 0)
    {
        if (curve is { } c) return value < c[4] ? c[3] * value + c[6] : Math.Pow(c[1] * value + c[2], c[0]) + c[5];
        return decode == null ? CameraRawPixels.SrgbToLinear(value) : Sample(decode[channel], value);
    }
    internal double Encode(double value, int channel = 0)
    {
        value = Math.Clamp(value, 0, 1);
        if (curve is { } c) return value < c[3] * c[4] + c[6] && c[3] != 0
            ? (value - c[6]) / c[3] : (Math.Pow(Math.Max(0, value - c[5]), 1d / c[0]) - c[2]) / c[1];
        return encode == null ? CameraRawPixels.LinearToSrgb(value) : Sample(encode[channel], value);
    }
}
