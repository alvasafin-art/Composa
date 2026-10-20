using System.Collections.Immutable;
using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

/// <summary>A retained surface over an explicit source-pixel region, including new content/filter padding outside
/// its original control frame. Each cell stays a tensor polynomial, so bounds and tessellation include overshoot.</summary>
internal sealed class WarpGeometry
{
    internal sealed class Patch(double u0, double u1, double v0, double v1, ImmutableArray<WarpPoint> controls)
    {
        public double U0 { get; } = u0;
        public double U1 { get; } = u1;
        public double V0 { get; } = v0;
        public double V1 { get; } = v1;
        public ImmutableArray<WarpPoint> Controls { get; } = controls;

        public WarpPoint Evaluate(double u, double v)
        {
            Span<WarpPoint> row = stackalloc WarpPoint[4];
            for (var y = 0; y < 4; y++) row[y] = Bezier(Controls[y * 4], Controls[y * 4 + 1], Controls[y * 4 + 2], Controls[y * 4 + 3], u);
            return Bezier(row[0], row[1], row[2], row[3], v);
        }

        private static WarpPoint Bezier(WarpPoint a, WarpPoint b, WarpPoint c, WarpPoint d, double t)
        {
            var s = 1 - t;
            return a * (s * s * s) + b * (3 * s * s * t) + c * (3 * s * t * t) + d * (t * t * t);
        }
    }

    public WarpMesh Mesh { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public SKRect SourceRegion { get; }
    public SKRect Bounds { get; }
    public ImmutableArray<Patch> Patches { get; }

    public WarpGeometry(WarpMesh mesh, int sourceWidth, int sourceHeight, SKRect sourceRegion)
    {
        if (!DocumentLimits.FitsSurface(sourceWidth, sourceHeight)) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (!new[] { sourceRegion.Left, sourceRegion.Top, sourceRegion.Right, sourceRegion.Bottom }.All(float.IsFinite) || sourceRegion.Width <= 0 || sourceRegion.Height <= 0)
            throw new ArgumentException("The Warp input region must be finite and nonempty.", nameof(sourceRegion));
        Mesh = mesh; SourceWidth = sourceWidth; SourceHeight = sourceHeight; SourceRegion = sourceRegion;
        var columns = Knots((sourceRegion.Left / (double)sourceWidth - mesh.Domain.X) / mesh.Domain.Width,
            (sourceRegion.Right / (double)sourceWidth - mesh.Domain.X) / mesh.Domain.Width, mesh.Columns);
        var rows = Knots((sourceRegion.Top / (double)sourceHeight - mesh.Domain.Y) / mesh.Domain.Height,
            (sourceRegion.Bottom / (double)sourceHeight - mesh.Domain.Y) / mesh.Domain.Height, mesh.Rows);
        var patches = ImmutableArray.CreateBuilder<Patch>((columns.Length - 1) * (rows.Length - 1));
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        Span<WarpPoint> samples = stackalloc WarpPoint[16]; Span<WarpPoint> alongX = stackalloc WarpPoint[16]; Span<WarpPoint> controls = stackalloc WarpPoint[16];
        Span<WarpPoint> column = stackalloc WarpPoint[4]; Span<WarpPoint> converted = stackalloc WarpPoint[4];
        for (var y = 0; y < rows.Length - 1; y++) for (var x = 0; x < columns.Length - 1; x++)
        {
            for (var j = 0; j < 4; j++) for (var i = 0; i < 4; i++)
            {
                var u = columns[x] + (columns[x + 1] - columns[x]) * i / 3;
                var v = rows[y] + (rows[y + 1] - rows[y]) * j / 3;
                var point = mesh.EvaluateExtended(u, v); samples[j * 4 + i] = new(point.X * sourceWidth, point.Y * sourceHeight);
            }
            for (var j = 0; j < 4; j++) Interpolate(samples.Slice(j * 4, 4), alongX.Slice(j * 4, 4));
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 4; j++) column[j] = alongX[j * 4 + i];
                Interpolate(column, converted);
                for (var j = 0; j < 4; j++) controls[j * 4 + i] = converted[j];
            }
            foreach (var point in controls)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || Math.Abs(point.X) >= int.MaxValue - DocumentLimits.MaxSide || Math.Abs(point.Y) >= int.MaxValue - DocumentLimits.MaxSide)
                    throw new InvalidOperationException("The extended Warp geometry is outside the supported coordinate range.");
                left = Math.Min(left, point.X); top = Math.Min(top, point.Y); right = Math.Max(right, point.X); bottom = Math.Max(bottom, point.Y);
            }
            patches.Add(new(columns[x], columns[x + 1], rows[y], rows[y + 1], ImmutableArray.Create(controls.ToArray())));
        }
        Patches = patches.MoveToImmutable();
        Bounds = mesh.IsIdentity ? sourceRegion : new(MathF.BitDecrement((float)left), MathF.BitDecrement((float)top), MathF.BitIncrement((float)right), MathF.BitIncrement((float)bottom));
    }

    private static double[] Knots(double start, double end, int divisions)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start) throw new InvalidOperationException("The Warp source region is too small to extend safely.");
        var points = new List<double> { start };
        for (var i = 0; i <= divisions; i++) { var knot = (double)i / divisions; if (knot > start && knot < end) points.Add(knot); }
        points.Add(end); return points.ToArray();
    }

    private static void Interpolate(ReadOnlySpan<WarpPoint> values, Span<WarpPoint> controls)
    {
        controls[0] = values[0]; controls[3] = values[3];
        controls[1] = values[0] * (-5.0 / 6) + values[1] * 3 - values[2] * 1.5 + values[3] * (1.0 / 3);
        controls[2] = values[0] * (1.0 / 3) - values[1] * 1.5 + values[2] * 3 - values[3] * (5.0 / 6);
    }

    public WarpPoint SourcePoint(Patch patch, double u, double v)
    {
        var point = Mesh.Domain.At(patch.U0 + (patch.U1 - patch.U0) * u, patch.V0 + (patch.V1 - patch.V0) * v);
        return new(point.X * SourceWidth, point.Y * SourceHeight);
    }

    public int Subdivisions(double scale, double tolerance)
    {
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        static double Length(WarpPoint point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);
        double curvature = 0;
        foreach (var patch in Patches)
        {
            var b = patch.Controls; double uu = 0, vv = 0, uv = 0;
            for (var j = 0; j < 4; j++) for (var i = 0; i < 2; i++) uu = Math.Max(uu, Length((b[j * 4 + i + 2] - b[j * 4 + i + 1] * 2 + b[j * 4 + i]) * 6));
            for (var j = 0; j < 2; j++) for (var i = 0; i < 4; i++) vv = Math.Max(vv, Length((b[(j + 2) * 4 + i] - b[(j + 1) * 4 + i] * 2 + b[j * 4 + i]) * 6));
            for (var j = 0; j < 3; j++) for (var i = 0; i < 3; i++) uv = Math.Max(uv, Length((b[(j + 1) * 4 + i + 1] - b[j * 4 + i + 1] - b[(j + 1) * 4 + i] + b[j * 4 + i]) * 9));
            curvature = Math.Max(curvature, (uu + 2 * uv + vv) * scale);
        }
        var needed = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(curvature / (2 * tolerance))));
        var divisions = 1; while (divisions < needed) divisions *= 2; return divisions;
    }
}
