using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

public static class VectorStrokeGeometry
{
    internal static SKPath Line(ShapeStyle style, int width, int height)
    {
        var thickness = (float)style.LineWidth;
        var x = Math.Min(thickness, width) / 2;
        var y = Math.Min(thickness, height) / 2;
        var path = new SKPath();
        path.MoveTo((float)(style.StartX * width ?? x), (float)(style.StartY * height ?? y));
        path.LineTo((float)(style.EndX * width ?? width - x), (float)(style.EndY * height ?? height - y));
        return path;
    }

    internal static SKRect InkBounds(ShapeStyle style, int width, int height)
    {
        if (!style.StrokeEnabled || style.Kind != ShapeKind.Line && style.Stroke == null) return default;
        var thickness = style.Kind == ShapeKind.Line ? (float)style.LineWidth : (float)style.StrokeWidth;
        if (thickness <= 0) return default;
        using var centerline = style.Kind == ShapeKind.Line ? Line(style, width, height) : style.Path?.Build(width, height);
        if (centerline == null) return default;
        using var ink = Build(centerline, style, thickness);
        return ink.TightBounds;
    }

    public static SKPath Build(SKPath centerline, ShapeStyle style, float width)
    {
        var options = style.StrokeOptions ?? new VectorStroke();
        var cap = options.Cap ?? (style.Kind == ShapeKind.Line || style.RoundCaps ? StrokeCap.Round : StrokeCap.Butt);
        var closed = style.Path?.IsFullyClosed == true;
        var position = closed ? options.Position : StrokePosition.Center;
        var inkWidth = position == StrokePosition.Center ? width : width * 2;
        using var dash = options.Profile == StrokeProfile.Uniform ? style.Dash?.CreateEffect(width) : null;
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = inkWidth, PathEffect = dash,
            StrokeCap = cap switch { StrokeCap.Round => SKStrokeCap.Round, StrokeCap.Square => SKStrokeCap.Square, _ => SKStrokeCap.Butt },
            StrokeJoin = options.Join switch { StrokeJoin.Round => SKStrokeJoin.Round, StrokeJoin.Bevel => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter },
            StrokeMiter = (float)(1 / Math.Sin(options.MiterAngle * Math.PI / 360))
        };
        var ink = options.Profile == StrokeProfile.Uniform
            ? paint.GetFillPath(centerline) ?? new SKPath()
            : ProfileOutline(centerline, paint, options.Profile, style.Dash, width);
        if (position != StrokePosition.Center)
        {
            var clipped = ink.Op(centerline, position == StrokePosition.Inside ? SKPathOp.Intersect : SKPathOp.Difference);
            if (clipped != null) { ink.Dispose(); ink = clipped; }
        }
        if (closed || options.Start == StrokeMarker.None && options.End == StrokeMarker.None) return ink;

        using var measure = new SKPathMeasure(centerline);
        do
        {
            if (measure.Length <= 0 || measure.IsClosed) continue;
            AddMarker(options.Start, 0, -1);
            AddMarker(options.End, measure.Length, 1);
        } while (measure.NextContour());
        return ink;

