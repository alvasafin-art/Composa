using Composa.Rendering;
using SkiaSharp;

namespace Composa.Model;

public sealed partial class Layer
{
    private sealed record WarpGeometryState(WarpMesh Mesh, SKImage Pixels, SKImage? Source, int PaddingX, int PaddingY, bool CurrentGrid, WarpGeometry Geometry);
    private WarpGeometryState? warpGeometry;

    /// <summary>Existing meshes use the immutable unfiltered grid; a new mesh starts in the current appearance grid
    /// and that appearance is retained as its smart source before the interactive edit starts.</summary>
    public SKSizeI WarpSourceSize
    {
        get
        {
            var basis = Warp == null ? Pixels : FilterSource ?? Pixels;
            return basis == null ? SKSizeI.Empty : new(basis.Width, basis.Height);
        }
    }
    public SKMatrix WarpMatrix => Warp != null && FilterSource != null ? SKMatrix.CreateTranslation(FilterPaddingX, FilterPaddingY).PostConcat(Matrix) : Matrix;

    public SKPoint WarpNodePosition(WarpPoint point)
    {
        var size = WarpSourceSize;
        return WarpMatrix.MapPoint((float)(point.X * size.Width), (float)(point.Y * size.Height));
    }

    public WarpPoint WarpPointAt(SKPoint documentPoint)
    {
        var size = WarpSourceSize;
        if (size.Width == 0 || !WarpMatrix.TryInvert(out var inverse)) throw new InvalidOperationException("The layer transform is singular.");
        var point = inverse.MapPoint(documentPoint); return new((double)point.X / size.Width, (double)point.Y / size.Height);
    }

    internal WarpGeometry GeometryForWarp(WarpMesh mesh, bool currentGrid = false)
    {
        var pixels = Pixels ?? throw new InvalidOperationException("A Warp surface requires image pixels.");
        var image = Rendering.Pixels.ImageOf(pixels); var source = FilterSource == null ? null : Rendering.Pixels.ImageOf(FilterSource);
        lock (warpCache.Gate)
        {
            if (warpGeometry is { } cached && cached.Mesh.Equals(mesh) && ReferenceEquals(cached.Pixels, image) && ReferenceEquals(cached.Source, source)
                && cached.PaddingX == FilterPaddingX && cached.PaddingY == FilterPaddingY && cached.CurrentGrid == currentGrid) return cached.Geometry;
            var basis = currentGrid ? pixels : FilterSource ?? pixels;
            var dx = !currentGrid && FilterSource != null ? -FilterPaddingX : 0; var dy = !currentGrid && FilterSource != null ? -FilterPaddingY : 0;
            var bounds = Rendering.Pixels.ContentBounds(pixels);
            var region = bounds.IsEmpty ? SKRect.Empty : new SKRect(bounds.Left + dx, bounds.Top + dy, bounds.Right + dx, bounds.Bottom + dy);
            if (FilterSource != null)
            {
                var original = Rendering.Pixels.ContentBounds(FilterSource);
                if (!original.IsEmpty)
                {
                    var ox = currentGrid ? FilterPaddingX : 0; var oy = currentGrid ? FilterPaddingY : 0;
                    var before = new SKRect(original.Left + ox, original.Top + oy, original.Right + ox, original.Bottom + oy);
                    region = region.IsEmpty ? before : SKRect.Union(region, before);
                }
            }
            if (region.IsEmpty) region = new(0, 0, basis.Width, basis.Height);
            var geometry = new WarpGeometry(mesh, basis.Width, basis.Height, region);
            warpGeometry = new(mesh, image, source, FilterPaddingX, FilterPaddingY, currentGrid, geometry); return geometry;
        }
    }
}
