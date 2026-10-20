using Composa.Model;
using SkiaSharp;

namespace Composa.Painting;

public enum LiquifyTool { Push, Reconstruct, Freeze, Thaw }

/// <summary>An immutable, sparse backward displacement field. Local history and background render jobs share
/// untouched tiles; colors are never stored here or repeatedly resampled during a stroke.</summary>
public sealed partial class LiquifyField
{
    private const int TileSide = 128, TilePixels = TileSide * TileSide;
    private sealed record Tile(SKPoint[]? Flow, byte[]? Frozen);
    private readonly Dictionary<int, Tile> tiles;
    private readonly int columns;
    public int Width { get; }
    public int Height { get; }
    public long AllocatedBytes => tiles.Values.Sum(t => (t.Flow == null ? 0L : TilePixels * 8L) + (t.Frozen == null ? 0L : TilePixels));
    public bool HasDeformation => tiles.Values.Any(t => t.Flow != null);

    public LiquifyField(int width, int height)
    {
        if (!DocumentLimits.FitsSurface(width, height, 9)) throw new ArgumentOutOfRangeException(nameof(width), "The Liquify field exceeds the supported surface size.");
        Width = width; Height = height; columns = (width + TileSide - 1) / TileSide; tiles = [];
    }
    private LiquifyField(LiquifyField basis, Dictionary<int, Tile> changed)
    { Width = basis.Width; Height = basis.Height; columns = basis.columns; tiles = changed; }