        void AddMarker(StrokeMarker marker, float distance, float direction)
        {
            if (marker == StrokeMarker.None || !measure.GetPositionAndTangent(distance, out var anchor, out var tangent)) return;
            if (tangent.Length <= 0 && !measure.GetPositionAndTangent(measure.Length * (direction < 0 ? .001f : .999f), out _, out tangent)) return;
            var endpointWidth = width * ProfileScale(options.Profile, distance / measure.Length);
            using var mark = Marker(marker, endpointWidth, cap);
            var angle = MathF.Atan2(tangent.Y * direction, tangent.X * direction);
            var placement = SKMatrix.CreateRotation(angle).PostConcat(SKMatrix.CreateTranslation(anchor.X, anchor.Y));
            mark.Transform(placement);
            if (marker is StrokeMarker.Arrow or StrokeMarker.SlimArrow or StrokeMarker.Triangle)
            {
                // Hide the pointed marker's flat/round/square shaft end beneath the tip.
                // A triangle needs more clearance because its sides converge to a point.
                var cut = marker == StrokeMarker.Triangle ? -endpointWidth : 0;
                using var capArea = new SKPath();
                capArea.AddRect(new SKRect(cut, -endpointWidth * .62f, endpointWidth * .55f, endpointWidth * .62f));
                capArea.Transform(placement);
                var trimmed = ink.Op(capArea, SKPathOp.Difference);
                if (trimmed != null) { ink.Dispose(); ink = trimmed; }
            }
            var combined = ink.Op(mark, SKPathOp.Union);
            if (combined != null) { ink.Dispose(); ink = combined; }
            else ink.AddPath(mark);
        }
    }

    private static float ProfileScale(StrokeProfile profile, float t) => profile switch
    {
        StrokeProfile.TaperStart => .12f + .88f * t,
        StrokeProfile.TaperEnd => 1 - .88f * t,
        StrokeProfile.TaperBoth => .12f + .88f * (1 - Math.Abs(2 * t - 1)),
        _ => 1
    };

    private static SKPath ProfileOutline(SKPath path, SKPaint paint, StrokeProfile profile, StrokeDash? dash, float width)
    {
        using var measure = new SKPathMeasure(path);
        var outline = new SKPath();
        var fullWidth = paint.StrokeWidth;
        var ends = paint.StrokeCap;
        var intervals = dash?.Intervals.Select(n => (float)(n * (dash.Relative ? width : 1))).ToArray();
        var period = intervals?.Sum() ?? 0;
        do
        {
            var length = measure.Length;
            if (length <= 0) continue;
            var step = Math.Max(length / 96, .35f);
            if (intervals == null || period < .25 || length / period * intervals.Length > 16384)
            {
                Run(0, length);
                continue;
            }
            var phase = (float)((dash!.Offset % period + period) % period);
            var at = -phase;
            var index = 0;
            while (at < length)
            {
                var next = at + intervals[index];
                if (next > 0 && (index & 1) == 0 && next > at) Run(Math.Max(at, 0), Math.Min(next, length));
                at = next;
                index = (index + 1) % intervals.Length;
            }

            void Run(float first, float last)
            {
                if (last <= first) return;
                var subdivisions = Math.Max(1, (int)MathF.Ceiling((last - first) / step));
                paint.StrokeCap = subdivisions == 1 ? ends : SKStrokeCap.Butt;
                for (int i = 0; i < subdivisions; i++)
                {
                    var start = first + (last - first) * i / subdivisions;
                    var end = first + (last - first) * (i + 1) / subdivisions;
                    using var segment = new SKPath();
                    if (!measure.GetSegment(start, end, segment, true)) continue;
                    paint.StrokeWidth = fullWidth * ProfileScale(profile, (start + end) / (2 * length));
                    using var part = paint.GetFillPath(segment);
                    if (part != null) outline.AddPath(part);
                }
                if (subdivisions == 1 || ends == SKStrokeCap.Butt) return;
                AddCap(first, -1);
                AddCap(last, 1);
            }

            void AddCap(float distance, float direction)
            {
                if (!measure.GetPositionAndTangent(distance, out var anchor, out var tangent)) return;
                var radius = fullWidth * ProfileScale(profile, distance / length) / 2;
                if (ends == SKStrokeCap.Round) { outline.AddCircle(anchor.X, anchor.Y, radius); return; }
                var along = new SKPoint(tangent.X * radius * direction, tangent.Y * radius * direction);
                var side = new SKPoint(-along.Y, along.X);
                using var capPath = new SKPath();
                capPath.AddPoly([anchor - side, anchor + along - side, anchor + along + side, anchor + side]);
                outline.AddPath(capPath);
            }
        } while (measure.NextContour());
        return outline;
    }

    private static SKPath Marker(StrokeMarker marker, float width, StrokeCap cap)
    {
        var rounded = cap == StrokeCap.Round;
        var hollow = marker >= StrokeMarker.HollowTriangle;
        var path = new SKPath();
        if (marker is StrokeMarker.Arrow or StrokeMarker.SlimArrow)
        {
            var slim = marker == StrokeMarker.SlimArrow;
            float length = width * (slim ? 3.8f : 2.8f);
            float half = width * (slim ? 1f : 1.6f);
            path.MoveTo(-length, -half); path.LineTo(0, 0); path.LineTo(-length, half);
            return Outline(path, width * .78f);
        }

        float radius = width * 1.35f;
        var shift = hollow ? radius + (cap == StrokeCap.Butt ? 0 : width / 2) : 0;
        switch (marker)
        {
            case StrokeMarker.Circle or StrokeMarker.HollowCircle:
                path.AddCircle(shift, 0, radius);
                break;
            case StrokeMarker.Square or StrokeMarker.HollowSquare:
                var box = new SKRect(shift - radius, -radius, shift + radius, radius);
                if (rounded) path.AddRoundRect(box, radius / 3, radius / 3);
                else path.AddRect(box);
                break;
            case StrokeMarker.Diamond or StrokeMarker.HollowDiamond:
                path.AddPoly([new(shift - radius, 0), new(shift, -radius), new(shift + radius, 0), new(shift, radius)]);
                break;
            default:
                var tip = hollow ? radius * 2 + (cap == StrokeCap.Butt ? 0 : width / 2) : 0;
                path.AddPoly([new(tip, 0), new(tip - radius * 2, radius), new(tip - radius * 2, -radius)]);
                break;
        }
        if (hollow) return Outline(path, width * .55f);
        if (rounded && marker is StrokeMarker.Diamond or StrokeMarker.Triangle)
        {
            using var fillet = new SKPaint { Style = SKPaintStyle.StrokeAndFill, StrokeWidth = width * .38f, StrokeJoin = SKStrokeJoin.Round };
            var softened = fillet.GetFillPath(path);
            if (softened != null) { path.Dispose(); return softened; }
        }
        return path;

        SKPath Outline(SKPath source, float thickness)
        {
            using (source)
            using (var pencil = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = thickness,
                StrokeJoin = rounded ? SKStrokeJoin.Round : SKStrokeJoin.Miter,
                StrokeCap = rounded ? SKStrokeCap.Round : cap == StrokeCap.Square ? SKStrokeCap.Square : SKStrokeCap.Butt })
                return pencil.GetFillPath(source) ?? new SKPath();
        }
    }
}
