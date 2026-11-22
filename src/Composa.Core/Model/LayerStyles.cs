using System.Text.Json.Serialization;

namespace Composa.Model;

public enum LayerStyleKind { Text, Appearance }

[Flags]
public enum StyleFields
{
    None = 0,
    Typeface = 1 << 0, Size = 1 << 1, Color = 1 << 2, Tracking = 1 << 3, Leading = 1 << 4, ParagraphSpacing = 1 << 5,
    Opacity = 1 << 6, Fill = 1 << 7, FillVisible = 1 << 8, Stroke = 1 << 9, StrokeVisible = 1 << 10, StrokeWidth = 1 << 11,
    EffectStroke = 1 << 12, DropShadow = 1 << 13, ColorOverlay = 1 << 14, InnerShadow = 1 << 15,
    OuterGlow = 1 << 16, InnerGlow = 1 << 17, GradientOverlay = 1 << 18,
    Text = Typeface | Size | Color | Tracking | Leading | ParagraphSpacing,
    Shape = Fill | FillVisible | Stroke | StrokeVisible | StrokeWidth,
    Effects = EffectStroke | DropShadow | ColorOverlay | InnerShadow | OuterGlow | InnerGlow | GradientOverlay,
    Appearance = Opacity | Shape | Effects
}

public sealed record LayerStyleBinding(Guid Id, StyleFields Overrides = StyleFields.None);

/// <summary>Reusable text properties in layer pixels. Wording, paragraph geometry and alignment belong to the layer.</summary>
public sealed record TextStyleDefinition(TextFace Face, double Size, uint Color, double Tracking, double Leading,
    bool PercentTracking, bool PercentLeading, double ParagraphSpacing)
{
    public static TextStyleDefinition From(TextStyle style) => new(style.Face, style.Size, style.Color, style.Tracking,
        style.Leading, style.Type.PercentTracking, style.Type.PercentLeading, style.Type.ParagraphSpacing);

    internal TextStyleDefinition Normalized() => From(Apply(new TextStyle(), StyleFields.Text).Clamped());

    internal StyleFields Differences(TextStyle style)
    {
        var result = StyleFields.None;
        if (Face != style.Face || style.FontRuns != null) result |= StyleFields.Typeface;
        if (Size != style.Size || style.SizeRuns != null) result |= StyleFields.Size;
        if (Color != style.Color || style.ColorRuns != null) result |= StyleFields.Color;
        result |= SpacingDifferences(style.Format);
        if (style.FormatRuns != null)
            foreach (var run in style.FormatRuns) result |= SpacingDifferences(run.Format);
        return result;
    }

    private StyleFields SpacingDifferences(TextFormat format)
    {
        var result = StyleFields.None;
        if (Tracking != format.Tracking || PercentTracking != format.Type.PercentTracking) result |= StyleFields.Tracking;
        if (Leading != format.Leading || PercentLeading != format.Type.PercentLeading) result |= StyleFields.Leading;
        if (ParagraphSpacing != format.Type.ParagraphSpacing) result |= StyleFields.ParagraphSpacing;
        return result;
    }

    internal TextStyle Apply(TextStyle style, StyleFields fields)
    {
        if (fields.HasFlag(StyleFields.Typeface))
            style = style with { FontFamily = TextStyle.IsFamilyName(Face.FontFamily) ? Face.FontFamily : "Inter",
                Bold = Face.Bold, Italic = Face.Italic, FontStyle = Face.FontStyle, FontRuns = null };
        if (fields.HasFlag(StyleFields.Size)) style = style with { Size = Size, SizeRuns = null };
        if (fields.HasFlag(StyleFields.Color)) style = style with { Color = Color, ColorRuns = null };
        if ((fields & (StyleFields.Tracking | StyleFields.Leading | StyleFields.ParagraphSpacing)) == 0) return style;
        return style.FormatRange(0, 0, format => format with
        {
            Tracking = fields.HasFlag(StyleFields.Tracking) ? Tracking : format.Tracking,
            Leading = fields.HasFlag(StyleFields.Leading) ? Leading : format.Leading,
            Type = format.Type with
            {
                PercentTracking = fields.HasFlag(StyleFields.Tracking) ? PercentTracking : format.Type.PercentTracking,
                PercentLeading = fields.HasFlag(StyleFields.Leading) ? PercentLeading : format.Type.PercentLeading,
                ParagraphSpacing = fields.HasFlag(StyleFields.ParagraphSpacing) ? ParagraphSpacing : format.Type.ParagraphSpacing
            }
        });
    }
}

