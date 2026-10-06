using SkiaSharp;
namespace Composa.Model;

/// <summary>Anchor and absolute control handles, normalized to the layer grid.</summary>
public sealed record BezierNode(double X, double Y, double InX, double InY, double OutX, double OutY)
{
    public static BezierNode Corner(double x, double y) => new(x, y, x, y, x, y);
}
public sealed record VectorPath
{
    public BezierNode[] Nodes { get; init; } = [];
    public bool Closed { get; init; }
    public bool Equals(VectorPath? other) => other != null && Closed == other.Closed && Nodes.SequenceEqual(other.Nodes);
    public override int GetHashCode() { var hash = new HashCode(); hash.Add(Closed); foreach (var node in Nodes) hash.Add(node); return hash.ToHashCode(); }
    public VectorPath Normalized() => this with { Nodes = Nodes.Take(4096).Where(n => new[] { n.X, n.Y, n.InX, n.InY, n.OutX, n.OutY }.All(double.IsFinite)).ToArray() };
    public VectorPath SplitSegment(int segment, double t)
    {
        if (Nodes.Length < 2 || Nodes.Length >= 4096 || segment < 0 || segment >= Nodes.Length - (Closed ? 0 : 1)) return this;
        t = Math.Clamp(t, .01, .99); var end = (segment + 1) % Nodes.Length; var a = Nodes[segment]; var b = Nodes[end];
        static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double t) => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        var p = Lerp((a.X, a.Y), (a.OutX, a.OutY), t); var q = Lerp((a.OutX, a.OutY), (b.InX, b.InY), t); var r = Lerp((b.InX, b.InY), (b.X, b.Y), t);
        var u = Lerp(p, q, t); var v = Lerp(q, r, t); var center = Lerp(u, v, t);
        var nodes = Nodes.ToList(); nodes[segment] = a with { OutX = p.X, OutY = p.Y }; nodes[end] = b with { InX = r.X, InY = r.Y };
        nodes.Insert(segment + 1, new(center.X, center.Y, u.X, u.Y, v.X, v.Y)); return this with { Nodes = nodes.ToArray() };
    }
    public VectorPath RemoveNode(int index) => index < 0 || index >= Nodes.Length || Nodes.Length <= 2 ? this :
        this with { Nodes = Nodes.Where((_, i) => i != index).ToArray(), Closed = Closed && Nodes.Length > 3 };
    public SKPath Build(float width, float height)
    {
        var path = new SKPath(); if (Nodes.Length == 0) return path;
        path.MoveTo((float)Nodes[0].X * width, (float)Nodes[0].Y * height);
        for (var i = 1; i < Nodes.Length + (Closed ? 1 : 0); i++)
        {
            var from = Nodes[(i - 1) % Nodes.Length]; var to = Nodes[i % Nodes.Length];
            path.CubicTo((float)from.OutX * width, (float)from.OutY * height, (float)to.InX * width, (float)to.InY * height, (float)to.X * width, (float)to.Y * height);
        }
        if (Closed) path.Close(); return path;
    }
}
