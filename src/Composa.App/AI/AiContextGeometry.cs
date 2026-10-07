using Composa.AI;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>Source-pixel context shared by the overlay and the native masked workflow.</summary>
internal static class AiContextGeometry
{
    internal static SKRectI Flux(SKRectI selection, SKRectI canvas, int grow, int blend, double context, int blur = 0)
    {
        // Pixaroma's keep-shape geometry: grow, absolute context/seam room,
        // then proportional context on each axis. Shift a crop at canvas edges
        // instead of clipping away its context on one side.
        var margin = Math.Clamp(grow, 0, 64) + Math.Max(24, Math.Clamp(blend, 0, 64));
        var factor = Math.Clamp(context, 1, 8);
        var w = Math.Min(canvas.Width, (int)Math.Round(selection.Width * factor + 2 * margin));
        var h = Math.Min(canvas.Height, (int)Math.Round(selection.Height * factor + 2 * margin));
        var x = Math.Clamp((int)Math.Round((selection.Left + (double)selection.Right - w) / 2), canvas.Left, canvas.Right - w);
        var y = Math.Clamp((int)Math.Round((selection.Top + (double)selection.Bottom - h) / 2), canvas.Top, canvas.Bottom - h);
        return new(x, y, x + w, y + h);
    }

    /// <summary>The total crop including context sets the uniform scale and pixel budget.
    /// Only padding, never an aspect change, satisfies the VAE's 16-pixel grid.</summary>
    internal static ((int Width, int Height) Content, (int Width, int Height) Padded) FluxSize(
        SKRectI bounds, SKRectI target, int width, int height, bool original, int? minimumSide = null)
    {
        var scale = original ? 1 : minimumSide is > 0 ? (double)minimumSide.Value / Math.Max(bounds.Width, bounds.Height)
            : Math.Sqrt((double)width * height / Math.Max(1L, (long)bounds.Width * bounds.Height));
        if (!original) scale = Math.Max(scale, 64.0 / Math.Min(bounds.Width, bounds.Height));
        var w = checked((int)Math.Round(bounds.Width * scale)); var h = checked((int)Math.Round(bounds.Height * scale));
        var padded = (Width: Math.Max(64, checked((w + 15) / 16 * 16)), Height: Math.Max(64, checked((h + 15) / 16 * 16)));
        if (!Composa.Model.DocumentLimits.FitsSurface(padded.Width, padded.Height))
            throw new InvalidOperationException($"Generation including context exceeds the {Composa.Model.DocumentLimits.MaxSide} px / {Composa.Model.DocumentLimits.MaxSurfaceMegapixels} MP limit. Reduce image size.");
        return ((w,h), padded);
    }

    internal static SKRectI Padded(SKRectI selection, SKRectI canvas, int padding) => SKRectI.Intersect(canvas,
        new(selection.Left - padding, selection.Top - padding, selection.Right + padding, selection.Bottom + padding));
}
