using System.Text;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>
/// Turns Photoshop vector layers into what this editor has: a fill rectangle or ellipse becomes a live shape, any
/// other path is drawn once into pixels. Reads the <c>vogk</c>, <c>vmsk</c>/<c>vsms</c>, <c>SoCo</c> and <c>vstk</c>
/// blocks described in Adobe's Photoshop File Formats Specification.
/// </summary>
internal static class PsdVector
{
    public sealed record Live(ShapeStyle Style, SKRectI Bounds, List<string> Notes, SKMatrix? Placement = null);
    public sealed record Raster(SKBitmap Image, SKRectI Bounds);

    /// <summary>The layer's fill color from its <c>SoCo</c> block, when it is a solid color layer.</summary>
    public static uint? FillColor(Dictionary<string, byte[]> extra)
    {
        if (extra.TryGetValue("vscg", out var fill) && fill.Length > 4 && Encoding.ASCII.GetString(fill, 0, 4) == "SoCo")
            return PsdDescriptor.Color(PsdDescriptor.Child(PsdDescriptor.ReadVersioned(fill[4..]), "Clr "));
        return extra.TryGetValue("SoCo", out var soco) ? PsdDescriptor.Color(PsdDescriptor.Child(PsdDescriptor.ReadVersioned(soco), "Clr ")) : null;
    }

    private static (bool Fill, bool Stroke, uint? StrokeColor, double StrokeWidth) StrokeSettings(Dictionary<string, byte[]> extra, bool hasFill)
    {
        if (!extra.TryGetValue("vstk", out var vstk)) return (hasFill, false, null, 1);
        var settings = PsdDescriptor.ReadVersioned(vstk);
        var fill = PsdDescriptor.Flag(settings, "fillEnabled") ?? hasFill;
        var stroke = PsdDescriptor.Flag(settings, "strokeEnabled") ?? false;
        var color = PsdDescriptor.Color(PsdDescriptor.Child(PsdDescriptor.Child(settings, "strokeStyleContent"), "Clr "));
        var width = PsdDescriptor.Number(settings, "strokeStyleLineWidth") ?? 1;
        if (PsdDescriptor.Unit(settings, "strokeStyleLineWidth") == "#Pnt") width *= (PsdDescriptor.Number(settings, "strokeStyleResolution") ?? 72) / 72;
        var opacity = PsdDescriptor.Number(settings, "strokeStyleOpacity") ?? 100;
        if (color != null) color = (color.Value & 0xFFFFFF) | (uint)Math.Round(Math.Clamp(opacity / 100, 0, 1) * 255) << 24;
        return (fill, stroke, color, width);
    }

    /// <summary>A live rectangle, rounded rectangle or ellipse when the layer is one filled with a solid color.</summary>
    public static Live? LiveShape(Dictionary<string, byte[]> extra, SKSizeI canvas, long remainingPixels)
    {
        var fill = FillColor(extra);
        var (fillEnabled, strokeEnabled, strokeColor, strokeWidth) = StrokeSettings(extra, fill != null);
        if (extra.TryGetValue("vstk", out var strokeData) && strokeEnabled)
        {
            var strokeSettings = PsdDescriptor.ReadVersioned(strokeData);
            if (PsdDescriptor.List(strokeSettings, "strokeStyleLineDashSet") is { Count: > 0 }
                || PsdDescriptor.Enumeration(strokeSettings, "strokeStyleBlendMode") is { } blend && blend != "Nrml"
                || PsdDescriptor.Enumeration(strokeSettings, "strokeStyleLineJoinType") is { } join && join != "strokeStyleMiterJoin") return null;
        }
        if (OpenLine(extra, canvas, strokeEnabled, strokeColor, strokeWidth, remainingPixels) is { } line) return line;
        if (fill == null && !strokeEnabled) return null;
        var origin = Origination(extra) ?? SharpRectangle(extra, canvas);
        if (origin == null) return null;
        var (kind, box, radius, notes) = origin.Value;
        var placement = OriginationMatrix(extra);
        if (placement is { } m && strokeEnabled)
        {
            var scale = Math.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            var other = Math.Sqrt(m.SkewX * m.SkewX + m.ScaleY * m.ScaleY);
            if (scale < 1e-6 || Math.Abs(scale - other) > 1e-4 * scale || Math.Abs(m.ScaleX * m.SkewX + m.SkewY * m.ScaleY) > 1e-4 * scale * other) return null;
            strokeWidth /= scale;
        }
        if (!double.IsFinite(strokeWidth) || strokeWidth < 0 || strokeWidth > 500) return null;
        var half = strokeEnabled && strokeColor != null ? strokeWidth / 2 : 0;
        var settings = extra.TryGetValue("vstk", out var settingsData) ? PsdDescriptor.ReadVersioned(settingsData) : null;
        var alignment = PsdDescriptor.Enumeration(settings, "strokeStyleLineAlignment");
        if (alignment is "strokeStyleAlignInside") half = 0;
        else if (alignment is "strokeStyleAlignOutside") half = strokeWidth;
        box.Inflate((float)half, (float)half);
        // Path points are 8.24 fixed-point fractions of the canvas, so a corner drawn at 20 may read back as 19.99999.
        int left = (int)Math.Round(box.Left), top = (int)Math.Round(box.Top), right = (int)Math.Round(box.Right), bottom = (int)Math.Round(box.Bottom);
        var size = PixelSize(new SKRect(left, top, right, bottom), remainingPixels);
        if (size == null) return null;
        var bounds = new SKRectI(left, top, left + size.Value.Width, top + size.Value.Height);
        var style = new ShapeStyle(radius > 0 && kind == ShapeKind.Rectangle ? ShapeKind.RoundedRectangle : kind, fill ?? strokeColor ?? 0xFF000000, radius > 0 ? radius + half : 0)
            { FillEnabled = fillEnabled && fill != null, Stroke = strokeEnabled ? strokeColor : null, StrokeWidth = strokeEnabled ? strokeWidth : 2 };
        var matrix = placement ?? SKMatrix.Identity;
        var offset = matrix.MapPoint(left, top); matrix.TransX = offset.X; matrix.TransY = offset.Y;
        return new Live(style, bounds, notes, matrix);
    }

