using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Composa.ColorManagement;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>Four circular hue boundaries. Drag a handle to change its falloff, or the band to translate it.</summary>
internal sealed class HueBandEditor : Control
{
    private HueBand band;
    private readonly IBrush[] hues;
    private int selected = 4, dragging = -1;
    private double pressX;
    private double[]? pressEdges;
    private HueBand before;
    public HueBand Band { get => band; set { band = value.Normalized(); InvalidateVisual(); } }
    public event Action<HueBand>? Changed;
    private double Left => 6;
    private double TrackWidth => Math.Max(1, Bounds.Width - 12);
    private double X(double hue) => Left + HueBand.Wrap(hue) / 360 * TrackWidth;
    private double[] Edges => [band.Start, band.Start + band.FadeIn, band.Start + band.FadeIn + band.Width, band.Start + band.FadeIn + band.Width + band.FadeOut];
    public HueBandEditor(HueBand band, RgbProfile profile, RgbProfile displayProfile)
    {
        Band = band; Height = 46; Focusable = true; Cursor = new Cursor(StandardCursorType.SizeWestEast);
        hues = Enumerable.Range(0, 360).Select(i =>
        {
            var color = new HsvColor(1, i, .85, .9).ToRgb();
            return (IBrush)new SolidColorBrush(new SKColor(color.R, color.G, color.B).ToDisplay(profile, displayProfile));
        }).ToArray();
        ToolTip.SetTip(this, "Drag the four handles to set the full hue range and its feathered edges. Drag elsewhere to move the band. Left/Right: 1°, Alt: 0.1°. Up/Down: choose handle. Escape: cancel drag.");
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        for (var i = 0; i < 360; i++)
        {
            using (context.PushOpacity(.22 + .78 * band.Weight(i + .5)))
                context.FillRectangle(hues[i], new Rect(Left + i / 360.0 * TrackWidth, 12, TrackWidth / 360 + .3, 20));
        }
        var edges = Edges;
        for (var i = 0; i < edges.Length; i++)
        {
            var x = X(edges[i]); var brush = i == selected && IsFocused ? Palette.Accent : Brushes.White;
            context.DrawLine(new Pen(Brushes.Black, 3), new(x, 10), new(x, 35));
            context.DrawLine(new Pen(brush, 1.5), new(x, 10), new(x, 35));
            context.DrawRectangle(brush, new Pen(Brushes.Black), new Rect(x - 4, i is 0 or 3 ? 3 : 34, 8, 8));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); pressX = e.GetPosition(this).X; pressEdges = Edges; before = band;
        selected = Enumerable.Range(0, 4).OrderBy(i => Math.Abs(X(pressEdges[i]) - pressX)).First();
        if (Math.Abs(X(pressEdges[selected]) - pressX) > 8) selected = 4;
        dragging = selected; e.Pointer.Capture(this); e.Handled = true; InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (dragging < 0 || pressEdges == null) return;
        var delta = (e.GetPosition(this).X - pressX) / TrackWidth * 360;
        SetEdges(pressEdges, dragging, delta); e.Handled = true;
    }
    private void SetEdges(double[] original, int handle, double delta)
    {
        var edges = original.ToArray();
        if (handle == 4) for (var i = 0; i < 4; i++) edges[i] += delta;
        else edges[handle] = Math.Clamp(edges[handle] + delta, handle == 0 ? edges[3] - 360 : edges[handle - 1], handle == 3 ? edges[0] + 360 : edges[handle + 1]);
        Band = new(edges[0], edges[1] - edges[0], edges[2] - edges[1], edges[3] - edges[2]); Changed?.Invoke(band);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); dragging = -1; pressEdges = null; e.Pointer.Capture(null); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); dragging = -1; pressEdges = null; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (e.Handled) return;
        if (e.Key == Key.Escape && dragging >= 0)
        { dragging = -1; pressEdges = null; Band = before; Changed?.Invoke(band); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Right)
        { SetEdges(Edges, selected, (e.Key == Key.Left ? -1 : 1) * (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? .1 : 1)); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down) { selected = (selected + (e.Key == Key.Up ? 4 : 1)) % 5; InvalidateVisual(); e.Handled = true; }
    }
}
