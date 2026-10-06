namespace Composa.Painting;

/// <summary>Coverage allocates only touched 64×64 tiles instead of a canvas-sized scratch array.</summary>
internal sealed class StrokeCoverage
{
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
}
