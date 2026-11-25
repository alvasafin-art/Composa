using Composa.Model;
using SkiaSharp;

namespace Composa.Text;

public sealed partial class TextLayout
{
    private readonly float pathLength;
    /// <summary>Text-space coordinates of the top-left pixel of a path text bitmap.</summary>
    public SKPoint PathOrigin { get; }

    private static SKRectI PathBounds(TextStyle style, SKPath curve)
    {
        var bounds = curve.TightBounds;
        var margin = Padding + (float)Math.Max(style.Size, style.SizeRuns?.Max(r => r.Size) ?? style.Size) * 2;
        bounds.Inflate(margin, margin);
        return Geometry.RoundOut(bounds);
    }

    public static SKPoint PathOriginFor(TextStyle style)
    {
        using var curve = style.Path?.Build();
        if (curve == null) return SKPoint.Empty;
        var bounds = PathBounds(style, curve);
        return new(bounds.Left, bounds.Top);
    }

    public SKPath? CreateLocalTextPath(bool fullOutline = false)
    {
        var curve = fullOutline ? Style.Path?.Outline.Build(1, 1) : Style.Path?.Build();
        curve?.Offset(-PathOrigin.X, -PathOrigin.Y);
        return curve;
    }

    private bool SamplePath(SKPathMeasure measure, float x, out SKPoint at, out SKPoint tangent)
    {
        var ok = measure.GetPositionAndTangent(Math.Clamp(x - Padding, 0, pathLength), out at, out tangent);
        at -= PathOrigin;
        return ok;
    }

    private void AddPathRun(SKTextBlobBuilder builder, SKPathMeasure measure, SKFont font, Line line, ushort[] glyphs, SKPoint[] positions)
    {
        var used = 0;
        var maps = new SKRotationScaleMatrix[glyphs.Length];
        var advances = font.GetGlyphWidths(glyphs);
        for (var i = 0; i < glyphs.Length; i++)
        {
            var position = positions[i];
            // Sample the tangent at the glyph's centre so the glyph stays rigid through a bend.
            var half = advances[i] / 2;
            var centre = position.X + half;
            if (centre < Padding || centre > Padding + pathLength || !SamplePath(measure, centre, out var at, out var tangent)) continue;
            at -= new SKPoint(tangent.X * half, tangent.Y * half);
            at = Offset(at, tangent, position.Y - line.Baseline);
            maps[used] = new(tangent.X, tangent.Y, at.X, at.Y);
            glyphs[used++] = glyphs[i];
        }
        if (used > 0) builder.AddRotationScaleRun(glyphs.AsSpan(0, used), font, maps.AsSpan(0, used));
    }

    private void DrawPathRule(SKCanvas canvas, SKPaint paint, SKPathMeasure measure, Line line, int start, int end, float offset)
    {
        using var rule = new SKPath();
        var from = Math.Clamp(line.X + line.Positions[start], Padding, Padding + pathLength);
        var to = Math.Clamp(line.X + line.Positions[end], from, Padding + pathLength);
        int steps = Math.Max(1, (int)Math.Ceiling((to - from) / 4));
        for (var i = 0; i <= steps; i++)
        {
            if (!SamplePath(measure, from + (to - from) * i / steps, out var at, out var tangent)) return;
            at = Offset(at, tangent, offset);
            if (i == 0) rule.MoveTo(at); else rule.LineTo(at);
        }
        paint.Style = SKPaintStyle.Stroke;
        canvas.DrawPath(rule, paint);
        paint.Style = SKPaintStyle.Fill;
    }

    public (SKPoint Top, SKPoint Bottom)? CaretSegmentAt(int index)
    {
        var (x, top, bottom) = CaretAt(index);
        if (Style.Path == null)
            return Style.IsBox && top > BlockSize - Padding ? null : (LocalPoint(x, top), LocalPoint(x, bottom));
        using var curve = Style.Path.Build();
        using var measure = new SKPathMeasure(curve, false, 2);
        return SamplePath(measure, x, out var at, out var tangent)
            ? (Offset(at, tangent, top - Lines[0].Baseline), Offset(at, tangent, bottom - Lines[0].Baseline)) : null;
    }

    public List<SKPoint[]> SelectionPolygons(int start, int end)
    {
        if (Style.Path == null)
            return SelectionRects(start, end).Select(r => new SKPoint[] { LocalPoint(r.Left, r.Top), LocalPoint(r.Right, r.Top), LocalPoint(r.Right, r.Bottom), LocalPoint(r.Left, r.Bottom) }).ToList();

        var line = Lines[0];
        using var curve = Style.Path.Build();
        using var measure = new SKPathMeasure(curve, false, 2);
        var polygons = new List<SKPoint[]>();
        int stop = Math.Min(end, line.End);
        for (var i = Math.Max(start, line.Start); i < stop; i++)
        {
            var x = line.X + line.Positions[i - line.Start];
            var next = line.X + line.Positions[i + 1 - line.Start];
            if (next <= x || x >= Padding + pathLength || next <= Padding) continue;
            if (SamplePath(measure, x, out var a, out var ta) && SamplePath(measure, next, out var b, out var tb))
                polygons.Add([Offset(a, ta, -line.Ascent), Offset(b, tb, -line.Ascent), Offset(b, tb, line.Descent), Offset(a, ta, line.Descent)]);
        }
        return polygons;
    }

    private int PathIndexAt(SKPoint point)
    {
        var line = Lines[0];
        using var curve = Style.Path!.Build();
        using var measure = new SKPathMeasure(curve, false, 2);
        var nearest = 0;
        var best = float.PositiveInfinity;
        for (var i = 0; i < line.Positions.Length; i++)
        {
            var x = line.X + line.Positions[i];
            if (x > Padding + pathLength && i > 0 && line.X + line.Positions[i - 1] > Padding + pathLength) continue;
            if (!SamplePath(measure, x, out var at, out var tangent)) continue;
            var normal = new SKPoint(-tangent.Y, tangent.X);
            var delta = point - at;
            at = Offset(at, tangent, Math.Clamp(delta.X * normal.X + delta.Y * normal.Y, -line.Ascent, line.Descent));
            delta = point - at;
            float distance = delta.X * delta.X + delta.Y * delta.Y;
            if (distance < best) { nearest = i; best = distance; }
        }
        return nearest;
    }

    private static SKPoint Offset(SKPoint point, SKPoint tangent, float normal) => new(point.X - tangent.Y * normal, point.Y + tangent.X * normal);
}
