using SkiaSharp;
namespace Composa.Model;

/// <summary>Anchor and absolute control handles, normalized to the layer grid.</summary>
public sealed record BezierNode(double X, double Y, double InX, double InY, double OutX, double OutY)
{
    public bool StartsContour { get; init; }
    public bool StartsUnion { get; init; }
    public static BezierNode Corner(double x, double y) => new(x, y, x, y, x, y);
}
public sealed partial record VectorPath
{
    public BezierNode[] Nodes { get; init; } = [];
    public bool Closed { get; init; }
    public bool EvenOdd { get; init; }
    public bool Equals(VectorPath? other) => other != null && Closed == other.Closed && EvenOdd == other.EvenOdd && Nodes.SequenceEqual(other.Nodes);
    public override int GetHashCode() { var hash = new HashCode(); hash.Add(Closed); hash.Add(EvenOdd); foreach (var node in Nodes) hash.Add(node); return hash.ToHashCode(); }
    public VectorPath Normalized() => this with { Nodes = Nodes.Take(4096).Where(n => new[] { n.X, n.Y, n.InX, n.InY, n.OutX, n.OutY }.All(double.IsFinite)).ToArray() };
    public VectorPath SplitSegment(int segment, double t)
    {
        if (Nodes.Length < 2 || Nodes.Length >= 4096 || segment < 0 || !HasSegment(segment)) return this;
        t = Math.Clamp(t, 0, 1); var end = SegmentEnd(segment); var a = Nodes[segment]; var b = Nodes[end];
        static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double t) => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        var p = Lerp((a.X, a.Y), (a.OutX, a.OutY), t); var q = Lerp((a.OutX, a.OutY), (b.InX, b.InY), t); var r = Lerp((b.InX, b.InY), (b.X, b.Y), t);
        var u = Lerp(p, q, t); var v = Lerp(q, r, t); var center = Lerp(u, v, t);
        var nodes = Nodes.ToList(); nodes[segment] = a with { OutX = p.X, OutY = p.Y }; nodes[end] = b with { InX = r.X, InY = r.Y };
        nodes.Insert(segment + 1, new(center.X, center.Y, u.X, u.Y, v.X, v.Y)); return this with { Nodes = nodes.ToArray() };
    }
    public SKPath Build(float width, float height)
    {
        var path = new SKPath { FillType = EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        var start = 0;
        for (var end = 1; end <= Nodes.Length; end++)
        {
            if (end != Nodes.Length && !Nodes[end].StartsUnion) continue;
            if (start == 0)
                BuildGroup(path, start, end, width, height);
            else
            {
                using var component = new SKPath { FillType = SKPathFillType.EvenOdd };
                BuildGroup(component, start, end, width, height);
                var combined = path.Op(component, SKPathOp.Union);
                if (combined != null) { path.Dispose(); path = combined; }
            }
            start = end;
        }
        return path;
    }

    private void BuildGroup(SKPath path, int start, int end, float width, float height)
    {
        for (int i = start; i < end; i++)
        {
            var from = Nodes[i];
            if (i == 0 || from.StartsContour) path.MoveTo((float)from.X * width, (float)from.Y * height);
            var next = SegmentEnd(i);
            if (next < 0) continue;
            var to = Nodes[next];
            path.CubicTo((float)from.OutX * width, (float)from.OutY * height, (float)to.InX * width, (float)to.InY * height,
                (float)to.X * width, (float)to.Y * height);
            if (next <= i) path.Close();
        }
    }
}