public sealed record ShapePaintDefinition(uint Fill, VectorGradient? FillGradient, bool FillEnabled,
    uint? Stroke, VectorGradient? StrokeGradient, bool StrokeEnabled, double StrokeWidth)
{
    public static ShapePaintDefinition From(ShapeStyle shape) => new(shape.Fill, shape.FillGradient, shape.FillEnabled,
        shape.Stroke, shape.StrokeGradient, shape.StrokeEnabled, shape.StrokeWidth);

    internal ShapePaintDefinition Normalized() => new(Fill, FillGradient?.Normalized(), FillEnabled,
        Stroke, StrokeGradient?.Normalized(), StrokeEnabled, double.IsFinite(StrokeWidth) ? Math.Clamp(StrokeWidth, 0, 500) : 2);

    internal StyleFields Differences(ShapePaintDefinition other)
    {
        var result = StyleFields.None;
        if (Fill != other.Fill || FillGradient != other.FillGradient) result |= StyleFields.Fill;
        if (FillEnabled != other.FillEnabled) result |= StyleFields.FillVisible;
        if (Stroke != other.Stroke || StrokeGradient != other.StrokeGradient) result |= StyleFields.Stroke;
        if (StrokeEnabled != other.StrokeEnabled) result |= StyleFields.StrokeVisible;
        if (StrokeWidth != other.StrokeWidth) result |= StyleFields.StrokeWidth;
        return result;
    }

    internal ShapeStyle Apply(ShapeStyle shape, StyleFields fields) => shape with
    {
        Fill = fields.HasFlag(StyleFields.Fill) ? Fill : shape.Fill,
        FillGradient = fields.HasFlag(StyleFields.Fill) ? FillGradient : shape.FillGradient,
        FillEnabled = fields.HasFlag(StyleFields.FillVisible) ? FillEnabled : shape.FillEnabled,
        Stroke = fields.HasFlag(StyleFields.Stroke) ? Stroke : shape.Stroke,
        StrokeGradient = fields.HasFlag(StyleFields.Stroke) ? StrokeGradient : shape.StrokeGradient,
        StrokeEnabled = fields.HasFlag(StyleFields.StrokeVisible) ? StrokeEnabled : shape.StrokeEnabled,
        StrokeWidth = fields.HasFlag(StyleFields.StrokeWidth) ? StrokeWidth : shape.StrokeWidth
    };
}

public sealed record AppearanceStyleDefinition(double Opacity, ShapePaintDefinition? Paint, LayerEffects? Effects)
{
    [JsonIgnore] public StyleFields Fields => StyleFields.Opacity | (Paint != null ? StyleFields.Shape : 0) | (Effects != null ? StyleFields.Effects : 0);
    public static AppearanceStyleDefinition From(Layer layer) => new(layer.Opacity,
        layer.Shape is { Kind: not ShapeKind.Line } shape ? ShapePaintDefinition.From(shape) : null,
        layer.Pixels != null ? layer.Effects ?? LayerEffects.Empty : null);
    internal AppearanceStyleDefinition Normalized() => new(double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1,
        Paint?.Normalized(), Effects?.Clamped());

    public static StyleFields EffectField(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => StyleFields.EffectStroke,
        LayerEffectKind.DropShadow => StyleFields.DropShadow,
        LayerEffectKind.ColorOverlay => StyleFields.ColorOverlay,
        LayerEffectKind.InnerShadow => StyleFields.InnerShadow,
        LayerEffectKind.OuterGlow => StyleFields.OuterGlow,
        LayerEffectKind.InnerGlow => StyleFields.InnerGlow,
        _ => StyleFields.GradientOverlay
    };

    internal StyleFields Differences(AppearanceStyleDefinition other)
    {
        var result = Opacity == other.Opacity ? StyleFields.None : StyleFields.Opacity;
        if (Paint != null && other.Paint != null) result |= Paint.Differences(other.Paint);
        if (Effects != null && other.Effects != null)
            foreach (var kind in Enum.GetValues<LayerEffectKind>())
                if (Effects.WithFrom(kind, other.Effects) != Effects) result |= EffectField(kind);
        return result;
    }

    internal LayerEffects ApplyEffects(LayerEffects effects, StyleFields fields)
    {
        if (Effects != null)
            foreach (var kind in Enum.GetValues<LayerEffectKind>())
                if (fields.HasFlag(EffectField(kind))) effects = effects.WithFrom(kind, Effects);
        return effects;
    }
}

public sealed record LayerStyleDefinition(Guid Id, string Name, TextStyleDefinition? Text = null,
    AppearanceStyleDefinition? Appearance = null)
{
    public const int MaxNameLength = 80;
    [JsonIgnore] public LayerStyleKind Kind => Text != null ? LayerStyleKind.Text : LayerStyleKind.Appearance;
    [JsonIgnore] public StyleFields Fields => Text != null ? StyleFields.Text : Appearance?.Fields ?? StyleFields.None;
    public static string? NormalizeName(string? value)
    {
        var name = value?.Trim();
        return name is { Length: > 0 and <= MaxNameLength } && !name.Any(char.IsControl) ? name : null;
    }
    internal LayerStyleDefinition? Normalized() =>
        Id != Guid.Empty && NormalizeName(Name) is { } name && (Text != null) != (Appearance != null)
            ? this with { Name = name, Text = Text?.Normalized(), Appearance = Appearance?.Normalized() }
            : null;
}

public sealed partial class Layer
{
    public LayerStyleBinding? SharedTextStyle { get; set; }
    public LayerStyleBinding? SharedAppearanceStyle { get; set; }

    public LayerStyleBinding? StyleBinding(LayerStyleKind kind) => kind == LayerStyleKind.Text ? SharedTextStyle : SharedAppearanceStyle;
    internal void SetStyleBinding(LayerStyleKind kind, LayerStyleBinding? binding)
    {
        if (kind == LayerStyleKind.Text) SharedTextStyle = binding;
        else SharedAppearanceStyle = binding;
    }
}
