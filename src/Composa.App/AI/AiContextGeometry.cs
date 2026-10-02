using Composa.AI;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>Source-pixel context shared by the overlay and the native masked workflow.</summary>
internal static class AiContextGeometry
{
    internal static SKRectI Flux(SKRectI selection, SKRectI canvas, int grow, int blend, double context)
    {
        var margin = Math.Max(Math.Clamp(grow, 0, 64) + 4 * Math.Clamp(blend, 0, 64),
            (int)Math.Ceiling(Math.Max(selection.Width, selection.Height) * (Math.Clamp(context, 1, 8) - 1) / 2));
        return Padded(selection, canvas, margin);
    }

    internal static SKRectI Padded(SKRectI selection, SKRectI canvas, int padding) => SKRectI.Intersect(canvas,
        new(selection.Left - padding, selection.Top - padding, selection.Right + padding, selection.Bottom + padding));
}
