using Composa.ColorManagement;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    /// <summary>Changes how raster channel numbers are interpreted. Embedded documents keep their own profiles.</summary>
    public void AssignColorProfile(RgbProfile profile) => ChangeColorProfile(profile, assign: true, flatten: false);

    /// <summary>Converts RGB colors. Flatten preserves the composite; keeping layers evaluates blends and
    /// adjustments in the new space and can change their appearance, as in other layered RGB editors.</summary>
    public void ConvertColorProfile(RgbProfile profile, bool flatten = true) => ChangeColorProfile(profile, assign: false, flatten);

    private void ChangeColorProfile(RgbProfile profile, bool assign, bool flatten)
    {
        ArgumentNullException.ThrowIfNull(profile);
        FinishInteraction();
        if (document.Profile.Equals(profile)) return;
        var next = document.Clone(); next.Profile = profile;
        var owned = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        var reassigned = new Dictionary<SKBitmap, SKBitmap>(ReferenceEqualityComparer.Instance);
        var converted = new Dictionary<SKBitmap, SKBitmap>(ReferenceEqualityComparer.Instance);
        try
        {
            SKBitmap? Transform(SKBitmap? image, bool reinterpret)
            {
                if (image == null) return null;
                var cache = reinterpret ? reassigned : converted;
                if (!cache.TryGetValue(image, out var result))
                {
                    result = reinterpret ? Pixels.AssignProfile(image, profile) : Pixels.ConvertWorking(image, Pixels.DepthOf(image), profile);
                    cache.Add(image, result); owned.Add(result);
                }
                return result;
            }
            if (flatten)
            {
                using var composite = DocumentRenderer.Flatten(document);
                var result = Pixels.ConvertWorking(composite, document.WorkingDepth, profile); owned.Add(result);
                var layer = Layer.Raster("Converted Image", result);
                next.Layers.Clear(); next.Layers.Add(layer); next.SetActive(layer.Id);
            }
            else
            {
                using var swatches = new RgbColorConverter(document.Profile, profile);
                foreach (var layer in next.AllLayers())
                {
                    // A smart object's embedded original remains color-managed independently of its parent.
                    layer.Pixels = Transform(layer.Pixels, assign && !layer.IsSmartObject);
                    layer.FilterSource = Transform(layer.FilterSource, assign && !layer.IsSmartObject);
                    if (!assign)
                    {
                        ConvertStyleColors(layer, swatches);
                        if (layer.Text is { } text)
                        { var rendered = RenderText(text, next.WorkingDepth, profile); owned.Add(rendered); layer.Pixels = rendered; }
                        else if (layer.Shape is { } shape && layer.Pixels is { } image)
                        { var rendered = RenderShape(shape, image.Width, image.Height, next.WorkingDepth, profile); owned.Add(rendered); layer.Pixels = rendered; }
                    }
                    if (assign && layer.IsSmartObject && layer.FilterSource != null)
                    {
                        RenderSmartFilters(layer, next);
                        owned.Add(layer.Pixels!);
                    }
                }
            }
            if (next.RasterPixels() > DocumentLimits.DocumentPixelBudget) throw new InvalidOperationException("The profile conversion exceeds the document raster memory budget.");
        }
        catch { foreach (var image in owned) image.Dispose(); throw; }
        // Any intermediate replaced filter preview is uncommitted and no longer needed.
        var retained = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        next.CollectBitmaps(retained, new(), includeSelection: true);
        foreach (var image in owned) if (!retained.Contains(image)) image.Dispose();
        Apply(assign ? "Assign Color Profile" : "Convert Color Profile", () => document = next);
        InvalidateAll(); LayersChanged?.Invoke();
    }

    private static void ConvertStyleColors(Layer layer, RgbColorConverter converter)
    {
        uint Color(uint value) => converter.Convert(value);
        GradientRamp? Ramp(GradientRamp? ramp) => ramp == null ? null : ramp with { Colors = ramp.Colors.Select(s => s with { Color = Color(s.Color) }).ToArray() };
        if (layer.Text is { } text) layer.Text = text with { Color = Color(text.Color), ColorRuns = text.ColorRuns?.Select(r => r with { Color = Color(r.Color) }).ToArray() };
        if (layer.Shape is { } shape) layer.Shape = shape with { Fill = Color(shape.Fill), Stroke = shape.Stroke is { } stroke ? Color(stroke) : null };
        if (layer.Adjustment is GradientMapAdjustment map) layer.Adjustment = map with { Shadows = Color(map.Shadows), Highlights = Color(map.Highlights) };
        layer.SmartFilters = layer.SmartFilters.Select(f => f with { Settings = f.Settings with { VignetteColor = Color(f.Settings.VignetteColor) } }).ToArray();
        if (layer.Effects is { } e) layer.Effects = e with
        {
            Stroke = e.Stroke is { } stroke ? stroke with { Color = Color(stroke.Color) } : null,
            Shadow = e.Shadow is { } shadow ? shadow with { Color = Color(shadow.Color) } : null,
            InnerShadow = e.InnerShadow is { } inner ? inner with { Color = Color(inner.Color) } : null,
            OuterGlow = e.OuterGlow is { } glow ? glow with { Color = Color(glow.Color) } : null,
            InnerGlow = e.InnerGlow is { } innerGlow ? innerGlow with { Color = Color(innerGlow.Color) } : null,
            ColorOverlay = e.ColorOverlay is { } overlay ? overlay with { Color = Color(overlay.Color) } : null,
            GradientOverlay = e.GradientOverlay is { } gradient ? gradient with { StartColor = Color(gradient.StartColor), EndColor = Color(gradient.EndColor), Ramp = Ramp(gradient.Ramp) } : null
        };
    }
}
