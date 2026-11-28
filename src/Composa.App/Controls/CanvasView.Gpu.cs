using Avalonia;
using Avalonia.Threading;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private enum ViewportGraphics { Unknown, Software, Hardware }
    private int viewportGraphics;
    private bool acceleratedViewportVisible;
    private GRContext? rejectedViewportGraphics;
    private static readonly SKColorSpace ViewportSrgb = SKColorSpace.CreateSrgb();
    private DispatcherTimer? navigationPreview;
    private bool rescaleDuringNavigation, rescaledViewportVisible, scrolledReducedViewport;
    internal bool IsNavigating => drag is Drag.Pan or Drag.ZoomScrub || navigationPreview?.IsEnabled == true;

    internal bool? HardwareViewportAvailable => (ViewportGraphics)Volatile.Read(ref viewportGraphics) switch
    {
        ViewportGraphics.Hardware => true,
        ViewportGraphics.Software => false,
        _ => null
    };

    private bool DetectViewportGraphics(GRContext? graphics)
    {
        var available = graphics is { IsAbandoned: false } && !ReferenceEquals(graphics, rejectedViewportGraphics);
        Volatile.Write(ref viewportGraphics, (int)(available ? ViewportGraphics.Hardware : ViewportGraphics.Software));
        return available;
    }

    private void PreviewDuringNavigation(bool rescale = false)
    {
        rescaleDuringNavigation = rescale;
        if (!rescale && zoom >= 1)
        {
            navigationPreview?.Stop();
            return;
        }
        if (navigationPreview == null)
        {
            navigationPreview = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            navigationPreview.Tick += FinishNavigationPreview;
        }
        // Pans and zooms can arrive as a sequence. Return to the precise raster
        // viewport once the sequence stops, outside the rendering callback.
        navigationPreview.Stop();
        navigationPreview.Start();
    }

    private void FinishNavigationPreview(object? sender, EventArgs args)
    {
        navigationPreview!.Stop();
        rescaleDuringNavigation = false;
        if (scrolledReducedViewport)
        {
            scrolledReducedViewport = false;
            viewStale = true;
            InvalidateCanvas();
            return;
        }
        if (acceleratedViewportVisible || rescaledViewportVisible) InvalidateCanvas();
    }

    private bool TryRescaleViewport(SKMatrix view, SKRect document, Size size, out SKRect source, out SKRect target)
    {
        source = target = default;
        if (!rescaleDuringNavigation || navigationPreview?.IsEnabled != true || session == null
            || viewCache == null || viewStale || !viewDirty.IsEmpty || pendingVisibility != null
            || !session.CanRetainVisibilityBands(new RenderView(viewKey.Scale, viewKey.Origin))
            || zoom == viewKey.Scale || viewKey.Scale < 1 && zoom > viewKey.Scale * 2
            || !view.TryInvert(out var inverse)) return false;
        var visible = SKRect.Intersect(document, inverse.MapRect(SKRect.Create((float)size.Width, (float)size.Height)));
        if (visible.IsEmpty) return false;

        var oldScale = viewKey.Scale;
        var oldOffset = reducedCacheOffset is { } offset
            ? new SKPoint((float)-offset.X, (float)-offset.Y) : new SKPoint(-viewKey.Origin.X * oldScale, -viewKey.Origin.Y * oldScale);
        var oldView = SKMatrix.CreateScale(oldScale, oldScale).PostConcat(SKMatrix.CreateTranslation(oldOffset.X, oldOffset.Y));
        var oldPixels = oldView.MapRect(visible);
        if (!SKRect.Create(viewKey.Width, viewKey.Height).Contains(oldPixels)) return false;
        source = oldPixels;
        target = view.MapRect(visible);
        return true;
    }

    private AcceleratedViewport? CaptureAcceleratedViewport(Size size, Point displayOrigin)
    {
        var movingLayer = drag == Drag.Transform;
        if (HardwareViewportAvailable != true || zoom >= 1 || session == null || !displayProfile.Equals(RgbProfile.Srgb)
            || (!movingLayer && navigationPreview?.IsEnabled != true)) return null;

        var scaling = Scaling;
        var x = Math.Round(displayOrigin.X * scaling);
        var y = Math.Round(displayOrigin.Y * scaling);
        var left = (int)Math.Max(0, x);
        var top = (int)Math.Max(0, y);
        var right = (int)Math.Min(Math.Ceiling(size.Width * scaling), Math.Ceiling(x + session.Document.Width * zoom));
        var bottom = (int)Math.Min(Math.Ceiling(size.Height * scaling), Math.Ceiling(y + session.Document.Height * zoom));
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0 || (long)width * height is < 160_000 or > 8_000_000) return null;

        // An existing exact frame costs one image draw when a pan stays inside its padding.
        if (!viewStale && viewDirty.IsEmpty && pendingVisibility == null && viewKey.Scale == (float)zoom && reducedCacheOffset is { } cached
            && x + cached.X <= left && y + cached.Y <= top
            && x + cached.X + viewKey.Width >= right && y + cached.Y + viewKey.Height >= bottom) return null;

        var mapping = new RenderView((float)zoom, new SKPoint((float)((left - x) / zoom), (float)((top - y) / zoom)))
        {
            OutputOffset = new SKPoint((float)(x - left), (float)(y - top))
        };
#if CANVAS_PERFORMANCE
        var capture = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var scene = session.CaptureGpuViewport(new SKSizeI(width, height), mapping, movingLayer);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted(scene == null ? "Skia scene fallback" : "Skia scene capture", capture);
#endif
        if (scene == null) return null;

        var target = new SKRect((float)(left / scaling), (float)(top / scaling), (float)(right / scaling), (float)(bottom / scaling));
        return new AcceleratedViewport(scene, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, ViewportSrgb), target);
    }

    private sealed class AcceleratedViewport(DocumentRenderer.ViewportScene scene, SKImageInfo info, SKRect target)
    {
        public bool Draw(SKCanvas destination, GRContext? graphics, SKRect page)
        {
#if CANVAS_PERFORMANCE
            var allocating = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            using var texture = graphics is { IsAbandoned: false } ? SKSurface.Create(graphics, true, info) : null;
            // The rendering backend can change between recording and playing back an operation.
            using var raster = texture == null ? SKSurface.Create(info) : null;
            if ((texture ?? raster) is not { } surface) return false;
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.StageCompleted(texture == null ? "Skia raster viewport surface" : "Skia texture viewport surface", allocating);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            surface.Canvas.Clear(SKColors.Transparent);
            scene.Draw(surface.Canvas);
            using var image = surface.Snapshot();
            destination.Save();
            destination.ClipRect(page);
            destination.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear));
            destination.Restore();
#if CANVAS_PERFORMANCE
            if (texture != null) CanvasPerformanceTrace.StageCompleted("GPU texture viewport", started);
#endif
            return texture != null;
        }
    }
}
