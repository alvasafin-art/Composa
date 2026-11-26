using SkiaSharp;

namespace Composa.Model;

public readonly record struct VectorCorner(SKPoint Anchor, SKPoint Incoming, SKPoint Outgoing,
    float Angle, float Radius, float MaximumRadius)
{
    private float Inset => Radius / MathF.Tan(Angle / 2);
    private float ControlLength => (4f / 3f) * Radius * MathF.Tan((MathF.PI - Angle) / 4);
    public SKPoint Bisector
    {
        get { var sum = Incoming + Outgoing; return new SKPoint(sum.X / sum.Length, sum.Y / sum.Length); }
    }
    internal SKPoint Entry => Along(Anchor, Incoming, Inset);
    internal SKPoint Exit => Along(Anchor, Outgoing, Inset);
    internal SKPoint EntryControl => Along(Entry, Incoming, -ControlLength);
    internal SKPoint ExitControl => Along(Exit, Outgoing, -ControlLength);

    public SKPoint AlongBisector(float distance) => Along(Anchor, Bisector, distance);

    private static SKPoint Along(SKPoint point, SKPoint direction, float distance) =>
        new(point.X + direction.X * distance, point.Y + direction.Y * distance);
}

public sealed partial record VectorPath
{
    /// <summary>A circular corner between two straight edges, measured in local image pixels.</summary>
    public VectorCorner? CornerAt(int index, float width, float height)
    {
        var next = SegmentEnd(index);
        if (next < 0) return null;
        var node = Nodes[index];
        var previous = index - 1;
        if (index == 0 || node.StartsContour)
        {
            if (!Closed || node.OpensContour) return null;
            previous = ContourRange(index).End - 1;
        }
        if (previous == next) return null;
        var before = Nodes[previous];
        var after = Nodes[next];
        var anchor = Point(node.X, node.Y);
        var incoming = Point(before.X, before.Y) - anchor;
        var outgoing = Point(after.X, after.Y) - anchor;
        var a = incoming.Length;
        var b = outgoing.Length;
        if (a < .001f || b < .001f) return null;
        incoming = new SKPoint(incoming.X / a, incoming.Y / a);
        outgoing = new SKPoint(outgoing.X / b, outgoing.Y / b);
        if (!OnEdge(Point(node.InX, node.InY), incoming, a) || !OnEdge(Point(before.OutX, before.OutY), incoming, a)
            || !OnEdge(Point(node.OutX, node.OutY), outgoing, b) || !OnEdge(Point(after.InX, after.InY), outgoing, b)) return null;
        var dot = Math.Clamp(incoming.X * outgoing.X + incoming.Y * outgoing.Y, -1, 1);
        if (Math.Abs(dot) > .9999f) return null;
        var angle = MathF.Acos(dot);
        // Two neighbouring corners can each consume at most half of their common edge.
        var maximum = MathF.Min(Math.Min(a, b) / 2 * MathF.Tan(angle / 2), DocumentLimits.MaxSide);
        var radius = double.IsFinite(node.CornerRadius) ? (float)Math.Clamp(node.CornerRadius, 0, maximum) : 0;
        return new VectorCorner(anchor, incoming, outgoing, angle, radius, maximum);

        SKPoint Point(double x, double y) => new((float)x * width, (float)y * height);
        bool OnEdge(SKPoint control, SKPoint direction, float length)
        {
            var offset = control - anchor;
            var along = offset.X * direction.X + offset.Y * direction.Y;
            return along >= -.001f && along <= length + .001f
                && Math.Abs(offset.X * direction.Y - offset.Y * direction.X) <= .001f;
        }
    }
}
