using Composa.Rendering;
using SkiaSharp;

namespace Composa.Model;

public sealed record VectorGradient(GradientRamp Ramp, bool Radial = false, double Angle = 0, double Scale = 1)
{
    public VectorGradient Normalized() => this with
    {
        Ramp = (Ramp ?? new()).Normalized(),
        Angle = double.IsFinite(Angle) ? Angle % 360 : 0,
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .01, 4) : 1
    };

    internal SKShader Shader(SKBitmap target)
    {
        var center = new SKPoint(target.Width / 2f, target.Height / 2f);
        var radians = Angle * Math.PI / 180;
        var direction = new SKPoint((float)Math.Cos(radians), -(float)Math.Sin(radians));
        var radius = (float)(Scale * (Radial ? Math.Min(center.X, center.Y)
            : Math.Abs(direction.X) * center.X + Math.Abs(direction.Y) * center.Y));
        var offset = new SKPoint(radius * direction.X, radius * direction.Y);
        var colors = new List<SKColorF>();
        var offsets = new List<float>();
        var positions = Ramp.Colors.Select(s => s.Position).Concat(Ramp.Opacities.Select(s => s.Position)).Append(0).Append(1).Distinct().Order();
        foreach (var position in positions)
        {
            if (position > 0)
            {
                var before = Ramp.Sample(Math.BitDecrement(position));
                if (before != Ramp.Sample(position)) { colors.Add((SKColorF)before); offsets.Add((float)position); }
            }
            colors.Add((SKColorF)Ramp.Sample(position)); offsets.Add((float)position);
        }
        return Radial
            ? SKShader.CreateRadialGradient(center, radius, colors.ToArray(), target.ColorSpace, offsets.ToArray(), SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(center - offset, center + offset, colors.ToArray(), target.ColorSpace, offsets.ToArray(), SKShaderTileMode.Clamp);
    }
}
