using Avalonia;
using Avalonia.Input;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    internal event Action<PromptRegion, Point>? PromptRegionEditRequested;
    private bool showPromptRegions = true;
    private int? activePromptRegion;
    private PromptRegion? draggedRegion;
    private int regionCorner = -1;

    internal bool ShowPromptRegions
    {
        get => showPromptRegions;
        set
        {
            if (showPromptRegions == value) return;
            showPromptRegions = value;
            if (!value) activePromptRegion = null;
            UpdateCursor();
            InvalidateCanvas();
        }
    }

    private void OnPromptRegionsChanged()
    {
        if (ActiveRegion() == null) activePromptRegion = null;
        if (drag == Drag.None && session?.Tool == Tool.PromptRegion) UpdateCursor();
        InvalidateCanvas();
    }

    private PromptRegion? ActiveRegion() => activePromptRegion is { } id
        ? session?.Document.PromptRegions.Find(r => r.Number == id && r.Enabled) : null;

    private static SKPoint RegionCorner(SKRect rect, int corner) =>
        new(corner % 2 == 0 ? rect.Left : rect.Right, corner < 2 ? rect.Top : rect.Bottom);

    private SKRect RegionOnScreen(PromptRegion region)
    {
        var start = ToScreen(new(region.Left, region.Top));
        var end = ToScreen(new(region.Right, region.Bottom));
        return new((float)start.X, (float)start.Y, (float)end.X, (float)end.Y);
    }

    private static SKRect RegionTag(SKRect rect, string label, SKFont font, float hair) =>
        SKRect.Create(rect.Left - hair, rect.Top - 18 * hair, Math.Max(18 * hair, font.MeasureText(label) + 6 * hair), 18 * hair);

    private PromptRegion? HitRegionTag(Point point)
    {
        if (!ShowPromptRegions || session == null) return null;
        var hair = 1f / (float)Scaling;
        var regions = session.Document.PromptRegions;
        for (var i = regions.Count - 1; i >= 0; i--)
        {
            var region = regions[i];
            if (!region.Enabled) continue;
            var rect = RegionOnScreen(region);
            if (point.Y < rect.Top - 18 * hair || point.Y >= rect.Top || point.X < rect.Left - hair) continue;
            using var font = new SKFont(SKTypeface.Default, 11 * hair);
            if (RegionTag(rect, region.Number.ToString(), font, hair).Contains((float)point.X, (float)point.Y))
                return region;
        }
        return null;
    }

    private int HitActiveRegionCorner(Point point)
    {
        if (!ShowPromptRegions || ActiveRegion() is not { } region) return -1;
        var rect = RegionOnScreen(region);
        var radius = 4 / Scaling;
        var nearest = -1;
        var distance = double.PositiveInfinity;
        for (var i = 0; i < 4; i++)
        {
            var corner = RegionCorner(rect, i);
            var dx = Math.Abs(point.X - corner.X);
            var dy = Math.Abs(point.Y - corner.Y);
            if (dx <= radius && dy <= radius && dx * dx + dy * dy < distance)
            {
                nearest = i;
                distance = dx * dx + dy * dy;
            }
        }
        return nearest;
    }

    private StandardCursorType PromptRegionCursor()
    {
        var corner = drag == Drag.PromptRegion ? regionCorner : cursorInside && !OverRuler(cursorScreen) ? HitActiveRegionCorner(cursorScreen) : -1;
        if (corner >= 0)
            return corner is 0 or 3 ? StandardCursorType.TopLeftCorner : StandardCursorType.TopRightCorner;
        if (drag == Drag.PromptRegion)
            return draggedRegion == null ? StandardCursorType.Cross : StandardCursorType.SizeAll;
        return cursorInside && !OverRuler(cursorScreen) && HitRegionTag(cursorScreen) is { } tag
            ? activePromptRegion == tag.Number ? StandardCursorType.SizeAll : StandardCursorType.Hand
            : StandardCursorType.Cross;
    }

    private void BeginRegionDrag(Point point, int clicks)
    {
        regionCorner = HitActiveRegionCorner(point);
        draggedRegion = regionCorner >= 0 ? ActiveRegion() : HitRegionTag(point);
        activePromptRegion = draggedRegion?.Number;
        if (regionCorner < 0 && draggedRegion is { } tagged && clicks >= 2)
        {
            PromptRegionEditRequested?.Invoke(tagged, point);
            return;
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
            frame.Offset(Math.Clamp((int)Math.Round(currentDocument.X - pressDocument.X), -frame.Left, doc.Width - frame.Right),
                Math.Clamp((int)Math.Round(currentDocument.Y - pressDocument.Y), -frame.Top, doc.Height - frame.Bottom));
            return frame;
        }
        if (region != null)
        {
            var start = RegionCorner(region.Bounds, regionCorner);
            x = Math.Clamp((int)Math.Round(start.X + currentDocument.X - pressDocument.X), 0, doc.Width);
            y = Math.Clamp((int)Math.Round(start.Y + currentDocument.Y - pressDocument.Y), 0, doc.Height);
        }
        int a = region == null ? Math.Clamp((int)pressDocument.X, 0, doc.Width) : regionCorner % 2 == 0 ? region.Right : region.Left;
        int b = region == null ? Math.Clamp((int)pressDocument.Y, 0, doc.Height) : regionCorner < 2 ? region.Bottom : region.Top;
        return new(Math.Min(a, x), Math.Min(b, y), Math.Max(a, x), Math.Max(b, y));
    }

    private void FinishRegionDrag()
    {
        if (Distance(pressScreen, ToScreen(currentDocument)) < 3) return;
        var frame = RegionFrame();
        if (frame.IsEmpty) return;
        if (draggedRegion is { } existing)
            session!.ChangePromptRegion(existing with { Left = frame.Left, Top = frame.Top, Right = frame.Right, Bottom = frame.Bottom });
        else if (session!.AddPromptRegion(frame) is { } added)
            PromptRegionEditRequested?.Invoke(added, ToScreen(new SKPoint(frame.Left, frame.Bottom)));
    }

    private void CaptureRegions(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        var document = session!.Document;
        var regions = ShowPromptRegions ? document.PromptRegions.Where(r => r.Enabled).ToList() : [];
        if (drag == Drag.PromptRegion && Distance(pressScreen, ToScreen(currentDocument)) >= 3)
        {
            var frame = RegionFrame();
            if (draggedRegion is { } old) regions.Remove(old);
            if (!frame.IsEmpty && (draggedRegion != null || document.PromptRegions.Count < 1000))
            {
                var number = draggedRegion?.Number ?? Enumerable.Range(1, 1000).First(n => document.PromptRegions.All(r => r.Number != n));
                regions.Add(new(number, frame.Left, frame.Top, frame.Right, frame.Bottom));
            }
        }

        var selected = session.Tool == Tool.PromptRegion ? activePromptRegion : null;
        SKRect? selection = null;
        foreach (var region in regions)
        {
            var rect = view.MapRect(region.Bounds);
            if (region.Number == selected) selection = rect;
            steps.Add(c =>
            {
                using var ink = new SKPaint { Color = region.Ink.Color, StrokeWidth = 2 * hair, Style = SKPaintStyle.Stroke, IsAntialias = true };
                c.DrawRect(rect, ink);
                ink.Style = SKPaintStyle.Fill;
                var label = region.Number.ToString();
                using var font = new SKFont(SKTypeface.Default, 11 * hair);
                var tag = RegionTag(rect, label, font, hair);
                using var badge = new SKRoundRect();
                var radius = new SKPoint(3 * hair, 3 * hair);
                badge.SetRectRadii(tag, [radius, radius, SKPoint.Empty, SKPoint.Empty]);
                c.DrawRoundRect(badge, ink);
                ink.Color = region.Ink.Color.Red + region.Ink.Color.Green > 320 ? SKColors.Black : SKColors.White;
                var m = font.Metrics;
                c.DrawText(label, tag.MidX, tag.MidY - (m.Ascent + m.Descent) / 2, SKTextAlign.Center, font, ink);
            });
        }

        if (selection is { } active)
            steps.Add(c =>
            {
                using var ink = new SKPaint { IsAntialias = true, StrokeWidth = hair };
                for (var i = 0; i < 4; i++)
                {
                    var corner = RegionCorner(active, i);
                    var handle = SKRect.Create(corner.X - 4 * hair, corner.Y - 4 * hair, 8 * hair, 8 * hair);
                    ink.Style = SKPaintStyle.Fill; ink.Color = SKColors.White;
                    c.DrawRect(handle, ink);
                    ink.Style = SKPaintStyle.Stroke; ink.Color = new SKColor(20, 26, 32);
                    c.DrawRect(handle, ink);
                }
            });
    }
}