    /// <summary>Displacement at a source-grid pixel centre, in original pixels.</summary>
    public SKPoint DisplacementAt(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height) return default;
        return tiles.TryGetValue(y / TileSide * columns + x / TileSide, out var tile) && tile.Flow != null ? tile.Flow[y % TileSide * TileSide + x % TileSide] : default;
    }
    public byte FrozenAt(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height) return 0;
        return tiles.TryGetValue(y / TileSide * columns + x / TileSide, out var tile) && tile.Frozen != null ? tile.Frozen[y % TileSide * TileSide + x % TileSide] : (byte)0;
    }
    private SKPoint SampleFlow(float x, float y)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        int left = (int)x, top = (int)y, right = Math.Min(left + 1, Width - 1), bottom = Math.Min(top + 1, Height - 1);
        float fx = x - left, fy = y - top;
        return Scale(Scale(DisplacementAt(left, top), 1 - fx) + Scale(DisplacementAt(right, top), fx), 1 - fy)
            + Scale(Scale(DisplacementAt(left, bottom), 1 - fx) + Scale(DisplacementAt(right, bottom), fx), fy);
    }
    private static SKPoint Scale(SKPoint p, float amount) => new(p.X * amount, p.Y * amount);

    /// <summary>Applies a pointer segment. Freeze/selection protect destination pixels, including reconstruction.
    /// A push composes the inverse map with the previous field, rather than translating already sampled colors.</summary>
    public LiquifyField Stroke(SKPoint from, SKPoint to, LiquifyTool tool, double size, double strength = 1, double hardness = .5, SKBitmap? selection = null, CancellationToken token = default)
    {
        if (!Enum.IsDefined(tool) || !float.IsFinite(from.X) || !float.IsFinite(from.Y) || !float.IsFinite(to.X) || !float.IsFinite(to.Y)
            || !double.IsFinite(size) || size < 1 || size > DocumentLimits.MaxSide || !double.IsFinite(strength) || !double.IsFinite(hardness))
            throw new ArgumentOutOfRangeException(nameof(size));
        if (selection != null && (selection.ColorType != SKColorType.Alpha8 || selection.Width != Width || selection.Height != Height))
            throw new ArgumentException("The Liquify selection must be an Alpha8 source-grid plane.", nameof(selection));
        strength = Math.Clamp(strength, 0, 1); hardness = Math.Clamp(hardness, 0, 1);
        if (strength == 0 || tool == LiquifyTool.Push && from == to) return this;
        // Pointer capture may report a point beyond the window. Bound the work before subdividing the segment.
        SKPoint Clip(SKPoint p) => new(Math.Clamp(p.X, (float)-size, (float)(Width + size)), Math.Clamp(p.Y, (float)-size, (float)(Height + size)));
        from = Clip(from); to = Clip(to);
        var distance = SKPoint.Distance(from, to); var steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(.5, size * .15)));
        var result = this; var previous = from;
        for (var i = 1; i <= steps; i++)
        {
            token.ThrowIfCancellationRequested();
            var point = from + Scale(to - from, (float)i / steps);
            result = result.Dab(point, point - previous, tool, size / 2, strength, hardness, selection, token); previous = point;
        }
        return result;
    }

    private unsafe LiquifyField Dab(SKPoint centre, SKPoint delta, LiquifyTool tool, double radius, double strength, double hardness, SKBitmap? selection, CancellationToken token)
    {
        var left = Math.Max(0, (int)Math.Floor(centre.X - radius)); var right = Math.Min(Width, (int)Math.Ceiling(centre.X + radius));
        var top = Math.Max(0, (int)Math.Floor(centre.Y - radius)); var bottom = Math.Min(Height, (int)Math.Ceiling(centre.Y + radius));
        if (right <= left || bottom <= top) return this;
        Dictionary<int, Tile>? changed = null; var copied = new HashSet<int>();
        var mask = selection == null ? null : (byte*)selection.GetPixels(); var stride = selection?.RowBytes ?? 0;
        var freezing = tool is LiquifyTool.Freeze or LiquifyTool.Thaw;
        for (var y = top; y < bottom; y++) for (var x = left; x < right; x++)
        {
            if ((x & 255) == 0) token.ThrowIfCancellationRequested();
            var dx = x + .5 - centre.X; var dy = y + .5 - centre.Y; var normalized = Math.Sqrt(dx * dx + dy * dy) / radius;
            if (normalized >= 1) continue;
            var fade = hardness >= 1 ? 1 : Math.Clamp((1 - normalized) / (1 - hardness), 0, 1);
            var weight = strength * fade * fade * (3 - 2 * fade);
            if (!freezing) weight *= (1 - FrozenAt(x, y) / 255.0) * (mask == null ? 1 : mask[(long)y * stride + x] / 255.0);
            if (weight <= 0) continue;
            int key = y / TileSide * columns + x / TileSide, index = y % TileSide * TileSide + x % TileSide;
            var flow = DisplacementAt(x, y); var frozen = FrozenAt(x, y); var nextFlow = flow; var nextFrozen = frozen;
            if (freezing) nextFrozen = (byte)Math.Clamp((int)Math.Round(frozen + ((tool == LiquifyTool.Freeze ? 255 : 0) - frozen) * weight), 0, 255);
            else if (tool == LiquifyTool.Reconstruct) nextFlow = Scale(flow, (float)(1 - weight));
            else
            {
                var offset = Scale(delta, (float)weight);
                nextFlow = SampleFlow(x - offset.X, y - offset.Y) - offset;
            }
            if (Math.Abs(nextFlow.X) + Math.Abs(nextFlow.Y) < .00001f) nextFlow = default;
            if (flow == nextFlow && frozen == nextFrozen) continue;
            changed ??= new(tiles);
            if (!copied.Contains(key))
            {
                changed.TryGetValue(key, out var old);
                var tile = freezing ? new Tile(old?.Flow, old?.Frozen?.ToArray() ?? new byte[TilePixels])
                    : new Tile(old?.Flow?.ToArray() ?? new SKPoint[TilePixels], old?.Frozen);
                changed[key] = tile; copied.Add(key);
            }
            var target = changed[key]; if (freezing) target.Frozen![index] = nextFrozen; else target.Flow![index] = nextFlow;
        }
        if (changed == null) return this;
        foreach (var key in copied)
        {
            var t = changed[key];
            if (!freezing && t.Flow!.All(p => p == default)) t = t with { Flow = null };
            if (freezing && t.Frozen!.All(v => v == 0)) t = t with { Frozen = null };
            if (t.Flow == null && t.Frozen == null) changed.Remove(key); else changed[key] = t;
        }
        var result = new LiquifyField(this, changed);
        if (result.AllocatedBytes > DocumentLimits.MaxSurfaceBytes) throw new InvalidOperationException("The Liquify field exceeds the working memory limit.");
        GC.KeepAlive(selection); return result;
    }

    /// <summary>Restores the original map everywhere, keeping the local protection marks.</summary>
    public LiquifyField RestoreAll() => !HasDeformation ? this : new(this, tiles.Where(t => t.Value.Frozen != null).ToDictionary(t => t.Key, t => new Tile(null, t.Value.Frozen)));
    public LiquifyField ThawAll() => !tiles.Values.Any(t => t.Frozen != null) ? this : new(this, tiles.Where(t => t.Value.Flow != null).ToDictionary(t => t.Key, t => new Tile(t.Value.Flow, null)));

    internal IEnumerable<(object Identity, long Bytes)> Storage => tiles.Values.SelectMany(t => new[] {
        (Identity: (object?)t.Flow, Bytes: TilePixels * 8L), (Identity: (object?)t.Frozen, Bytes: (long)TilePixels) })
        .Where(t => t.Identity != null).Select(t => (t.Identity!, t.Bytes));
}
