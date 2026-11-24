using SkiaSharp;

namespace Composa.Model;

/// <summary>A single Bézier contour in text-space pixels. The range is in segment indices and fractional Bézier parameters.</summary>
public sealed record TextPath(VectorPath Outline, double Start = 0, double? End = null)
{
    private int SegmentCount => Outline.Closed ? Outline.Nodes.Length : Outline.Nodes.Length - 1;

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

    internal TextPath Scaled(double x, double y) => this with
    {
        Outline = Outline with { Nodes = Outline.Nodes.Select(n => new BezierNode(n.X * x, n.Y * y, n.InX * x, n.InY * y, n.OutX * x, n.OutY * y)).ToArray() }
    };

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
