using SkiaSharp;

namespace Composa.Model;

/// <summary>A single Bézier contour in text-space pixels. The range is in segment indices and fractional Bézier parameters.</summary>
public sealed record TextPath(VectorPath Outline, double Start = 0, double? End = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int SegmentCount => Outline.Closed ? Outline.Nodes.Length : Outline.Nodes.Length - 1;

    internal bool IsValid()
    {
        if (Outline?.Nodes is not { Length: >= 2 and <= 4096 } nodes || !double.IsFinite(Start)
            || Start < 0 || Start >= SegmentCount
            || End is { } end && (!double.IsFinite(end) || end <= Start || end > (Outline.Closed ? Start + SegmentCount : SegmentCount))) return false;
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] is not { } n || i > 0 && (n.StartsContour || n.StartsUnion)
                || !Valid(n.X) || !Valid(n.Y) || !Valid(n.InX) || !Valid(n.InY) || !Valid(n.OutX) || !Valid(n.OutY)) return false;
        }
        return true;

        static bool Valid(double value) => Math.Abs(value) <= DocumentLimits.MaxSide * 4.0;
    }

    public TextPath WithStart(double parameter)
    {
        if (!double.IsFinite(parameter)) return this;
        const double gap = .00001;
        var limit = Outline.Closed ? SegmentCount : End ?? SegmentCount;
        var start = Math.Clamp(parameter, 0, Math.Max(0, limit - gap));
        var end = Outline.Closed && End is { } last ? start + (last - Start) : End;
        return this with { Start = start, End = end };
    }

    public SKPoint StartPoint()
    {
        using var curve = Build();
        return curve.PointCount == 0 ? SKPoint.Empty : curve.GetPoint(0);
    }

    public VectorPath MapOutline(double scaleX, double scaleY, double offsetX = 0, double offsetY = 0) =>
        Outline with { Nodes = Outline.Nodes.Select(n => new BezierNode(
            n.X * scaleX + offsetX, n.Y * scaleY + offsetY,
            n.InX * scaleX + offsetX, n.InY * scaleY + offsetY,
            n.OutX * scaleX + offsetX, n.OutY * scaleY + offsetY)).ToArray() };

    internal TextPath Scaled(double x, double y) => this with { Outline = MapOutline(x, y) };

    public SKPath Build()
    {
        if (Start == 0 && End == null) return Outline.Build(1, 1);

        var path = new SKPath();
        double end = End ?? (Outline.Closed ? Start + SegmentCount : SegmentCount);
        for (var segment = (int)Math.Floor(Start); segment < end; segment++)
        {
            var a = Outline.Nodes[segment % Outline.Nodes.Length];
            var b = Outline.Nodes[(segment + 1) % Outline.Nodes.Length];
            float from = (float)Math.Max(0, Start - segment), to = (float)Math.Min(1, end - segment);
            var p0 = new SKPoint((float)a.X, (float)a.Y);
            var p1 = new SKPoint((float)a.OutX, (float)a.OutY);
            var p2 = new SKPoint((float)b.InX, (float)b.InY);
            var p3 = new SKPoint((float)b.X, (float)b.Y);
            var (first, outgoing) = At(from);
            var (last, incoming) = At(to);
            if (path.IsEmpty) path.MoveTo(first);
            path.CubicTo(first + Scale(outgoing, to - from), last - Scale(incoming, to - from), last);

            (SKPoint Point, SKPoint TangentThird) At(float t)
            {
                var a = Lerp(p0, p1, t); var b = Lerp(p1, p2, t); var c = Lerp(p2, p3, t);
                var left = Lerp(a, b, t); var right = Lerp(b, c, t);
                return (Lerp(left, right, t), right - left);
            }
        }
        return path;

        static SKPoint Scale(SKPoint p, float t) => new(p.X * t, p.Y * t);
        static SKPoint Lerp(SKPoint a, SKPoint b, float t) => a + Scale(b - a, t);
    }
}
