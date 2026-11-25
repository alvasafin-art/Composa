using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    // Small Win32 cursor shapes can travel on the system pointer path, including over RDP.
    // Keep larger tips, pen input, smoothing trails, and clone markers on the canvas.
    private const int MaxNativeCursorSide = 64;
    private PointerType brushCursorPointer;
    private StandardCursorType canvasCursorType = StandardCursorType.Arrow;
    private Cursor? nativeBrushCursor;
    private NativeBrushShape? nativeBrushShape;
    private bool nativeBrushCursorActive;
    private bool nativeBrushCursorUnavailable;

    private readonly record struct NativeBrushShape(float Radius, float Edge, float Inner, float Roundness, float Angle, float Scaling)
    {
        internal bool Crosshair => Radius < 3 * Scaling;
        internal int HalfExtent => (int)Math.Ceiling(Math.Max(Edge + 3, Crosshair ? 8 * Scaling : 0));
    }

    private NativeBrushShape? CurrentNativeBrushShape()
    {
        if (!OperatingSystem.IsWindows() || nativeBrushCursorUnavailable || canvasCursorType != StandardCursorType.None
            || !cursorInside || brushCursorPointer != PointerType.Mouse || spaceDown || controlHover || temporaryMove
            || session is not { Tool: Tool.Brush or Tool.Eraser } painting
            || (drag != Drag.None && (drag != Drag.Stroke || painting.Brush.Smoothing > 0))
            || TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.HandleDescriptor != "HWND")
            return null;

        var brush = painting.Brush;
        var radius = (float)(brush.CursorRadius() * zoom);
        var shape = new NativeBrushShape(radius, (float)(brush.Size * .5 * zoom), (float)(radius * brush.Hardness),
            (float)brush.TipRoundness, (float)brush.TipAngle, (float)Scaling);
        if (!float.IsFinite(shape.Edge) || shape.Edge < 0 || shape.Edge > MaxNativeCursorSide / 2f
            || shape.HalfExtent * 2 + 1 > MaxNativeCursorSide)
            return null;

        // Preserve viewport clipping when the outline overlaps the edge of the control.
        var pad = shape.HalfExtent / Scaling;
        return new Rect(Bounds.Size).Deflate(pad).Contains(cursorScreen) ? shape : null;
    }

    private void InvalidateBrushCursor()
    {
        ApplyCanvasCursor();
        if (!nativeBrushCursorActive) Child?.InvalidateVisual();
    }

    private void ApplyCanvasCursor(bool updateStandard = false)
    {
        if (CurrentNativeBrushShape() is { } shape && TryApplyNativeBrushShape(shape))
        {
            if (!nativeBrushCursorActive)
            {
                nativeBrushCursorActive = true;
                Child?.InvalidateVisual();
            }
            return;
        }

        // Guides, text and transform handles can set their own cursor directly.
        if (updateStandard || (nativeBrushCursorActive && ReferenceEquals(Cursor, nativeBrushCursor)))
            Cursor = new Cursor(canvasCursorType);
        if (nativeBrushCursorActive)
        {
            nativeBrushCursorActive = false;
            Child?.InvalidateVisual();
        }
    }

    private bool TryApplyNativeBrushShape(NativeBrushShape shape)
    {
        if (shape == nativeBrushShape)
        {
            if (!nativeBrushCursorActive) Cursor = nativeBrushCursor;
            return true;
        }
        Cursor next;
        try { next = CreateNativeBrushCursor(shape); }
        catch (Win32Exception) { nativeBrushCursorUnavailable = true; return false; }
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
        nativeBrushCursorUnavailable = false;
        cursorInside = false;
    }

    private static Cursor CreateNativeBrushCursor(NativeBrushShape shape)
    {
        var center = shape.HalfExtent;
        var side = 2 * center + 1;
        using var pixels = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(pixels))
        using (var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true })
        using (var dashes = SKPathEffect.CreateDash([2, 3], 0))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(center + .5f, center + .5f);
            canvas.Save();
            if (shape.Roundness != 1) canvas.RotateDegrees(-shape.Angle);
            if (shape.Edge - shape.Radius > 1)
            {
                stroke.PathEffect = dashes;
                Ring(shape.Edge, 1, SKColors.Black);
                Ring(shape.Edge, 0, SKColors.White);
                stroke.PathEffect = null;
            }
            if (!shape.Crosshair)
            {
                Ring(shape.Radius, 1, SKColors.Black);
                Ring(shape.Radius, 0, SKColors.White);
                if (shape.Inner > 2 * shape.Scaling && shape.Inner < shape.Radius - 2 * shape.Scaling)
                    Ring(shape.Inner, 0, SKColors.White.WithAlpha(92));
            }
            canvas.Restore();
            if (shape.Crosshair) DrawCrosshair(canvas, SKPoint.Empty, 1, 7 * shape.Scaling);

            void Ring(float radius, float extra, SKColor color)
            {
                stroke.Color = color;
                canvas.DrawOval(0, 0, radius + extra, radius * shape.Roundness + extra, stroke);
            }
        }
        using var image = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pixels.GetPixels(),
            new PixelSize(side, side), new Vector(96, 96), pixels.RowBytes);
        return new Cursor(image, new PixelPoint(center, center));
    }
}
