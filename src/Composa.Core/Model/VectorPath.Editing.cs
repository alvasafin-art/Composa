using SkiaSharp;

namespace Composa.Model;

public sealed partial record VectorPath
{
    public bool HasSegment(int i) => SegmentEnd(i) >= 0;

    public int SegmentEnd(int index)
    {
        if ((uint)index >= Nodes.Length || Nodes.Length < 2) return -1;
        if (index + 1 < Nodes.Length && !Nodes[index + 1].StartsContour) return index + 1;
        if (!Closed) return -1;
        var start = index;
        while (start > 0 && !Nodes[start].StartsContour) start--;
        return start == index ? -1 : start;
    }

    public VectorPath RemoveSegment(int i)
    {
        if (!HasSegment(i)) return this;
        if (Closed && Nodes.Skip(1).Any(node => node.StartsContour)) return this;
        if (Closed)
            return this with { Closed = false, Nodes = Nodes.Skip(i + 1).Concat(Nodes.Take(i + 1)).ToArray() };
        var nodes = Nodes.ToArray();
        nodes[i + 1] = nodes[i + 1] with { StartsContour = true };
        var broken = this with { Nodes = nodes };
        var retained = nodes.Where((_, n) => broken.HasSegment(n) || broken.HasSegment(n - 1)).ToArray();
        if (retained.Length > 0) retained[0] = retained[0] with { StartsContour = false };
        return broken with { Nodes = retained };
    }

    public VectorPath BendSegment(int i, double t, double dx, double dy)
    {
        if (!HasSegment(i) || t <= 0 || t >= 1) return this;
        // The point under the cursor must move by the drag delta. The least-squares
        // solution changes the two handles as little as possible and fixes both anchors.
        double u = 1 - t, a = 3 * u * u * t, b = 3 * u * t * t, sum = a * a + b * b;
        int next = SegmentEnd(i);
        var nodes = Nodes.ToArray(); var left = nodes[i]; var right = nodes[next];
        nodes[i] = left with { OutX = left.OutX + dx * a / sum, OutY = left.OutY + dy * a / sum };
        nodes[next] = right with { InX = right.InX + dx * b / sum, InY = right.InY + dy * b / sum };
        return this with { Nodes = nodes };
    }

    private readonly record struct Point(double X, double Y)
    {
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
        public static Point operator *(Point a, double scale) => new(a.X * scale, a.Y * scale);
        public double Dot(Point b) => X * b.X + Y * b.Y;
        public double Length => Math.Sqrt(Dot(this));
        public Point Unit => Length > 0 ? this * (1 / Length) : default;
    }

    private static Point Evaluate(Point a, Point b, Point c, Point d, double t)
    {
        double u = 1 - t;
        return a * (u * u * u) + b * (3 * u * u * t) + c * (3 * u * t * t) + d * (t * t * t);
    }

