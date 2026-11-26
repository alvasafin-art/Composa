using Avalonia;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Geometry = Composa.Model.Geometry;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private bool TryScrollViewport(SKRectI viewport, RenderView mapping, Point? previousOffset)
    {
        if (session == null || viewCache == null || viewStale || !viewDirty.IsEmpty || pendingVisibility != null
            || viewKey.Scale != mapping.Scale || !session.CanRetainVisibilityBands(mapping)
            || mapping.Scale < 1 && navigationPreview?.IsEnabled != true
            || (previousOffset == null) != (reducedCacheOffset == null)) return false;
        var (dx, dy) = previousOffset is { } old && reducedCacheOffset is { } current
            ? (current.X - old.X, current.Y - old.Y)
            : (mapping.Origin.X - viewKey.Origin.X, mapping.Origin.Y - viewKey.Origin.Y);
        if (dx != Math.Round(dx) || dy != Math.Round(dy)) return false;
        var shiftX = (int)dx;
        var shiftY = (int)dy;
        var kept = Geometry.Intersect(viewport, SKRectI.Create(-shiftX, -shiftY, viewKey.Width, viewKey.Height));
        if (kept.IsEmpty || (long)kept.Width * kept.Height * 2 < (long)viewport.Width * viewport.Height) return false;
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        // The compositor can still be drawing the old viewport, so publish a new
        // bitmap after copying the common pixels and filling the newly exposed strips.
        var next = Pixels.NewWorkingColor(Math.Max(viewCache.Width, viewport.Width), Math.Max(viewCache.Height, viewport.Height),
            ColorDepth.Eight, displayProfile);
        var previous = viewCache.GetPixelSpan();
        var output = next.GetPixelSpan();
        var bytes = kept.Width * 4;
        for (var y = kept.Top; y < kept.Bottom; y++)
            previous.Slice((y + shiftY) * viewCache.RowBytes + (kept.Left + shiftX) * 4, bytes)
                .CopyTo(output.Slice(y * next.RowBytes + kept.Left * 4, bytes));
        RenderStrip(new SKRectI(0, 0, viewport.Right, kept.Top));
        RenderStrip(new SKRectI(0, kept.Bottom, viewport.Right, viewport.Bottom));
        RenderStrip(new SKRectI(0, kept.Top, kept.Left, kept.Bottom));
        RenderStrip(new SKRectI(kept.Right, kept.Top, viewport.Right, kept.Bottom));
        ReleaseViewportImage();
        viewCache = next;
        scrolledReducedViewport = mapping.Scale < 1;
        // Strip renders do not necessarily preserve the original full-frame band partition.
        viewHasFullBands = false;
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("viewport exposed strips", started);
#endif
        return true;

        void RenderStrip(SKRectI strip)
        {
            if (!strip.IsEmpty) session.RenderPanStrip(next, strip, mapping);
        }
    }
}
