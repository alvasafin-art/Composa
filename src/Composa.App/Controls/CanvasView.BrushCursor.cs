using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    // Win32 owns the negotiated cursor transport. Keep the bitmap within the
    // large-pointer size supported by the Windows RDP protocol.
    private const int MaxNativeCursorSide = 384;
    private PointerType brushCursorPointer;
    private StandardCursorType canvasCursorType = StandardCursorType.Arrow;
    private Cursor? nativeBrushCursor;
    private BrushOutlineShape? nativeBrushShape;
    private bool nativeBrushCursorActive;
    private int nativeCursorSideLimit = MaxNativeCursorSide;

    private readonly record struct BrushOutlineShape(float Radius, float Roundness, float Angle, float Scaling)
    {
        internal bool Crosshair => Radius < 3 * Scaling;
        internal int HalfExtent => (int)Math.Ceiling(Math.Max(Radius + 2, Crosshair ? 8 * Scaling : 0));
    }

    private BrushOutlineShape BrushOutline(EditorSession painting, float pressure)
    {
        var brush = painting.Brush;
        var radius = (float)(brush.CursorRadius(pressure) * zoom);
        return new BrushOutlineShape(radius, (float)brush.TipRoundness, (float)brush.TipAngle, (float)Scaling);
    }

    private BrushOutlineShape? CurrentNativeBrushShape()
    {
        if (!OperatingSystem.IsWindows() || canvasCursorType != StandardCursorType.None
            || !cursorInside || brushCursorPointer != PointerType.Mouse || spaceDown || controlHover || temporaryMove
            || session is not { Tool: Tool.Brush or Tool.Eraser } painting
            || (drag != Drag.None && (drag != Drag.Stroke || painting.Brush.Smoothing > 0))
            || TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.HandleDescriptor != "HWND")
            return null;

        var shape = BrushOutline(painting, 1);
        if (!float.IsFinite(shape.Radius) || shape.Radius < 0 || shape.Radius > nativeCursorSideLimit / 2f
            || shape.HalfExtent * 2 + 1 > nativeCursorSideLimit)
            return null;

        // Preserve viewport clipping when the outline overlaps the edge of the control.
        var pad = shape.HalfExtent / Scaling;
        return new Rect(Bounds.Size).Deflate(pad).Contains(cursorScreen) ? shape : null;
    }

    internal void InvalidateBrushCursor() => ApplyCanvasCursor();

    private void ApplyCanvasCursor(bool updateStandard = false)
    {
        if (CurrentNativeBrushShape() is { } shape && TryApplyNativeBrushShape(shape))
        {
            nativeBrushCursorActive = true;
        }
        else
        {
            // Guides, text and transform handles can set their own cursor directly.
            if (updateStandard || (nativeBrushCursorActive && ReferenceEquals(Cursor, nativeBrushCursor)))
                Cursor = new Cursor(canvasCursorType);
            nativeBrushCursorActive = false;
        }
        if (Child is BrushCursorOverlay overlay) overlay.Refresh();
    }

    private bool TryApplyNativeBrushShape(BrushOutlineShape shape)
    {
        if (shape == nativeBrushShape)
        {
            if (!nativeBrushCursorActive) Cursor = nativeBrushCursor;
            return true;
        }
        Cursor next;
        try { next = CreateNativeBrushCursor(shape); }
        catch (Win32Exception)
        {
            nativeCursorSideLimit = shape.HalfExtent * 2;
            return false;
        }
        var previous = nativeBrushCursor;
        nativeBrushCursor = next;
        nativeBrushShape = shape;
        Cursor = next;
        previous?.Dispose();
        return true;
    }

    private void ReleaseNativeBrushCursor()
    {
        if (nativeBrushCursorActive && ReferenceEquals(Cursor, nativeBrushCursor))
            Cursor = new Cursor(canvasCursorType);
        nativeBrushCursorActive = false;
        nativeBrushCursor?.Dispose();
        nativeBrushCursor = null;
        nativeBrushShape = null;
        nativeCursorSideLimit = MaxNativeCursorSide;
        cursorInside = false;
    }

    private static Cursor CreateNativeBrushCursor(BrushOutlineShape shape)
    {
        var center = shape.HalfExtent;
        var side = 2 * center + 1;
        using var pixels = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(center + .5f, center + .5f);
            DrawBrushOutline(canvas, shape);
        }
        using var image = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pixels.GetPixels(),
            new PixelSize(side, side), new Vector(96, 96), pixels.RowBytes);
        return new Cursor(image, new PixelPoint(center, center));
    }

    private static void DrawBrushOutline(SKCanvas canvas, BrushOutlineShape shape)
    {
        if (shape.Crosshair)
        {
            DrawCrosshair(canvas, SKPoint.Empty, 1, 7 * shape.Scaling);
            return;
        }

        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.Save();
        if (shape.Roundness != 1) canvas.RotateDegrees(-shape.Angle);
        // A light edge on the outside keeps the dark ring visible on any background
        // without a stripe running between two dark lines on a white canvas.
        stroke.Color = SKColors.Black;
        stroke.StrokeWidth = 2;
        canvas.DrawOval(0, 0, shape.Radius, shape.Radius * shape.Roundness, stroke);
        stroke.Color = SKColors.White;
        stroke.StrokeWidth = 1.25f;
        canvas.DrawOval(0, 0, shape.Radius + .9f, shape.Radius * shape.Roundness + .9f, stroke);
        canvas.Restore();
    }

    private sealed partial class BrushCursorOverlay
    {
        private CompositionCustomVisual? outlineVisual;
        private OutlineContent? outlineContent;
        private bool drawsLegacy;

        public void Refresh()
        {
            var wasLegacy = drawsLegacy;
            var showOutline = false;
            drawsLegacy = !view.nativeBrushCursorActive && view.IsBrushTool && view.cursorInside;
            if (drawsLegacy && view.session is { Tool: Tool.Brush or Tool.Eraser } painting
                && !view.spaceDown && !view.controlHover && !view.temporaryMove
                && view.drag is Drag.None or Drag.Stroke
                && ElementComposition.GetElementVisual(this) is { } element)
            {
                var shape = view.BrushOutline(painting, painting.BrushPressure);
                if (float.IsFinite(shape.Radius) && shape.Radius >= 0)
                {
                    if (outlineVisual == null)
                    {
                        outlineVisual = element.Compositor.CreateCustomVisual(new OutlineRenderer());
                        ElementComposition.SetElementChildVisual(this, outlineVisual);
                    }
                    var center = painting.BrushPosition is { } strokeCenter ? view.ToScreen(strokeCenter) : view.cursorScreen;
                    var half = shape.HalfExtent / (double)shape.Scaling;
                    outlineVisual.Offset = new((float)(center.X - half), (float)(center.Y - half), 0);
                    outlineVisual.Size = new(2 * half, 2 * half);
                    showOutline = true;
                    var next = new OutlineContent(shape, view.brushCursorPointer == PointerType.Mouse);
                    if (outlineContent != next)
                    {
                        outlineContent = next;
                        outlineVisual.SendHandlerMessage(next);
                    }
                    drawsLegacy = false;
                }
            }
            if (drawsLegacy || wasLegacy) InvalidateVisual();
            if (outlineVisual != null) outlineVisual.Opacity = showOutline ? 1 : 0;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            drawsLegacy = false;
            if (outlineVisual != null)
            {
                outlineVisual.SendHandlerMessage(default(OutlineContent));
                ElementComposition.SetElementChildVisual(this, null);
                outlineVisual = null;
                outlineContent = null;
            }
            base.OnDetachedFromVisualTree(e);
        }
    }

    private readonly record struct OutlineContent(BrushOutlineShape Shape, bool Cache);

    private sealed class OutlineRenderer : CompositionCustomVisualHandler
    {
        private OutlineContent content;
        private SKImage? image;

        public override void OnMessage(object message)
        {
            if (message is not OutlineContent next || content == next) return;
            content = next;
            image?.Dispose();
            image = null;
            Invalidate();
        }

        public override void OnRender(Avalonia.Media.ImmediateDrawingContext context)
        {
            if (content.Shape.Scaling <= 0 || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var shape = content.Shape;
            var half = shape.HalfExtent;
            var side = 2 * half + 1;
            if (content.Cache && side <= 768 && image == null)
            {
                using var surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Translate(half, half);
                DrawBrushOutline(surface.Canvas, shape);
                image = surface.Snapshot();
            }

            canvas.Save();
            canvas.Scale(1 / shape.Scaling);
            if (image != null)
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
            else
            {
                canvas.Translate(half, half);
                DrawBrushOutline(canvas, shape);
            }
            canvas.Restore();
        }
    }
}
