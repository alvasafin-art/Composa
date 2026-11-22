using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

internal static class CanvasLayoutDrawing
{
    public static readonly SKColor Edge = new(176, 184, 195);
    public static readonly SKColor Safe = new(18, 176, 145);
    public static readonly SKColor Bleed = new(244, 123, 101);
    public static readonly SKColor Columns = new(50, 138, 255);
    public static readonly SKColor Rows = new(167, 88, 218);
    public static readonly SKColor Squares = new(201, 130, 46);

    public static void Draw(SKCanvas canvas, CanvasLayout layout, int width, int height, double resolution, SKMatrix view, float hair)
    {
        var sheet = view.MapRect(new(0, 0, width, height));
        var safe = layout.SafeBounds(width, height);
        var inside = safe.IsEmpty ? sheet : view.MapRect(safe);
        canvas.Save();
        canvas.ClipRect(sheet);
        if (layout.SquareSize is > 0 and var size && layout.SquareOpacity > 0)
        {
            var step = size * view.ScaleX;
            if (step > 0)
            {
                var visibleStep = step * Math.Max(1, MathF.Ceiling(5 * hair / step));
                using var line = new SKPaint { Color = Squares.WithAlpha(Alpha(130, layout.SquareOpacity)), StrokeWidth = hair };
                var visible = canvas.LocalClipBounds;
                for (var x = sheet.Left + Math.Max(1, MathF.Floor((visible.Left - sheet.Left) / visibleStep)) * visibleStep;
                    x < Math.Min(sheet.Right, visible.Right); x += visibleStep)
                    canvas.DrawLine(x, sheet.Top, x, sheet.Bottom, line);
                for (var y = sheet.Top + Math.Max(1, MathF.Floor((visible.Top - sheet.Top) / visibleStep)) * visibleStep;
                    y < Math.Min(sheet.Bottom, visible.Bottom); y += visibleStep)
                    canvas.DrawLine(sheet.Left, y, sheet.Right, y, line);
            }
        }
        DrawTracks(layout.Columns, width, true, Columns);
        DrawTracks(layout.Rows, height, false, Rows);
        if (!safe.IsEmpty && layout.SafeArea != default) Dashed(inside, Safe);
        canvas.Restore();
        if (layout.BleedMm > 0)
        {
            var bleed = (float)layout.BleedPixels(resolution);
            Dashed(view.MapRect(new(-bleed, -bleed, width + bleed, height + bleed)), Bleed);
        }

        void Dashed(SKRect rect, SKColor color)
        {
            using var effect = SKPathEffect.CreateDash([5 * hair, 4 * hair], 0);
            using var paint = new SKPaint { Color = color, StrokeWidth = hair, Style = SKPaintStyle.Stroke, PathEffect = effect };
            canvas.DrawRect(rect, paint);
        }

        void DrawTracks(CanvasTracks? grid, int extent, bool vertical, SKColor color)
        {
            if (grid is not { Opacity: > 0 } || grid.Geometry(extent) is not { } tracks) return;
            using var fill = new SKPaint { Color = color.WithAlpha(Alpha(49, grid.Opacity)) };
            using var edge = new SKPaint { Color = color.WithAlpha(Alpha(164, grid.Opacity)), StrokeWidth = hair, Style = SKPaintStyle.Stroke };
            var scale = vertical ? view.ScaleX : view.ScaleY;
            var stride = Math.Max(1, (int)Math.Ceiling(2 * hair / ((tracks.Size + tracks.Gap) * scale)));
            for (var i = 0; i < tracks.Count; i += stride)
            {
                var at = (float)tracks.Position(i);
                var end = (float)(tracks.Position(i) + tracks.Size);
                var a = view.MapPoint(at, at);
                var b = view.MapPoint(end, end);
                var band = vertical ? new SKRect(a.X, inside.Top, b.X, inside.Bottom)
                    : new SKRect(inside.Left, a.Y, inside.Right, b.Y);
                if ((end - at) * scale < hair)
                {
                    canvas.DrawLine(band.Left, band.Top, vertical ? band.Left : band.Right, vertical ? band.Bottom : band.Top, edge);
                    continue;
                }
                canvas.DrawRect(band, fill);
                canvas.DrawRect(band, edge);
            }
        }
    }

    private static byte Alpha(int colorAlpha, int opacity) => (byte)((colorAlpha * opacity + 50) / 100);
}
