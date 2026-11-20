using SkiaSharp;

namespace Composa.Model;

public sealed record CornerRadii(double TopLeft, double TopRight, double BottomRight, double BottomLeft)
{
    public CornerRadii Scaled(double scale) => new(TopLeft*scale,TopRight*scale,BottomRight*scale,BottomLeft*scale);
    public CornerRadii Clamped()
    {
        double Limit(double n) => double.IsFinite(n) ? Math.Clamp(n, 0, DocumentLimits.MaxSide) : 0;
        return new(Limit(TopLeft), Limit(TopRight), Limit(BottomRight), Limit(BottomLeft));
    }
    public double At(int i) => i switch { 0 => TopLeft, 1 => TopRight, 2 => BottomRight, _ => BottomLeft };
    public CornerRadii Set(int i, double value) => i switch
    {
        0 => this with { TopLeft = value }, 1 => this with { TopRight = value },
        2 => this with { BottomRight = value }, _ => this with { BottomLeft = value }
    };
}

public sealed partial record ShapeStyle
{
    public int Points { get; init; } = 5;
    public double InnerRatio { get; init; } = .5;
    public CornerRadii? Corners { get; init; }
    public bool IsRectangle() => Kind is ShapeKind.Rectangle or ShapeKind.RoundedRectangle;
    public bool IsRadial() => Kind is ShapeKind.Polygon or ShapeKind.Star;
    public double RadiusAt(int i) => Corners?.At(i) ?? (Kind == ShapeKind.Rectangle ? 0 : CornerRadius);
    public ShapeStyle WithRadius(double radius) => this with
    {
        Kind = IsRectangle() ? radius > 0 ? ShapeKind.RoundedRectangle : ShapeKind.Rectangle : Kind,
        CornerRadius = radius, Corners = null
    };
    public ShapeStyle WithCorner(int index, double value)
    {
        var radii = Corners ?? new CornerRadii(RadiusAt(0), RadiusAt(0), RadiusAt(0), RadiusAt(0));
        return this with { Kind = ShapeKind.RoundedRectangle, Corners = radii.Set(index, value) };
    }

    /// <summary>Closed cubic contour in layer coordinates normalized to its bitmap. Shared by drawing and point editing.</summary>
    public VectorPath ParametricOutline(double width, double height, double inset = 0)
    {
        int count = IsRadial() ? Math.Clamp(Points, 3, 64) * (Kind == ShapeKind.Star ? 2 : 1) : 4;
        var vertices = new SKPoint[count];
        if (IsRadial())
        {
            for (int i = 0; i < count; i++)
            {
                double angle = i * Math.Tau / count - Math.PI / 2;
                double r = Kind == ShapeKind.Star && i % 2 == 1 ? InnerRatio : 1;
                vertices[i] = new((float)(Math.Cos(angle)*r), (float)(Math.Sin(angle)*r));
            }
            var bounds = SKRect.Create(vertices.Min(p => p.X), vertices.Min(p => p.Y),
                vertices.Max(p => p.X) - vertices.Min(p => p.X), vertices.Max(p => p.Y) - vertices.Min(p => p.Y));
            for (int i=0; i<count; i++) vertices[i] = new((vertices[i].X-bounds.Left)/bounds.Width, (vertices[i].Y-bounds.Top)/bounds.Height);
        }
        else vertices = [new(0,0),new(1,0),new(1,1),new(0,1)];
        for (int i=0; i<count; i++) vertices[i] = new((float)(inset+vertices[i].X*(width-2*inset)),(float)(inset+vertices[i].Y*(height-2*inset)));
        var nodes = new List<BezierNode>(count * 2);
        for (int i=0; i<count; i++)
        {
            var v=vertices[i];
            var a=vertices[(i+count-1)%count]-v;
            var b=vertices[(i+1)%count]-v;
            var lenA=a.Length; var lenB=b.Length;
            var radius=Math.Max(0, RadiusAt(i)-inset);
            if (radius==0 || lenA<.001 || lenB<.001) { nodes.Add(BezierNode.Corner(v.X/width,v.Y/height)); continue; }
            var u=new SKPoint(a.X/lenA,a.Y/lenA); var t=new SKPoint(b.X/lenB,b.Y/lenB);
            double half=Math.Acos(Math.Clamp(u.X*t.X+u.Y*t.Y,-1,1))/2;
            var trim=Math.Min(Math.Min(lenA,lenB)/2, radius/Math.Max(.0001,Math.Tan(half)));
            var control=4d/3*Math.Tan((Math.PI-2*half)/4)*trim*Math.Tan(half);
            var first=BezierNode.Corner((v.X+u.X*trim)/width,(v.Y+u.Y*trim)/height);
            var last=BezierNode.Corner((v.X+t.X*trim)/width,(v.Y+t.Y*trim)/height);
            nodes.Add(first with { OutX=first.X-u.X*control/width, OutY=first.Y-u.Y*control/height });
            nodes.Add(last with { InX=last.X-t.X*control/width, InY=last.Y-t.Y*control/height });
        }
        return new VectorPath { Closed=true, Nodes=nodes.ToArray() };
    }
}
