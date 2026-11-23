namespace Composa.Painting;

/// <summary>Coverage allocates only touched 64×64 tiles instead of a canvas-sized scratch array.</summary>
internal sealed class StrokeCoverage
{
    internal const int TileSize = 64;
    private readonly ushort[]?[] tiles;
    private readonly int columns;
    public StrokeCoverage(int width, int height)
    {
        columns = (width + 63) >> 6;
        tiles = new ushort[columns * ((height + 63) >> 6)][];
    }
    public ushort this[int x, int y]
    {
        get => tiles[(y >> 6) * columns + (x >> 6)]?[(y & 63) * 64 + (x & 63)] ?? 0;
        set => (tiles[(y >> 6) * columns + (x >> 6)] ??= new ushort[4096])[(y & 63) * 64 + (x & 63)] = value;
    }

    internal Span<ushort> WritableRowSegment(int x, int y, int length)
    {
        var tile = tiles[(y >> 6) * columns + (x >> 6)] ??= new ushort[TileSize * TileSize];
        return tile.AsSpan((y & 63) * TileSize + (x & 63), length);
    }

    internal void CopyTo(SkiaSharp.SKRectI rect, Span<ushort> output)
    {
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var row = (y >> 6) * columns;
            for (var x = rect.Left; x < rect.Right;)
            {
                var offset = x & 63;
                var length = Math.Min(64 - offset, rect.Right - x);
                var segment = output[..length];
                if (tiles[row + (x >> 6)] is { } tile)
                    tile.AsSpan((y & 63) * 64 + offset, length).CopyTo(segment);
                else
                    segment.Clear();
                output = output[length..];
                x += length;
            }
        }
    }

    internal bool CopyFrom(SkiaSharp.SKRectI rect, ReadOnlySpan<ushort> input)
    {
        var changed = false;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var row = (y >> 6) * columns;
            for (var x = rect.Left; x < rect.Right;)
            {
                var offset = x & 63;
                var length = Math.Min(64 - offset, rect.Right - x);
                var segment = input[..length];
                ref var slot = ref tiles[row + (x >> 6)];
                if (slot != null || segment.ContainsAnyExcept((ushort)0))
                {
                    var target = (slot ??= new ushort[4096]).AsSpan((y & 63) * 64 + offset, length);
                    if (!segment.SequenceEqual(target))
                    {
                        segment.CopyTo(target);
                        changed = true;
                    }
                }
                input = input[length..];
                x += length;
            }
        }
        return changed;
    }
}
