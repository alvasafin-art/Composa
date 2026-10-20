using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>A reduced, color-managed document view. Panning redraws only the frame; pixel edits coalesce
/// thumbnail refreshes, and a hidden/collapsed panel does no rendering. No document bitmap is borrowed by its image.</summary>
public sealed class NavigatorPanel : UserControl
{
    private readonly CanvasView canvas;
    private readonly NavigatorPreview preview;
    private readonly SliderField zoom;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private EditorSession? session;
    private bool attached, dirty = true;
    private RgbProfile displayProfile = RgbProfile.Srgb;
    internal int ThumbnailBuilds { get; private set; }

    public NavigatorPanel(CanvasView canvas)
    {
        this.canvas = canvas; preview = new(this) { Name = "NavigatorPreview", MinHeight = 36, ClipToBounds = true };
        zoom = new SliderField("Zoom", 100, 1, 6400, .1, "0.0") { Name = "NavigatorZoom", Width = 120, Logarithmic = true };
        zoom.Changed += value => { if (!canvas.IsDragging) canvas.ZoomTo(value / 100); };
        var fit = Ui.TextButton("Fit", () => { if (!canvas.IsDragging) canvas.Fit(); }); fit.Name = "NavigatorFit";
        var actual = Ui.TextButton("100%", () => { if (!canvas.IsDragging) canvas.ZoomTo(1); }); actual.Name = "NavigatorActual";
        var buttons = Ui.Row(6, zoom, fit, actual); buttons.Margin = new Thickness(6, 4); buttons.HorizontalAlignment = HorizontalAlignment.Center;
        var body = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons); body.Children.Add(preview); Content = body;
        timer.Tick += (_, _) => { timer.Stop(); RefreshThumbnail(); };
        AttachedToVisualTree += (_, _) => { attached = true; Subscribe(); canvas.ViewChanged += ViewChanged; ViewChanged(); QueueRefresh(); };
        DetachedFromVisualTree += (_, _) => { attached = false; Unsubscribe(); canvas.ViewChanged -= ViewChanged; timer.Stop(); preview.Clear(); dirty = true; };
    }

    public EditorSession? Session
    {
        get => session;
        set
        {
            if (session == value) return;
            Unsubscribe(); session = value; if (attached) Subscribe(); preview.Clear(); dirty = true; ViewChanged(); QueueRefresh();
        }
    }
    public RgbProfile DisplayProfile { get => displayProfile; set { if (displayProfile.Equals(value)) return; displayProfile = value; dirty = true; QueueRefresh(); } }
    private void Subscribe()
    { if (session != null) { session.CanvasChanged += CanvasChanged; session.LayersChanged += LayersChanged; session.ChannelsChanged += LayersChanged; } }
    private void Unsubscribe()
    { if (session != null) { session.CanvasChanged -= CanvasChanged; session.LayersChanged -= LayersChanged; session.ChannelsChanged -= LayersChanged; } }
    private void CanvasChanged(SKRectI? area) { dirty = true; QueueRefresh(); }
    private void LayersChanged() { dirty = true; QueueRefresh(); }
    private void QueueRefresh() { if (attached && IsEffectivelyVisible && dirty && !timer.IsEnabled) timer.Start(); }
    private void ViewChanged()
    { zoom.Value = canvas.Zoom * 100; zoom.IsEnabled = session != null; preview.InvalidateVisual(); QueueRefresh(); }

    internal void RefreshThumbnail()
    {
        if (!attached || !IsEffectivelyVisible || !dirty) return;
        if (session == null) { preview.Clear(); dirty = false; return; }
        // Always render straight from the layers, never allocate a document-sized Composite for a thumbnail.
        var document = session.Document; var scale = Math.Min(1, 384d / Math.Max(document.Width, document.Height));
        using var sample = Pixels.NewWorkingColor(Math.Max(1, (int)Math.Ceiling(document.Width * scale)), Math.Max(1, (int)Math.Ceiling(document.Height * scale)), ColorDepth.Eight, displayProfile);
        session.RenderView(sample, new(0, 0, sample.Width, sample.Height), new RenderView((float)scale, SKPoint.Empty));
        preview.SetImage(Ui.ToAvaloniaBitmap(sample, 384, displayProfile), document.Width * scale / sample.Width, document.Height * scale / sample.Height); ThumbnailBuilds++; dirty = false;
    }

    internal Rect ImageBounds => preview.ImageBounds;
    internal Rect ViewFrame => preview.ViewFrame;
    internal bool OwnsPanKeys(IInputElement? focused) => ReferenceEquals(focused, preview);

    private sealed class NavigatorPreview : Control
    {
        private readonly NavigatorPanel panel; private Bitmap? image; private IPointer? pointer; private SKPoint offset; private Size coverage = new(1, 1);
        private static readonly IPen framePen = new Pen(new SolidColorBrush(Color.Parse("#FF6658")), 1.5);
        public NavigatorPreview(NavigatorPanel panel) { this.panel = panel; Focusable = true; Cursor = new Cursor(StandardCursorType.Hand); ToolTip.SetTip(this, "Click or drag to pan · wheel to zoom · arrows to pan"); }
        public Rect ImageBounds
        {
            get
            {
                if (panel.session == null || Bounds.Width < 1 || Bounds.Height < 1) return default;
                var d = panel.session.Document; var scale = Math.Min(Math.Max(0, Bounds.Width - 12) / d.Width, Math.Max(0, Bounds.Height - 12) / d.Height);
                return new((Bounds.Width - d.Width * scale) / 2, (Bounds.Height - d.Height * scale) / 2, d.Width * scale, d.Height * scale);
            }
        }
        public Rect ViewFrame
        {
            get
            {
                var box = ImageBounds; if (panel.session is not { } current || box.Width <= 0 || box.Height <= 0) return default;
                var rect = panel.canvas.VisibleDocumentRect; var scale = box.Width / current.Document.Width;
                return new(box.Left + rect.Left * scale, box.Top + rect.Top * scale, rect.Width * scale, rect.Height * scale);
            }
        }
        public void SetImage(Bitmap next, double widthFraction, double heightFraction) { var old = image; image = next; coverage = new(widthFraction, heightFraction); old?.Dispose(); InvalidateVisual(); }
        public void Clear() { var old = image; image = null; old?.Dispose(); InvalidateVisual(); }
        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Palette.Window, new Rect(Bounds.Size)); var box = ImageBounds; if (box.Width <= 0 || box.Height <= 0) return;
            using (context.PushClip(box))
            {
                for (var y = box.Top; y < box.Bottom; y += 8) for (var x = box.Left; x < box.Right; x += 8)
                    context.FillRectangle((((int)((x - box.Left) / 8) + (int)((y - box.Top) / 8)) & 1) == 0 ? Brushes.White : Brushes.LightGray, new Rect(x, y, 8, 8));
                if (image != null) context.DrawImage(image, new Rect(0, 0, image.Size.Width * coverage.Width, image.Size.Height * coverage.Height), box);
                var frame = ViewFrame;
                if (frame.Width > 0 && frame.Height > 0)
                { var visible = new Rect(frame.Center.X - Math.Max(2, frame.Width) / 2, frame.Center.Y - Math.Max(2, frame.Height) / 2, Math.Max(2, frame.Width), Math.Max(2, frame.Height)); context.DrawRectangle(null, framePen, visible); }
            }
        }
        private SKPoint DocumentPoint(Point point)
        { var box = ImageBounds; var scale = panel.session == null || box.Width <= 0 ? 1 : box.Width / panel.session.Document.Width; return new((float)((point.X - box.Left) / scale), (float)((point.Y - box.Top) / scale)); }
        private void Move(Point point)
        { var p = DocumentPoint(point); panel.canvas.CenterOn(new(p.X + offset.X, p.Y + offset.Y)); }
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e); var point = e.GetPosition(this);
            if (panel.session == null || panel.canvas.IsDragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !ImageBounds.Contains(point)) return;
            Focus(); var p = DocumentPoint(point); var center = panel.canvas.ViewportCenter;
            offset = ViewFrame.Contains(point) ? new(center.X - p.X, center.Y - p.Y) : SKPoint.Empty;
            pointer = e.Pointer; pointer.Capture(this); Move(point); e.Handled = true;
        }
        protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (pointer == e.Pointer) { Move(e.GetPosition(this)); e.Handled = true; } }
        protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); if (pointer == e.Pointer) { pointer = null; e.Pointer.Capture(null); e.Handled = true; } }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); pointer = null; }
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        { base.OnPointerWheelChanged(e); if (panel.session == null || panel.canvas.IsDragging) return; if (e.Delta.Y > 0) panel.canvas.ZoomIn(); else if (e.Delta.Y < 0) panel.canvas.ZoomOut(); e.Handled = true; }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e); if (panel.session == null || panel.canvas.IsDragging) return;
            var step = (float)((e.KeyModifiers & KeyModifiers.Shift) != 0 ? .5 : .1); var box = panel.canvas.VisibleDocumentRect; var center = panel.canvas.ViewportCenter;
            var next = e.Key switch { Key.Left => new SKPoint(center.X - box.Width * step, center.Y), Key.Right => new(center.X + box.Width * step, center.Y), Key.Up => new(center.X, center.Y - box.Height * step), Key.Down => new(center.X, center.Y + box.Height * step), _ => (SKPoint?)null };
            if (next is { } p) { panel.canvas.CenterOn(p); e.Handled = true; }
        }
    }
}
