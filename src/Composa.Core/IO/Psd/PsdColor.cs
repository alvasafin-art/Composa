using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>Converts Photoshop's tagged RGB into the sRGB working space, using the Skia already carried by the editor.</summary>
internal static class PsdColor
{
    public static void Apply(List<Layer> layers, byte[]? profile, List<PsdConversion> conversions)
    {
        if (profile == null || profile.AsSpan().SequenceEqual(PsdExport.SrgbProfileBytes)) return;
        using var source = SKColorSpace.CreateIcc(profile) ?? throw new PsdException("The embedded RGB color profile isn't supported. Convert the Photoshop file to sRGB before opening it.");
        if (source.IsSrgb) return;
        using var srgb = SKColorSpace.CreateSrgb();
        var colors = new Dictionary<uint, uint>();
        uint Color(uint value)
        {
            if (colors.TryGetValue(value, out var cached)) return cached;
            using var pixel = Pixels.NewColor(1, 1); pixel.Erase(new SKColor(value));
            using var converted = Convert(pixel, source, srgb);
            var result = (uint)converted.GetPixel(0, 0); colors[value] = result; return result;
        }
        foreach (var layer in Document.Flatten(layers))
        {
            // Embedded documents have their own profile and have already been converted on import.
            if (!layer.IsSmartObject && layer.Pixels is { } pixels)
            {
                var converted = Convert(pixels, source, srgb);
                layer.Pixels = converted; pixels.Dispose(); // These bitmaps belong to this uncommitted import only.
            }
            if (layer.Shape is { } shape) layer.Shape = shape with { Fill = Color(shape.Fill), Stroke = shape.Stroke is { } stroke ? Color(stroke) : null };
            if (layer.Text is { } text) layer.Text = text with
            {
                Color = Color(text.Color), ColorRuns = text.ColorRuns?.Select(r => r with { Color = Color(r.Color) }).ToArray()
            };
            if (layer.Effects is { } effects)
            {
                foreach (var kind in effects.Kinds) if (effects.ColorOf(kind) is { } color) effects = effects.WithColor(kind, Color(color));
                if (effects.GradientOverlay is { } gradient) effects = effects with { GradientOverlay = gradient with { EndColor = Color(gradient.EndColor) } };
                layer.Effects = effects;
            }
        }
        conversions.Add(new PsdConversion("Document", "The embedded RGB color profile was converted to the editor's sRGB working space. Adjustment results may differ from Photoshop."));
    }

    private static SKBitmap Convert(SKBitmap bitmap, SKColorSpace source, SKColorSpace destination)
    {
        var result = Pixels.NewColor(bitmap.Width, bitmap.Height);
        try
        {
            using var input = new SKPixmap(bitmap.Info.WithColorSpace(source), bitmap.GetPixels(), bitmap.RowBytes);
            if (!input.ReadPixels(result.Info.WithColorSpace(destination), result.GetPixels(), result.RowBytes))
                throw new PsdException("The Photoshop color profile could not be converted to sRGB.");
            Pixels.Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }
}
