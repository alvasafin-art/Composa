using Composa.AI;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>Source-pixel context shared by the overlay and the native masked workflow.</summary>
internal static class AiContextGeometry
{
    internal static SKRectI Flux(SKRectI selection, SKRectI canvas, int grow, int blend, double context, int blur = 0)
    {
        var margin = Math.Max(Math.Clamp(grow, 0, 64) + Math.Max(4 * Math.Clamp(blend, 0, 64),
                Math.Clamp(blend, 0, 64) + 3 * Math.Clamp(blur, 0, 64)),
            (int)Math.Ceiling(Math.Max(selection.Width, selection.Height) * (Math.Clamp(context, 1, 8) - 1) / 2));
        return Padded(selection, canvas, margin);
    }

    /// <summary>The selected region sets the uniform scale; context keeps that scale and adds pixels.
    /// Only padding, never an aspect change, satisfies the VAE's 16-pixel grid.</summary>
    internal static ((int Width, int Height) Content, (int Width, int Height) Padded) FluxSize(
        SKRectI bounds, SKRectI target, int width, int height, bool original, int? minimumSide = null)
    {
        var scale = original ? 1 : minimumSide is > 0 ? (double)minimumSide.Value / Math.Min(bounds.Width, bounds.Height)
            : Math.Sqrt((double)width * height / Math.Max(1L, (long)target.Width * target.Height));
        if (!original) scale = Math.Max(scale, 64.0 / Math.Min(bounds.Width, bounds.Height));
        var w = checked((int)Math.Round(bounds.Width * scale)); var h = checked((int)Math.Round(bounds.Height * scale));
        var padded = (Width: Math.Max(64, checked((w + 15) / 16 * 16)), Height: Math.Max(64, checked((h + 15) / 16 * 16)));
        if (!Composa.Model.DocumentLimits.FitsSurface(padded.Width, padded.Height))
            throw new InvalidOperationException($"Generation including context exceeds the {Composa.Model.DocumentLimits.MaxSide} px / {Composa.Model.DocumentLimits.MaxSurfaceMegapixels} MP limit. Reduce image size or mask context.");
        return ((w,h), padded);
    }

    internal static SKRectI Padded(SKRectI selection, SKRectI canvas, int padding) => SKRectI.Intersect(canvas,
        new(selection.Left - padding, selection.Top - padding, selection.Right + padding, selection.Bottom + padding));
}
