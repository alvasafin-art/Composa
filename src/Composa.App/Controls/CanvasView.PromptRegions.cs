using Avalonia;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    internal event Action<PromptRegion, Point>? PromptRegionEditRequested;
    internal bool ShowPromptRegions { get; set; } = true;
    private PromptRegion? draggedRegion;
    private int regionCorner;

    private void BeginRegionDrag(Point point, int clicks)
    {
        draggedRegion = null;
        regionCorner = -1;
        if (ShowPromptRegions)
            foreach (var r in session!.Document.PromptRegions.AsEnumerable().Reverse().Where(r => r.Enabled))
            {
                SKPoint[] corners = [new(r.Left,r.Top), new(r.Right,r.Top), new(r.Left,r.Bottom), new(r.Right,r.Bottom)];
                regionCorner = Array.FindIndex(corners, p => Distance(ToScreen(p), point) < 7);
                if (regionCorner < 0 && !r.Bounds.Contains((int)pressDocument.X, (int)pressDocument.Y)) continue;
                draggedRegion = r;
                if (clicks >= 2) { PromptRegionEditRequested?.Invoke(r, point); return; }
                break;
            }
        drag = Drag.PromptRegion;
    }

    private SKRectI RegionFrame()
    {
        var doc = session!.Document;
        int x = Math.Clamp((int)Math.Round(currentDocument.X), 0, doc.Width);
        int y = Math.Clamp((int)Math.Round(currentDocument.Y), 0, doc.Height);
        var region = draggedRegion;
        if (region != null && regionCorner < 0)
        {
            var frame = region.Bounds;
            frame.Offset(Math.Clamp((int)(currentDocument.X - pressDocument.X), -frame.Left, doc.Width - frame.Right),
                Math.Clamp((int)(currentDocument.Y - pressDocument.Y), -frame.Top, doc.Height - frame.Bottom));
            return frame;
        }
        int a = region == null ? Math.Clamp((int)pressDocument.X, 0, doc.Width) : regionCorner % 2 == 0 ? region.Right : region.Left;
        int b = region == null ? Math.Clamp((int)pressDocument.Y, 0, doc.Height) : regionCorner < 2 ? region.Bottom : region.Top;
        return new(Math.Min(a,x),Math.Min(b,y),Math.Max(a,x),Math.Max(b,y));
    }

    private void FinishRegionDrag()
    {
        if (Distance(pressScreen, ToScreen(currentDocument)) < 3) return;
        var frame = RegionFrame();
        if (frame.IsEmpty) return;
        if (draggedRegion is { } r)
            session!.ChangePromptRegion(r with { Left = frame.Left, Top = frame.Top, Right = frame.Right, Bottom = frame.Bottom });
        else if (session!.AddPromptRegion(frame) is { } added)
            PromptRegionEditRequested?.Invoke(added, ToScreen(new SKPoint(frame.Left, frame.Bottom)));
    }

    private void CaptureRegions(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        if (!ShowPromptRegions) return;
        var regions = session!.Document.PromptRegions.Where(r => r.Enabled).ToList();
        if (drag == Drag.PromptRegion)
        {
            var frame = RegionFrame();
            if (draggedRegion is { } old) regions.Remove(old);
            var number = draggedRegion?.Number ?? Enumerable.Range(1,1001).First(n => session.Document.PromptRegions.All(r => r.Number != n));
            regions.Add(new(number, frame.Left, frame.Top, frame.Right, frame.Bottom));
        }
        foreach (var r in regions)
        {
            var rect = view.MapRect(r.Bounds);
            steps.Add(c =>
            {
                using var ink = new SKPaint { Color = r.Ink.Color, StrokeWidth = 2 * hair, Style = SKPaintStyle.Stroke };
                c.DrawRect(rect, ink);
                ink.Style = SKPaintStyle.Fill;
                var label = r.Number.ToString();
                using var font = new SKFont(SKTypeface.Default, 11 * hair);
                var width = Math.Max(18 * hair, font.MeasureText(label) + 6 * hair);
                var top = rect.Top - 18 * hair;
                var tag = SKRect.Create(rect.Left - hair, top, width, 18 * hair);
                c.DrawRoundRect(tag, 3 * hair, 3 * hair, ink);
                c.DrawRect(SKRect.Create(tag.Left, tag.Bottom - 4 * hair, width, 4 * hair),ink);
                ink.Color = r.Ink.Color.Red + r.Ink.Color.Green > 320 ? SKColors.Black : SKColors.White;
                ink.IsAntialias = true;
                var m = font.Metrics;
                c.DrawText(label, tag.MidX, tag.MidY - (m.Ascent + m.Descent) / 2, SKTextAlign.Center, font, ink);
            });
        }
    }
}
