using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>A pixel-coordinate preview with independent zoom/pan and a local refinement brush.</summary>
internal sealed class MaskRefineView(int pixelWidth, int pixelHeight) : Control
{
    private Bitmap? bitmap;
    private double zoom = 1;
    private Point origin, pointer, panStart, panOrigin;
    private SKPoint? last;
    private bool fit = true, painting, panning;
    private readonly List<(SKPoint From, SKPoint To)> liveStroke = [];
    public double BrushSize { get; set; } = 30;
    public bool ShowStrokeOverlay { get; set; } = true;
    public event Action<SKPoint, SKPoint, bool>? Stroke;
    public event Action? StrokeStarted, StrokeEnded;
    public double Zoom => zoom;
    public bool IsPainting => painting;

    public void SetBitmap(Bitmap value) { var old = bitmap; bitmap = value; if (!painting) liveStroke.Clear(); InvalidateVisual(); old?.Dispose(); }
    public void ReleaseBitmap() { bitmap?.Dispose(); bitmap = null; }
    public void Fit() { fit = true; InvalidateVisual(); }
    public void ActualSize() { fit = false; SetZoom(1, new(Bounds.Width / 2, Bounds.Height / 2)); }
    public Point ToView(SKPoint p) => new(origin.X + p.X * zoom, origin.Y + p.Y * zoom);
    private SKPoint ToPixel(Point p) => new((float)((p.X - origin.X) / zoom), (float)((p.Y - origin.Y) / zoom));

    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(new SolidColorBrush(Color.FromRgb(35, 35, 35)), Bounds.WithX(0).WithY(0));
        if (fit && Bounds.Width > 0 && Bounds.Height > 0)
        {
            zoom = Math.Min((Bounds.Width - 12) / pixelWidth, (Bounds.Height - 12) / pixelHeight);
            zoom = Math.Max(.001, zoom); origin = new((Bounds.Width - pixelWidth * zoom) / 2, (Bounds.Height - pixelHeight * zoom) / 2); fit = false;
        }
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            if (bitmap != null) context.DrawImage(bitmap, new Rect(origin, new Size(pixelWidth * zoom, pixelHeight * zoom)));
            var strokePen = new Pen(new SolidColorBrush(Color.FromArgb(100, 120, 180, 255)), Math.Max(1, BrushSize * zoom), lineCap: PenLineCap.Round);
            if (ShowStrokeOverlay) foreach (var segment in liveStroke) context.DrawLine(strokePen, ToView(segment.From), ToView(segment.To));
            if (IsPointerOver && !panning)
            {
                var radius = BrushSize * zoom / 2;
                context.DrawEllipse(null, new Pen(Brushes.Black, 3), pointer, radius, radius);
                context.DrawEllipse(null, new Pen(Brushes.White, 1), pointer, radius, radius);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus(); pointer = e.GetPosition(this); var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed) { panning = true; panStart = pointer; panOrigin = origin; e.Pointer.Capture(this); e.Handled = true; }
        else if (props.IsLeftButtonPressed)
        {
            var p = ToPixel(pointer); if (p.X < 0 || p.Y < 0 || p.X >= pixelWidth || p.Y >= pixelHeight) return;
            painting = true; last = p; liveStroke.Clear(); liveStroke.Add((p, p)); StrokeStarted?.Invoke(); Stroke?.Invoke(p, p, e.KeyModifiers.HasFlag(KeyModifiers.Alt)); e.Pointer.Capture(this); e.Handled = true;
        }
        InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); pointer = e.GetPosition(this);
        if (panning) origin = panOrigin + (pointer - panStart);
        if (painting)
        { var p = ToPixel(pointer); liveStroke.Add((last ?? p, p)); Stroke?.Invoke(last ?? p, p, e.KeyModifiers.HasFlag(KeyModifiers.Alt)); last = p; }
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); FinishStroke(); panning = false; e.Pointer.Capture(null); e.Handled = true; InvalidateVisual(); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); FinishStroke(); panning = false; }
    private void FinishStroke() { if (painting) { painting = false; last = null; StrokeEnded?.Invoke(); } }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); InvalidateVisual(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { base.OnPointerWheelChanged(e); fit = false; SetZoom(Math.Clamp(zoom * Math.Pow(1.2, e.Delta.Y), .01, 32), e.GetPosition(this)); e.Handled = true; }
    private void SetZoom(double value, Point pivot)
    { var p = ToPixel(pivot); zoom = value; origin = new(pivot.X - p.X * zoom, pivot.Y - p.Y * zoom); InvalidateVisual(); }
}
