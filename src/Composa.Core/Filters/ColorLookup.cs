using Composa.ColorManagement;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

public enum LookupRgbSpace { Srgb, WorkingRgb, DisplayP3, Rgb1998 }

/// <summary>Embedded LDR RGB lookup, evaluated on straight float RGB in its explicitly selected color space.
/// Strength blends with the original in the document's encoded RGB; alpha never passes through the table.</summary>
public sealed record ColorLookupAdjustment : Adjustment
{
    public CubeLut? Table { get; init; }
    public double Strength { get; init; } = 100;
    public LookupRgbSpace Space { get; init; }
    public override AdjustmentKind Kind => AdjustmentKind.ColorLookup;
    public override string DisplayName => "Color Lookup";
    public override bool IsIdentity => Table == null || !double.IsFinite(Strength) || Strength <= 0;
    internal FloatPixelOp CreateColorOp(RgbProfile working)
    {
        var table = Table; var strength = double.IsFinite(Strength) ? Math.Clamp(Strength, 0, 100) / 100 : 0;
        if (table == null || strength == 0) return (ref double r, ref double g, ref double b, int _, int _) => { };
        var profile = Space switch { LookupRgbSpace.Srgb => RgbProfile.Srgb, LookupRgbSpace.DisplayP3 => RgbProfile.DisplayP3, LookupRgbSpace.Rgb1998 => RgbProfile.Rgb1998, _ => working };
        if (RgbProfile.SameSpace(working.Space, profile.Space)) return (ref double r, ref double g, ref double b, int _, int _) =>
        { var color = table.Map(r, g, b); r += (Math.Clamp(color.R, 0, 1) - r) * strength; g += (Math.Clamp(color.G, 0, 1) - g) * strength; b += (Math.Clamp(color.B, 0, 1) - b) * strength; };
        var forward = new LinearRgbTransform(working, profile); var backward = new LinearRgbTransform(profile, working); var input = working.Transfer; var lut = profile.Transfer;
        return (ref double r, ref double g, ref double b, int _, int _) =>
        {
            var linear = forward.Map(input.Decode(r, 0), input.Decode(g, 1), input.Decode(b, 2));
            var value = table.Map(lut.Encode(linear.R, 0), lut.Encode(linear.G, 1), lut.Encode(linear.B, 2));
            var original = backward.Map(lut.Decode(Math.Clamp(value.R, 0, 1), 0), lut.Decode(Math.Clamp(value.G, 0, 1), 1), lut.Decode(Math.Clamp(value.B, 0, 1), 2));
            r += (input.Encode(original.R, 0) - r) * strength; g += (input.Encode(original.G, 1) - g) * strength; b += (input.Encode(original.B, 2) - b) * strength;
        };
    }
    internal override PixelOp CreateOp() => throw new InvalidOperationException("Color Lookup requires its bitmap's working RGB profile.");
}