    public VectorPath RemoveNode(int index, double width = 1, double height = 1)
    {
        if ((uint)index >= Nodes.Length) return this;
        if (Nodes.Length <= 2) return this with { Nodes = Nodes.Where((_, i) => i != index).ToArray(), Closed = false };
        var nodes = Nodes.ToList();
        int prev = (index + nodes.Count - 1) % nodes.Count, next = (index + 1) % nodes.Count;
        var middle = nodes[index];
        if (Closed && (index == 0 || middle.StartsContour))
        {
            prev = index;
            while (prev + 1 < nodes.Count && !nodes[prev + 1].StartsContour) prev++;
        }
        if (Closed && SegmentEnd(index) >= 0) next = SegmentEnd(index);
        if (SegmentEnd(prev) == index && HasSegment(index) && prev != next)
        {
            var left = nodes[prev]; var right = nodes[next];
            Point P(double x, double y) => new(x * width, y * height);
            var a = P(left.X, left.Y); var b = P(left.OutX, left.OutY);
            var c = P(middle.InX, middle.InY); var m = P(middle.X, middle.Y);
            var e = P(middle.OutX, middle.OutY); var f = P(right.InX, right.InY); var g = P(right.X, right.Y);
            var forward = (b != a ? b - a : c != a ? c - a : m - a).Unit;
            var backward = (f != g ? f - g : e != g ? e - g : m - g).Unit;
            // Fit lengths along the original endpoint tangents. The split parameter is also
            // optimized: a de Casteljau subdivision has a zero-error solution at its original t.
            const int samples = 24;
            var targets = Enumerable.Range(1, 2 * samples - 1).Select(i => i <= samples
                ? Evaluate(a, b, c, m, (double)i / samples) : Evaluate(m, e, f, g, (double)(i - samples) / samples)).ToArray();
            double best = double.PositiveInfinity, bestSplit = .5;
            Point first = b, last = f;
            void Fit(double split)
            {
                if (split <= 0 || split >= 1) return;
                double xx = 0, xy = 0, yy = 0, xr = 0, yr = 0, rr = 0;
                for (int i = 1; i < 2 * samples; i++)
                {
                    double t = i <= samples ? split * i / samples : split + (1 - split) * (i - samples) / samples;
                    double u = 1 - t, h = 3 * u * u * t, k = 3 * u * t * t;
                    var residual = targets[i - 1] - (a * (u * u * u + h) + g * (t * t * t + k));
                    var x = forward * h; var y = backward * k;
                    xx += x.Dot(x); xy += x.Dot(y); yy += y.Dot(y);
                    xr += x.Dot(residual); yr += y.Dot(residual); rr += residual.Dot(residual);
                }
                void Candidate(double x, double y)
                {
                    if (x < 0 || y < 0) return;
                    double error = rr + x * (xx * x + 2 * xy * y - 2 * xr) + y * (yy * y - 2 * yr);
                    if (error >= best) return;
                    best = error; bestSplit = split; first = a + forward * x; last = g + backward * y;
                }
                double det = xx * yy - xy * xy;
                if (det > 1e-16) Candidate((xr * yy - yr * xy) / det, (yr * xx - xr * xy) / det);
                Candidate(xx > 0 ? Math.Max(0, xr / xx) : 0, 0);
                Candidate(0, yy > 0 ? Math.Max(0, yr / yy) : 0);
            }
            double inLength = (m - c).Length, outLength = (e - m).Length;
            if (inLength + outLength > 0) Fit(inLength / (inLength + outLength));
            for (int i = 1; i < 32; i++) Fit(i / 32.0);
            for (double step = 1.0 / 64; step > 1e-7; step /= 2)
            {
                var center = bestSplit; Fit(center - step); Fit(center + step);
            }
            nodes[prev] = left with { OutX = first.X / width, OutY = first.Y / height };
            nodes[next] = right with { InX = last.X / width, InY = last.Y / height };
        }
        if (middle.StartsContour && next > index && next < nodes.Count) nodes[next] = nodes[next] with { StartsContour = true, StartsUnion = middle.StartsUnion };
        nodes.RemoveAt(index);
        nodes[0] = nodes[0] with { StartsContour = false };
        return this with { Nodes = nodes.ToArray(), Closed = Closed };
    }

    // Subdivide in display coordinates. Bounding boxes prune distant segments even under shear or zoom.
    public (int Segment, double T) NearestSegment(SKMatrix transform, SKPoint target, double radius)
    {
        int segment = -1; double parameter = 0, best = radius * radius;
        Point Map(double x, double y) { var p = transform.MapPoint((float)x, (float)y); return new(p.X, p.Y); }
        var hit = new Point(target.X, target.Y);
        double precision = double.IsPositiveInfinity(radius) ? .125 : radius / 64;
        for (int i = 0; i < Nodes.Length; i++)
        {
            if (!HasSegment(i)) continue;
            var n = Nodes[i]; var end = Nodes[SegmentEnd(i)];
            Search(Map(n.X, n.Y), Map(n.OutX, n.OutY), Map(end.InX, end.InY), Map(end.X, end.Y), 0, 1, 0);
            void Search(Point a, Point b, Point c, Point d, double start, double stop, int depth)
            {
                var minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)); var maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
                var minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)); var maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
                var near = new Point(Math.Clamp(hit.X, minX, maxX), Math.Clamp(hit.Y, minY, maxY)) - hit;
                if (near.Dot(near) > best) return;
                var ab = (a + b) * .5; var bc = (b + c) * .5; var cd = (c + d) * .5;
                var l = (ab + bc) * .5; var r = (bc + cd) * .5; var mid = (l + r) * .5; var t = (start + stop) * .5;
                var delta = mid - hit; var distance = delta.Dot(delta);
                if (distance < best) { best = distance; segment = i; parameter = t; }
                if (depth >= 26 || Math.Max(maxX - minX, maxY - minY) < precision) return;
                Search(a, ab, l, mid, start, t, depth + 1); Search(mid, r, cd, d, t, stop, depth + 1);
            }
        }
        return (segment, parameter);
    }
}
