using System.Collections.Immutable;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace Composa.Model;

public readonly record struct WarpPoint(double X, double Y)
{
    public static WarpPoint operator +(WarpPoint a, WarpPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static WarpPoint operator -(WarpPoint a, WarpPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static WarpPoint operator *(WarpPoint a, double scale) => new(a.X * scale, a.Y * scale);
}

/// <summary>Source-grid region in normalized bitmap coordinates; deformation points use the same coordinates.</summary>
public readonly record struct WarpDomain(double X, double Y, double Width, double Height)
{
    public static WarpDomain Unit => new(0, 0, 1, 1);
    public WarpPoint At(double u, double v) => new(X + u * Width, Y + v * Height);
}

/// <summary>An immutable interpolating, C1-continuous surface. Nodes stay on the pointer, including internal nodes.
/// Bitmaps and undo states can share the mesh; every edit constructs a new value.</summary>
public sealed class WarpMesh : IEquatable<WarpMesh>
{
    public int Columns { get; }
    public int Rows { get; }
    public WarpDomain Domain { get; }
    public ImmutableArray<WarpPoint> Points { get; }

    [JsonConstructor]
    public WarpMesh(int columns, int rows, WarpDomain domain, ImmutableArray<WarpPoint> points)
    {
        if (columns < 1 || rows < 1 || columns > DocumentLimits.MaxWarpGridDivisions || rows > DocumentLimits.MaxWarpGridDivisions)
            throw new ArgumentOutOfRangeException(nameof(columns), $"Warp grids use 1 to {DocumentLimits.MaxWarpGridDivisions} divisions per axis.");
        if (!new[] { domain.X, domain.Y, domain.Width, domain.Height }.All(double.IsFinite) || domain.Width <= 0 || domain.Height <= 0
            || domain.X < 0 || domain.Y < 0 || domain.X + domain.Width > 1 + 1e-9 || domain.Y + domain.Height > 1 + 1e-9)
            throw new ArgumentException("The warp source region must be a positive normalized bitmap region.");
        if (points.IsDefault || points.Length != (columns + 1) * (rows + 1) || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)
            || Math.Abs(p.X) > DocumentLimits.MaxWarpCoordinate || Math.Abs(p.Y) > DocumentLimits.MaxWarpCoordinate))
            throw new ArgumentException("The warp grid has invalid control points.");
        Columns = columns; Rows = rows; Domain = domain; Points = points;
    }

    public static WarpMesh Create(int columns = 3, int rows = 3, WarpDomain? domain = null)
    {
        if (columns < 1 || rows < 1 || columns > DocumentLimits.MaxWarpGridDivisions || rows > DocumentLimits.MaxWarpGridDivisions) throw new ArgumentOutOfRangeException(nameof(columns));
        var area = domain ?? WarpDomain.Unit; var nodes = ImmutableArray.CreateBuilder<WarpPoint>((columns + 1) * (rows + 1));
        for (var y = 0; y <= rows; y++) for (var x = 0; x <= columns; x++) nodes.Add(area.At((double)x / columns, (double)y / rows));
        return new(columns, rows, area, nodes.MoveToImmutable());
    }

    [JsonIgnore]
    public bool IsIdentity
    {
        get
        {
            for (var i = 0; i < Points.Length; i++)
            {
                var expected = Domain.At((double)(i % (Columns + 1)) / Columns, (double)(i / (Columns + 1)) / Rows);
                if (Math.Abs(Points[i].X - expected.X) >= 1e-12 || Math.Abs(Points[i].Y - expected.Y) >= 1e-12) return false;
            }
            return true;
        }
    }

    public WarpMesh WithPoint(int index, WarpPoint point) => new(Columns, Rows, Domain, Points.SetItem(index, point));
    public WarpPoint Point(int column, int row) => Points[row * (Columns + 1) + column];

    /// <summary>Evaluates an interpolating Catmull-Rom patch in normalized source coordinates.</summary>
    public WarpPoint Evaluate(double u, double v)
    {
        if (!double.IsFinite(u) || !double.IsFinite(v)) throw new ArgumentException("Warp parameters must be finite.");
        u = Math.Clamp(u, 0, 1) * Columns; v = Math.Clamp(v, 0, 1) * Rows;
        var x = Math.Min(Columns - 1, (int)u); var y = Math.Min(Rows - 1, (int)v); u -= x; v -= y;
        Span<WarpPoint> row = stackalloc WarpPoint[4];
        for (var i = -1; i <= 2; i++) row[i + 1] = Cubic(Extended(x - 1, y + i), Extended(x, y + i), Extended(x + 1, y + i), Extended(x + 2, y + i), u);
        return Cubic(row[0], row[1], row[2], row[3], v);
    }

    private WarpPoint Extended(int x, int y)
    {
        if (x < 0) return Extended(0, y) * 2 - Extended(1, y);
        if (x > Columns) return Extended(Columns, y) * 2 - Extended(Columns - 1, y);
        if (y < 0) return Point(x, 0) * 2 - Point(x, 1);
        if (y > Rows) return Point(x, Rows) * 2 - Point(x, Rows - 1);
        return Point(x, y);
    }

    private static WarpPoint Cubic(WarpPoint a, WarpPoint b, WarpPoint c, WarpPoint d, double t) =>
        b + (c - a) * (.5 * t) + (a * 2 - b * 5 + c * 4 - d) * (.5 * t * t) + (b * 3 - a - c * 3 + d) * (.5 * t * t * t);

    /// <summary>Conservative bounds from each patch's exact Bernstein control hull, including cubic overshoot.</summary>
    public SKRect Bounds(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth < 1 || sourceHeight < 1) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (IsIdentity) return new((float)(Domain.X * sourceWidth), (float)(Domain.Y * sourceHeight),
            (float)((Domain.X + Domain.Width) * sourceWidth), (float)((Domain.Y + Domain.Height) * sourceHeight));
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        Span<WarpPoint> alongX = stackalloc WarpPoint[16]; Span<WarpPoint> controls = stackalloc WarpPoint[4];
        static void Bezier(WarpPoint a, WarpPoint b, WarpPoint c, WarpPoint d, Span<WarpPoint> output)
        { output[0] = b; output[1] = b + (c - a) * (1.0 / 6); output[2] = c - (d - b) * (1.0 / 6); output[3] = c; }
        for (var y = 0; y < Rows; y++) for (var x = 0; x < Columns; x++)
        {
            for (var i = -1; i <= 2; i++) Bezier(Extended(x - 1, y + i), Extended(x, y + i), Extended(x + 1, y + i), Extended(x + 2, y + i), alongX.Slice((i + 1) * 4, 4));
            for (var i = 0; i < 4; i++)
            {
                Bezier(alongX[i], alongX[4 + i], alongX[8 + i], alongX[12 + i], controls);
                foreach (var point in controls) { left = Math.Min(left, point.X); top = Math.Min(top, point.Y); right = Math.Max(right, point.X); bottom = Math.Max(bottom, point.Y); }
            }
        }
        // Outward rounding prevents converting the double hull to floats from shrinking it by a fraction of a pixel.
        return new(MathF.BitDecrement((float)(left * sourceWidth)), MathF.BitDecrement((float)(top * sourceHeight)),
            MathF.BitIncrement((float)(right * sourceWidth)), MathF.BitIncrement((float)(bottom * sourceHeight)));
    }

    /// <summary>Exact tensor Bernstein controls of one patch, in normalized source coordinates.</summary>
    internal void PatchControls(int x, int y, Span<WarpPoint> controls)
    {
        Span<WarpPoint> alongX = stackalloc WarpPoint[16]; Span<WarpPoint> column = stackalloc WarpPoint[4];
        static void Bezier(WarpPoint a, WarpPoint b, WarpPoint c, WarpPoint d, Span<WarpPoint> output)
        { output[0] = b; output[1] = b + (c - a) * (1.0 / 6); output[2] = c - (d - b) * (1.0 / 6); output[3] = c; }
        for (var i = -1; i <= 2; i++) Bezier(Extended(x - 1, y + i), Extended(x, y + i), Extended(x + 1, y + i), Extended(x + 2, y + i), alongX.Slice((i + 1) * 4, 4));
        for (var i = 0; i < 4; i++)
        {
            Bezier(alongX[i], alongX[4 + i], alongX[8 + i], alongX[12 + i], column);
            for (var j = 0; j < 4; j++) controls[j * 4 + i] = column[j];
        }
    }

    public bool Equals(WarpMesh? other) => other != null && Columns == other.Columns && Rows == other.Rows && Domain == other.Domain && Points.AsSpan().SequenceEqual(other.Points.AsSpan());
    public override bool Equals(object? obj) => obj is WarpMesh other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); hash.Add(Columns); hash.Add(Rows); hash.Add(Domain); foreach (var point in Points) hash.Add(point); return hash.ToHashCode(); }
}