    private static SKMatrix? OriginationMatrix(Dictionary<string, byte[]> extra)
    {
        if (!extra.TryGetValue("vogk", out var data) || data.Length < 8) return null;
        var origin = PsdDescriptor.List(PsdDescriptor.TryRead(data.AsSpan(8)), "keyDescriptorList")?.OfType<Dictionary<string, object?>>().FirstOrDefault();
        var t = PsdDescriptor.Child(origin, "Trnf");
        if (t == null) return null;
        var values = new[] { "xx", "xy", "yx", "yy", "tx", "ty" }.Select(k => PsdDescriptor.Number(t, k)).ToArray();
        if (values.Any(v => v == null)) return null;
        var m = new SKMatrix((float)values[0]!.Value, (float)values[2]!.Value, (float)values[4]!.Value,
            (float)values[1]!.Value, (float)values[3]!.Value, (float)values[5]!.Value, 0, 0, 1);
        return Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY) < 1e-8 ? null : m;
    }

    private static Live? OpenLine(Dictionary<string, byte[]> extra, SKSizeI canvas, bool stroke, uint? color, double width, long remaining)
    {
        if (!stroke || color == null || !double.IsFinite(width) || width <= 0 || width > 5000
            || !(extra.TryGetValue("vmsk", out var data) || extra.TryGetValue("vsms", out data))) return null;
        var records = Records(data).Where(r => r.Type is 0 or 1 or 2 or 3 or 4 or 5).ToArray();
        if (records.Length != 3 || records[0].Type != 3 || records[1].Type is not (4 or 5) || records[2].Type is not (4 or 5)) return null;
        var a = Knot(records[1].Body, canvas); var b = Knot(records[2].Body, canvas);
        if (Distance(a.In, a.Anchor) > .01 || Distance(a.Out, a.Anchor) > .01 || Distance(b.In, b.Anchor) > .01 || Distance(b.Out, b.Anchor) > .01) return null;
        var settings = extra.TryGetValue("vstk", out var vstk) ? PsdDescriptor.ReadVersioned(vstk) : null;
        if (PsdDescriptor.Enumeration(settings, "strokeStyleLineCapType") != "strokeStyleRoundCap") return null;
        var box = Geometry.RoundOut(new SKRect(Math.Min(a.Anchor.X, b.Anchor.X) - (float)width / 2,
            Math.Min(a.Anchor.Y, b.Anchor.Y) - (float)width / 2, Math.Max(a.Anchor.X, b.Anchor.X) + (float)width / 2, Math.Max(a.Anchor.Y, b.Anchor.Y) + (float)width / 2));
        var size = PixelSize(box, remaining); if (size == null) return null;
        var style = new ShapeStyle(ShapeKind.Line, color.Value, 0) { LineWidth = width,
            StartX = (a.Anchor.X - box.Left) / box.Width, StartY = (a.Anchor.Y - box.Top) / box.Height,
            EndX = (b.Anchor.X - box.Left) / box.Width, EndY = (b.Anchor.Y - box.Top) / box.Height };
        return new Live(style, box, []);
    }

    /// <summary>Any other vector layer drawn into pixels: the path filled and, when Photoshop drew one, stroked.</summary>
    public static Raster? Rasterized(Dictionary<string, byte[]> extra, SKSizeI canvas, long remainingPixels)
    {
        if (!(extra.TryGetValue("vmsk", out var mask) || extra.TryGetValue("vsms", out mask))) return null;
        using var path = Path(mask, canvas);
        if (path == null) return null;
        var fill = FillColor(extra);
        var (fillEnabled, strokeEnabled, strokeColor, strokeWidth) = StrokeSettings(extra, fill != null);
        fillEnabled &= fill != null;
        strokeEnabled &= strokeColor != null;
        if (!fillEnabled && !strokeEnabled) return null;
        if (!double.IsFinite(strokeWidth) || strokeWidth < 0 || strokeWidth > DocumentLimits.MaxSide) throw PsdException.TooLarge();
        var box = path.TightBounds;
        if (strokeEnabled) box.Inflate((float)Math.Ceiling(strokeWidth / 2 + 1), (float)Math.Ceiling(strokeWidth / 2 + 1));
        box = SKRect.Create(MathF.Floor(box.Left), MathF.Floor(box.Top), MathF.Ceiling(box.Right) - MathF.Floor(box.Left), MathF.Ceiling(box.Bottom) - MathF.Floor(box.Top));
        var size = PixelSize(box, remainingPixels);
        if (size == null) return null;
        var image = Pixels.NewColor(size.Value.Width, size.Value.Height);
        using (var surface = new SKCanvas(image))
        {
            surface.Translate(-box.Left, -box.Top);
            using var paint = new SKPaint { IsAntialias = true };
            if (fillEnabled)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor(fill!.Value);
                surface.DrawPath(path, paint);
            }
            if (strokeEnabled)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)strokeWidth;
                paint.StrokeJoin = SKStrokeJoin.Miter;
                paint.StrokeMiter = 10;
                paint.StrokeCap = SKStrokeCap.Butt;
                paint.Color = new SKColor(strokeColor!.Value);
                surface.DrawPath(path, paint);
            }
        }
        Pixels.Invalidate(image);
        return new Raster(image, new SKRectI((int)box.Left, (int)box.Top, (int)box.Left + size.Value.Width, (int)box.Top + size.Value.Height));
    }

    /// <summary>Refuses sizes past <see cref="DocumentLimits.MaxSide"/> and the remaining pixel budget; null for an empty box.</summary>
    private static SKSizeI? PixelSize(SKRect box, long remainingPixels)
    {
        if (!float.IsFinite(box.Left) || !float.IsFinite(box.Top) || !float.IsFinite(box.Width) || !float.IsFinite(box.Height)) return null;
        if (Math.Abs(box.Width) > DocumentLimits.MaxSide || Math.Abs(box.Height) > DocumentLimits.MaxSide) throw PsdException.TooLarge();
        if (box.Width < 1 || box.Height < 1) return null;
        int width = Math.Max(1, (int)Math.Round(box.Width)), height = Math.Max(1, (int)Math.Round(box.Height));
        if ((long)width * height > Math.Max(0, remainingPixels)) throw PsdException.TooLarge();
        return new SKSizeI(width, height);
    }

    /// <summary>Photoshop's own record of what the shape tool drew: 1 a rectangle, 2 a rounded rectangle, 5 an ellipse.</summary>
    private static (ShapeKind Kind, SKRect Box, double Radius, List<string> Notes)? Origination(Dictionary<string, byte[]> extra)
    {
        if (!extra.TryGetValue("vogk", out var vogk) || vogk.Length < 8) return null;
        // Two version numbers (1 and 16) lead the descriptor.
        var items = PsdDescriptor.TryRead(vogk.AsSpan(8));
        if (PsdDescriptor.List(items, "keyDescriptorList")?.OfType<Dictionary<string, object?>>().FirstOrDefault() is not { } shape) return null;
        var kind = PsdDescriptor.Number(shape, "keyOriginType") switch { 1 => ShapeKind.Rectangle, 2 => ShapeKind.RoundedRectangle, 5 => ShapeKind.Ellipse, _ => (ShapeKind?)null };
        if (kind == null) return null;
        var bbox = PsdDescriptor.Child(shape, "keyOriginShapeBBox");
        if (PsdDescriptor.Number(bbox, "Left") is not { } left || PsdDescriptor.Number(bbox, "Top ") is not { } top
            || PsdDescriptor.Number(bbox, "Rght") is not { } right || PsdDescriptor.Number(bbox, "Btom") is not { } bottom) return null;
        var box = new SKRect((float)left, (float)top, (float)right, (float)bottom);
        if (box.Width < 1 || box.Height < 1) return null;
        double radius = 0;
        var notes = new List<string>();
        if (kind is ShapeKind.Rectangle or ShapeKind.RoundedRectangle && PsdDescriptor.Child(shape, "keyOriginRRectRadii") is { } radii)
        {
            var corners = new[] { "topLeft", "topRight", "bottomRight", "bottomLeft" }.Select(c => PsdDescriptor.Number(radii, c)).ToArray();
            if (corners.All(c => c != null))
            {
                double low = corners.Min(c => c!.Value), high = corners.Max(c => c!.Value);
                if (high - low > 0.5) notes.Add("The rounded rectangle's corners differ; the largest radius was used for all four.");
                radius = Math.Max(0, high);
            }
        }
        return (kind.Value, box, radius, notes);
    }

    /// <summary>A path of four sharp, axis-aligned corners is a rectangle even without an origination record.</summary>
    private static (ShapeKind Kind, SKRect Box, double Radius, List<string> Notes)? SharpRectangle(Dictionary<string, byte[]> extra, SKSizeI canvas)
    {
        if (!(extra.TryGetValue("vmsk", out var mask) || extra.TryGetValue("vsms", out mask))) return null;
        var anchors = new List<SKPoint>();
        var subpaths = 0;
        foreach (var (type, body) in Records(mask))
        {
            if (type is 0 or 3) { if (++subpaths > 1) return null; continue; }
            if (type is not (1 or 2 or 4 or 5)) continue;
            var (incoming, anchor, outgoing) = Knot(body, canvas);
            if (Distance(incoming, anchor) > 0.5f || Distance(outgoing, anchor) > 0.5f) return null;
            anchors.Add(anchor);
        }
        if (anchors.Count != 4) return null;
        for (var i = 0; i < 4; i++)
        {
            SKPoint a = anchors[i], b = anchors[(i + 1) % 4];
            if (Math.Abs(a.X - b.X) > 0.5f && Math.Abs(a.Y - b.Y) > 0.5f) return null;
        }
        var box = new SKRect(anchors.Min(p => p.X), anchors.Min(p => p.Y), anchors.Max(p => p.X), anchors.Max(p => p.Y));
        return box.Width < 1 || box.Height < 1 ? null : (ShapeKind.Rectangle, box, 0, []);
    }

    /// <summary>The path records: 26 bytes each after an 8-byte header, knots as three 8.24 fixed-point points scaled to the canvas.</summary>
    private static IEnumerable<(int Type, byte[] Body)> Records(byte[] data)
    {
        for (var offset = 8; offset + 26 <= data.Length; offset += 26)
        {
            var type = (short)(data[offset] << 8 | data[offset + 1]);
            yield return (type, data[(offset + 2)..(offset + 26)]);
        }
    }

    private static (SKPoint In, SKPoint Anchor, SKPoint Out) Knot(byte[] body, SKSizeI canvas)
    {
        var cursor = new PsdCursor(body);
        SKPoint Point(ref PsdCursor c)
        {
            var y = c.I32() / (double)0x1000000;
            var x = c.I32() / (double)0x1000000;
            return new SKPoint((float)(x * canvas.Width), (float)(y * canvas.Height));
        }
        var incoming = Point(ref cursor);
        var anchor = Point(ref cursor);
        var outgoing = Point(ref cursor);
        return (incoming, anchor, outgoing);
    }

    private static float Distance(SKPoint a, SKPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static SKPath? Path(byte[] data, SKSizeI canvas)
    {
        if (data.Length < 8 || canvas.Width <= 0 || canvas.Height <= 0) return null;
        var path = new SKPath();
        var closed = true;
        var first = true;
        SKPoint previousOut = default, firstIn = default, firstAnchor = default;
        void Finish(SKPath p)
        {
            if (first) return;
            if (closed) { p.CubicTo(previousOut, firstIn, firstAnchor); p.Close(); }
            first = true;
        }
        foreach (var (type, body) in Records(data))
        {
            switch (type)
            {
                case 0 or 3:
                    Finish(path);
                    closed = type == 0;
                    break;
                case 1 or 2 or 4 or 5:
                    var (incoming, anchor, outgoing) = Knot(body, canvas);
                    if (first) { path.MoveTo(anchor); firstIn = incoming; firstAnchor = anchor; first = false; }
                    else path.CubicTo(previousOut, incoming, anchor);
                    previousOut = outgoing;
                    break;
            }
        }
        Finish(path);
        if (path.IsEmpty) { path.Dispose(); return null; }
        return path;
    }
}
