using SkiaSharp;

namespace Composa.Model;

public enum BlendMode
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, Difference, ColorDodge, ColorBurn,
    SoftLight, HardLight, Exclusion, Hue, Saturation, Color, Luminosity,
    LinearBurn, LinearDodge, VividLight, LinearLight, PinLight, HardMix, Subtract, Divide,
    PassThrough, DarkerColor, LighterColor, Dissolve
}

public static class BlendModeExtensions
{
    /// <summary>
    /// Photoshop's grouping: darkening modes together, then lightening, then contrast, then the comparative ones, then
    /// the component modes. Menus draw a line between each group. Pass Through is available only to groups.
    /// </summary>
    public static readonly BlendMode[][] Groups =
    [
        [BlendMode.PassThrough, BlendMode.Normal, BlendMode.Dissolve],
        [BlendMode.Darken, BlendMode.Multiply, BlendMode.ColorBurn, BlendMode.LinearBurn, BlendMode.DarkerColor],
        [BlendMode.Lighten, BlendMode.Screen, BlendMode.ColorDodge, BlendMode.LinearDodge, BlendMode.LighterColor],
        [BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.VividLight, BlendMode.LinearLight, BlendMode.PinLight, BlendMode.HardMix],
        [BlendMode.Difference, BlendMode.Exclusion, BlendMode.Subtract, BlendMode.Divide],
        [BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity]
    ];

    /// <summary>The modes Skia has no equivalent for; <see cref="Rendering.SeparableBlend"/> composites them per pixel instead.</summary>
    public static bool IsCustom(this BlendMode mode) => mode >= BlendMode.LinearBurn && mode != BlendMode.PassThrough;

    /// <summary>Skia's blend for a mode it can draw; custom modes never reach a paint and map to source-over here.</summary>
    public static SKBlendMode ToSkia(this BlendMode mode) => mode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver
    };

    public static string DisplayName(this BlendMode mode) => mode switch
    {
        BlendMode.ColorDodge => "Color Dodge",
        BlendMode.ColorBurn => "Color Burn",
        BlendMode.SoftLight => "Soft Light",
        BlendMode.HardLight => "Hard Light",
        BlendMode.LinearBurn => "Linear Burn",
        BlendMode.LinearDodge => "Linear Dodge (Add)",
        BlendMode.VividLight => "Vivid Light",
        BlendMode.LinearLight => "Linear Light",
        BlendMode.PinLight => "Pin Light",
        BlendMode.HardMix => "Hard Mix",
        BlendMode.PassThrough => "Pass Through",
        BlendMode.DarkerColor => "Darker Color",
        BlendMode.LighterColor => "Lighter Color",
        _ => mode.ToString()
    };
}
