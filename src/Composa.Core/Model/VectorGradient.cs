using System.Numerics;
using Composa.ColorManagement;
using Composa.Filters;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Model;

public sealed record VectorGradient(GradientRamp Ramp, bool Radial = false, double Angle = 0, double Scale = 1)
{
    public bool Angular { get; init; }
    public bool Perceptual { get; init; }
    public double CenterOffsetX { get; init; }
    public double CenterOffsetY { get; init; }

    public VectorGradient Normalized() => this with
    {
        Ramp = (Ramp ?? new()).Normalized(),
        Angle = double.IsFinite(Angle) ? Angle % 360 : 0,
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .01, 4) : 1,
        CenterOffsetX = double.IsFinite(CenterOffsetX) ? Math.Clamp(CenterOffsetX, -1, 1) : 0,
        CenterOffsetY = double.IsFinite(CenterOffsetY) ? Math.Clamp(CenterOffsetY, -1, 1) : 0
    };

    internal SKShader Shader(SKBitmap target, SKRect? geometry = null)
    {
        var center = new SKPoint(target.Width / 2f, target.Height / 2f);
        var radians = Angle * Math.PI / 180;
        var direction = new SKPoint((float)Math.Cos(radians), -(float)Math.Sin(radians));
        var radius = (float)(Scale * (Radial ? Math.Min(center.X, center.Y)
            : Math.Abs(direction.X) * center.X + Math.Abs(direction.Y) * center.Y));
        var offset = new SKPoint(radius * direction.X, radius * direction.Y);
        var colors = new List<SKColorF>();
        var offsets = new List<float>();
        var positions = Ramp.Colors.Select(s => s.Position).Concat(Ramp.Opacities.Select(s => s.Position)).Append(0).Append(1);
        var profile = Perceptual ? Pixels.ProfileOf(target) : RgbProfile.Srgb;
        using var converter = Perceptual && !profile.Equals(RgbProfile.Srgb) ? new RgbColorConverter(profile, RgbProfile.Srgb) : null;
        var perceptual = Perceptual ? Ramp.Colors.Select(stop => ToOklab(new SKColor(converter?.Convert(stop.Color) ?? stop.Color))).ToArray() : null;
        if (perceptual != null) positions = positions.Concat(Enumerable.Range(1, 255).Select(i => i / 256d));
        SKColorF Sample(double position)
        {
            if (perceptual == null) return (SKColorF)Ramp.Sample(position);
            var left = Math.Max(0, Array.FindLastIndex(Ramp.Colors, stop => stop.Position <= position));
            var right = Math.Min(left + 1, Ramp.Colors.Length - 1);
            var first = Ramp.Colors[left].Position;
            var last = Ramp.Colors[right].Position;
            var amount = last > first ? (float)Math.Clamp((position - first) / (last - first), 0, 1) : 0;
            return FromOklab(Vector3.Lerp(perceptual[left], perceptual[right], amount), (float)Ramp.OpacityAt(position));
        }
        // Extra samples approximate perceptual interpolation while the original stops stay editable.
        foreach (var position in positions.Distinct().Order())
        {
            var sample = Sample(position);
            if (position > 0)
            {
                var before = Sample(Math.BitDecrement(position));
                if (before != sample) { colors.Add(before); offsets.Add((float)position); }
            }
            colors.Add(sample); offsets.Add((float)position);
        }
        var space = Perceptual ? RgbProfile.Srgb.Space : target.ColorSpace;
        if (Angular)
        {
            var box = geometry ?? SKRect.Create(target.Width, target.Height);
            center = new SKPoint(box.MidX + (float)(CenterOffsetX * box.Width), box.MidY + (float)(CenterOffsetY * box.Height));
            var rotate = SKMatrix.CreateRotationDegrees(-(float)Angle, center.X, center.Y);
            return SKShader.CreateSweepGradient(center, colors.ToArray(), space, offsets.ToArray(), rotate);
        }
        return Radial
            ? SKShader.CreateRadialGradient(center, radius, colors.ToArray(), space, offsets.ToArray(), SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(center - offset, center + offset, colors.ToArray(), space, offsets.ToArray(), SKShaderTileMode.Clamp);
    }

    // Oklab matrices for linear sRGB: https://bottosson.github.io/posts/oklab/
    private static Vector3 ToOklab(SKColor color)
    {
        double r = CameraRawPixels.SrgbToLinear(color.Red / 255d), g = CameraRawPixels.SrgbToLinear(color.Green / 255d), b = CameraRawPixels.SrgbToLinear(color.Blue / 255d);
        double l = Math.Cbrt(.4122214708 * r + .5363325363 * g + .0514459929 * b);
        double m = Math.Cbrt(.2119034982 * r + .6806995451 * g + .1073969566 * b);
        double s = Math.Cbrt(.0883024619 * r + .2817188376 * g + .6299787005 * b);
        return new((float)(.2104542553 * l + .7936177850 * m - .0040720468 * s),
            (float)(1.9779984951 * l - 2.4285922050 * m + .4505937099 * s),
            (float)(.0259040371 * l + .7827717662 * m - .8086757660 * s));
    }

    private static SKColorF FromOklab(Vector3 lab, float alpha)
    {
        double l = lab.X + .3963377774 * lab.Y + .2158037573 * lab.Z;
        double m = lab.X - .1055613458 * lab.Y - .0638541728 * lab.Z;
        double s = lab.X - .0894841775 * lab.Y - 1.2914855480 * lab.Z;
        l = l * l * l; m = m * m * m; s = s * s * s;
        static float Encode(double value) => (float)CameraRawPixels.LinearToSrgb(Math.Clamp(value, 0, 1));
        return new(Encode(4.0767416621 * l - 3.3077115913 * m + .2309699292 * s),
            Encode(-1.2684380046 * l + 2.6097574011 * m - .3413193965 * s),
            Encode(-.0041960863 * l - .7034186147 * m + 1.7076147010 * s), alpha);
    }
}
