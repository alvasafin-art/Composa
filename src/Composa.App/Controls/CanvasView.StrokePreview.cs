using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Geometry = Composa.Model.Geometry;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private const int StrokePreviewTileSide = 64;
    private readonly BrushCursorOverlay brushCursorOverlay;
    private readonly StrokePreviewSurface strokePreview = new() { IsHitTestVisible = false };
    private StrokePreviewFrame? strokePreviewFrame;
    private SKBitmap? strokePreviewScratch;
    private bool strokePreviewDisplayed;
    private SKRectI pendingStrokePreview;
    private bool strokePreviewScheduled;

    private sealed record StrokePreviewFrame(Size Size, SKRect Screen, SKRectI Source, SKRect Target, float Scaling,
        float PixelSize, bool Nearest, RenderView View);
    private sealed record StrokePreviewTile(SKBitmap Bitmap, SKRectI Pixels, SKRect Bounds);
    private sealed record StrokePreviewUpdate(StrokePreviewFrame Frame, Dictionary<SKPointI, StrokePreviewTile> Tiles, SKRect Bounds);
    private static readonly object ClearStrokePreview = new();

    private void ResetStrokePreview()
    {
        strokePreviewFrame = null;
        pendingStrokePreview = SKRectI.Empty;
        if (!strokePreviewDisplayed) return;
        strokePreviewDisplayed = false;
        strokePreview.Send(ClearStrokePreview);
    }

    private void PrepareStrokePreview(Size size, SKRect screen, SKRect source, SKRect target, float scaling, bool nearest)
    {
        if (source.IsEmpty || target.IsEmpty) return;
        var devicePixelSize = MathF.Round(target.Width * scaling / source.Width);
        if (!strokePreview.Ready || !displayProfile.Equals(ColorManagement.RgbProfile.Srgb)
            || source.Left != MathF.Round(source.Left) || source.Top != MathF.Round(source.Top)
            || MathF.Abs(target.Left * scaling - MathF.Round(target.Left * scaling)) > .001f
            || MathF.Abs(target.Top * scaling - MathF.Round(target.Top * scaling)) > .001f
            || devicePixelSize < 1 || !nearest && devicePixelSize != 1
            || MathF.Abs(target.Width * scaling - source.Width * devicePixelSize) > .001f
            || MathF.Abs(target.Height * scaling - source.Height * devicePixelSize) > .001f) return;

        // Use the same output grid and sampling phase as the full frame. Work stays
        // on the canvas cache grid even when the document is much larger than the view.
        var view = new RenderView(viewKey.Scale, viewKey.Origin)
        {
            OutputOffset = reducedCacheOffset is { } offset ? new SKPoint((float)-offset.X, (float)-offset.Y) : null
        };
        strokePreviewFrame = new(size, screen, Geometry.RoundOut(source), target, scaling, devicePixelSize / scaling, nearest, view);
    }

    private bool QueueStrokePreview(SKRectI dirty)
    {
        if (strokePreviewFrame is not { } frame || !strokePreview.Ready || session is not { IsStroking: true } || viewCache == null
            || drag is not (Drag.None or Drag.Stroke) || session.Selection != null
            || frame.Size != Bounds.Size || frame.Scaling != (float)Scaling) return false;
        dirty.Inflate(2, 2);
        dirty = Geometry.Intersect(dirty, frame.Source);
        if (dirty.IsEmpty) return true;
        var pending = Geometry.Union(pendingStrokePreview, dirty);
        // At native zoom the patch only composites its damaged columns. Even
        // a large brush can keep most of the existing presentation on screen.
        var pendingPixels = (long)pending.Width * pending.Height;
        var framePixels = (long)frame.Source.Width * frame.Source.Height;
        if (frame.Nearest ? pendingPixels * 4 > framePixels * 3 : pendingPixels * 3 > framePixels)
        {
            pendingStrokePreview = SKRectI.Empty;
            return false;
        }
        pendingStrokePreview = pending;
        if (!strokePreviewScheduled)
        {
            strokePreviewScheduled = true;
            Dispatcher.UIThread.Post(PresentStroke, DispatcherPriority.Render);
        }
        return true;
    }

    private void PresentStroke()
    {
        strokePreviewScheduled = false;
        var dirty = pendingStrokePreview;
        pendingStrokePreview = SKRectI.Empty;
        if (dirty.IsEmpty) return;
        if (strokePreviewFrame is not { } frame || !strokePreview.Ready || session is not { IsStroking: true }
            || drag is not (Drag.None or Drag.Stroke) || frame.Size != Bounds.Size || frame.Scaling != (float)Scaling)
        {
            InvalidateCanvas();
            return;
        }
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        const int side = StrokePreviewTileSide;
        var covered = Geometry.Intersect(new SKRectI(dirty.Left / side * side, dirty.Top / side * side,
            (dirty.Right + side - 1) / side * side, (dirty.Bottom + side - 1) / side * side), frame.Source);
        var scratch = strokePreviewScratch;
        if (scratch == null || scratch.Width < viewKey.Width || scratch.Height < viewKey.Height)
        {
            scratch?.Dispose();
            strokePreviewScratch = scratch = Pixels.NewColor(viewKey.Width, viewKey.Height);
        }
        session.RenderViewportFrame(scratch, covered, frame.View, bandExtent: new SKRectI(0, 0, viewKey.Width, viewKey.Height));
        var replacements = new Dictionary<SKPointI, StrokePreviewTile>();
        var input = scratch.GetPixelSpan();
        var stride = scratch.RowBytes;
        for (var y = covered.Top / side * side; y < covered.Bottom; y += side)
        for (var x = covered.Left / side * side; x < covered.Right; x += side)
        {
            var part = Geometry.Intersect(SKRectI.Create(x, y, side, side), covered);
            var tile = Pixels.NewColor(part.Width, part.Height);
            var bytes = part.Width * 4;
            var output = tile.GetPixelSpan();
            var tileStride = tile.RowBytes;
            for (var row = 0; row < part.Height; row++)
                input.Slice((part.Top + row) * stride + part.Left * 4, bytes)
                    .CopyTo(output.Slice(row * tileStride, bytes));
            var target = StrokePreviewBounds(frame, part);
            var local = part;
            local.Offset(-frame.Source.Left, -frame.Source.Top);
            replacements.Add(new(x, y), new(tile, local, target));
        }
        strokePreviewDisplayed = true;
        strokePreview.Send(new StrokePreviewUpdate(frame, replacements, StrokePreviewBounds(frame, covered)));
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("stroke viewport damage", started);
#endif
    }

    private static SKRect StrokePreviewBounds(StrokePreviewFrame frame, SKRectI pixels) =>
        SKRect.Create(frame.Target.Left + (pixels.Left - frame.Source.Left) * frame.PixelSize,
            frame.Target.Top + (pixels.Top - frame.Source.Top) * frame.PixelSize,
            pixels.Width * frame.PixelSize, pixels.Height * frame.PixelSize);

    private sealed class StrokePreviewSurface : Control
    {
        private CompositionCustomVisual? visual;
        public bool Ready => visual != null;
        public void Send(object message) => visual?.SendHandlerMessage(message);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (ElementComposition.GetElementVisual(this) is not { } element) return;
            visual = element.Compositor.CreateCustomVisual(new StrokePreviewRenderer());
            visual.Size = new(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, visual);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Send(ClearStrokePreview);
            ElementComposition.SetElementChildVisual(this, null);
            visual = null;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (visual != null && change.Property == BoundsProperty) visual.Size = new(Bounds.Width, Bounds.Height);
        }
    }

    private sealed class StrokePreviewRenderer : CompositionCustomVisualHandler
    {
        private readonly Dictionary<SKPointI, StrokePreviewTile> tiles = [];
        private StrokePreviewFrame? frame;

        public override void OnMessage(object message)
        {
            if (message is StrokePreviewUpdate update)
            {
                frame = update.Frame;
                foreach (var (key, tile) in update.Tiles)
                {
                    if (tiles.Remove(key, out var previous)) Release(previous);
                    tiles.Add(key, tile);
                }
                Invalidate(FromSkia(update.Bounds).Inflate(2 / frame.Scaling));
            }
            else if (ReferenceEquals(message, ClearStrokePreview))
            {
                foreach (var tile in tiles.Values) Release(tile);
                tiles.Clear();
                frame = null;
            }
        }

        private static Rect FromSkia(SKRect bounds) => new(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

        private static void Release(StrokePreviewTile tile)
        {
            Pixels.Invalidate(tile.Bitmap);
            tile.Bitmap.Dispose();
        }

        public override void OnRender(Avalonia.Media.ImmediateDrawingContext context)
        {
            if (frame == null || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.ClipRect(frame.Screen);
            var sampling = new SKSamplingOptions(frame.Nearest ? SKFilterMode.Nearest : SKFilterMode.Linear);
            using var shader = CheckerShader(frame.Screen);
            using var checker = new SKPaint { Shader = shader };
            var clip = canvas.LocalClipBounds;
            foreach (var tile in tiles.Values)
            {
                if (!clip.IntersectsWith(tile.Bounds)) continue;
                canvas.Save();
                canvas.ClipRect(tile.Bounds);
                canvas.DrawRect(tile.Bounds, checker);
                canvas.Translate(frame.Target.Left, frame.Target.Top);
                canvas.Scale(frame.PixelSize);
                canvas.DrawImage(Pixels.ImageOf(tile.Bitmap), tile.Pixels.Left, tile.Pixels.Top, sampling);
                canvas.Restore();
            }
            canvas.Restore();
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.FrameCompleted();
#endif
        }
    }
}
