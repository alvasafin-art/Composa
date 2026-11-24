using SkiaSharp;

namespace Composa.Model;

public sealed record GradientColorStop(double Position, uint Color);
public sealed record GradientOpacityStop(double Position, double Opacity);
public sealed record GradientRamp
{
    public GradientColorStop[] Colors { get; init; } = [new(0, 0xFF000000), new(1, 0xFFFFFFFF)];
    public GradientOpacityStop[] Opacities { get; init; } = [new(0, 1), new(1, 1)];
    public GradientRamp Normalized() => this with
    {
        Colors = Colors.Length == 0 ? new GradientRamp().Colors : Colors.Take(64).Select(c => c with { Position = double.IsFinite(c.Position) ? Math.Clamp(c.Position, 0, 1) : 0, Color = c.Color | 0xFF000000 }).OrderBy(c => c.Position).ToArray(),
        Opacities = Opacities.Length == 0 ? new GradientRamp().Opacities : Opacities.Take(64).Select(o => new GradientOpacityStop(double.IsFinite(o.Position) ? Math.Clamp(o.Position, 0, 1) : 0, double.IsFinite(o.Opacity) ? Math.Clamp(o.Opacity, 0, 1) : 1)).OrderBy(o => o.Position).ToArray()
    };
    public static GradientRamp Between(uint start, uint end) => new()
    {
        Colors = [new(0, start | 0xFF000000), new(1, end | 0xFF000000)],
        Opacities = [new(0, (start >> 24) / 255.0), new(1, (end >> 24) / 255.0)]
    };
    public bool Equals(GradientRamp? other) => other != null && Colors.SequenceEqual(other.Colors) && Opacities.SequenceEqual(other.Opacities);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var c in Colors) hash.Add(c); foreach (var o in Opacities) hash.Add(o); return hash.ToHashCode(); }

    public SKColor Sample(double position)
    {
        var left = Colors.LastOrDefault(c => c.Position <= position) ?? Colors[0]; var right = Colors.FirstOrDefault(c => c.Position > position) ?? Colors[^1];
        var t = right.Position > left.Position ? Math.Clamp((position - left.Position) / (right.Position - left.Position), 0, 1) : 0;
        var a = new SKColor(left.Color); var b = new SKColor(right.Color);
        byte Mix(byte x, byte y) => (byte)Math.Clamp(Math.Round(x + (y - x) * t), 0, 255);
        return new(Mix(a.Red, b.Red), Mix(a.Green, b.Green), Mix(a.Blue, b.Blue), (byte)Math.Round(OpacityAt(position) * 255));
    }

    internal double OpacityAt(double position)
    {
        var left = Opacities.LastOrDefault(o => o.Position <= position) ?? Opacities[0];
        var right = Opacities.FirstOrDefault(o => o.Position > position) ?? Opacities[^1];
        var t = right.Position > left.Position ? Math.Clamp((position - left.Position) / (right.Position - left.Position), 0, 1) : 0;
        return Math.Clamp(left.Opacity + (right.Opacity - left.Opacity) * t, 0, 1);
    }
    public SKColor[] ShaderColors(bool reverse = false)
    {
        var normalized = Normalized(); return Enumerable.Range(0, 257).Select(i => normalized.Sample(reverse ? 1 - i / 256.0 : i / 256.0)).ToArray();
    }
    public static IReadOnlyDictionary<string, GradientRamp> Presets { get; } = new Dictionary<string, GradientRamp>
    {
        ["Black to white"] = new(),
        ["Transparent fade"] = Between(0xFFFFFFFF, 0x00FFFFFF),
        ["Sunset"] = new() { Colors = [new(0, 0xFF35247B), new(.45, 0xFFDD416B), new(1, 0xFFFFCB68)] },
        ["Spectrum"] = new() { Colors = [new(0, 0xFFFF0000), new(.2, 0xFFFFFF00), new(.4, 0xFF00FF00), new(.6, 0xFF00FFFF), new(.8, 0xFF0000FF), new(1, 0xFFFF00FF)] }
    };
}
