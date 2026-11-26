using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

/// <summary>An owned appearance. Bounds remain in the original pixel grid even for reduced previews.</summary>
public sealed class WarpedPixels(SKBitmap bitmap, SKRectI bounds) : IDisposable
{
    public SKBitmap Bitmap { get; } = bitmap;
    public SKRectI Bounds { get; } = bounds;
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>Rasterizes the immutable source through an interpolating surface. It never quantizes RGB16 or converts
/// the working profile. Linked coverage is sampled in source coordinates before the deformation.</summary>
public static class WarpRenderer
{
    private const double Tolerance = .2;
    private const int ChunkDivisions = 32; // Each native vertex buffer stays well below the ushort index ceiling.

    public static WarpedPixels Render(SKBitmap source, WarpMesh mesh, SKBitmap? linkedMask = null, double scale = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(mesh);
        if (source.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32)) throw new ArgumentException("Warp requires working RGB pixels.", nameof(source));
        if (linkedMask != null && (linkedMask.ColorType != SKColorType.Alpha8 || linkedMask.Width != source.Width || linkedMask.Height != source.Height))
            throw new ArgumentException("Linked warp coverage must use the original source grid.", nameof(linkedMask));
        cancellationToken.ThrowIfCancellationRequested();
        var content = Pixels.ContentBounds(source);
        var region = content.IsEmpty ? new SKRect(0, 0, source.Width, source.Height) : new SKRect(content.Left, content.Top, content.Right, content.Bottom);
        return RenderRegion(source, new WarpGeometry(mesh, source.Width, source.Height, region), SKPoint.Empty, linkedMask, null, scale, null, cancellationToken);
    }

    /// <summary>Samples a texture and optional coverage in canonical source coordinates. Separate originals and
    /// filter results can use one geometry/output frame even when the filter adds a differently sized border.</summary>
    internal static WarpedPixels RenderRegion(SKBitmap source, WarpGeometry geometry, SKPoint sourceOrigin, SKBitmap? linkedMask = null,
        SKMatrix? maskPlacement = null, double scale = 1, SKRectI? outputBounds = null, CancellationToken cancellationToken = default,
        bool interactive = false, SKRectI? renderClip = null)
    {
        if (source.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32)) throw new ArgumentException("Warp requires working RGB pixels.", nameof(source));
        if (linkedMask != null && linkedMask.ColorType != SKColorType.Alpha8) throw new ArgumentException("Warp coverage must be Alpha8.", nameof(linkedMask));
        if (!double.IsFinite(scale) || scale <= 0 || scale > 1) throw new ArgumentOutOfRangeException(nameof(scale));
        if (!float.IsFinite(sourceOrigin.X) || !float.IsFinite(sourceOrigin.Y))
            throw new ArgumentException("Warp texture origins must be finite.");
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = outputBounds ?? Geometry.RoundOut(geometry.Bounds);
        if (!DocumentLimits.FitsSurface(bounds.Width, bounds.Height, source.BytesPerPixel))
            throw new InvalidOperationException($"The deformed surface exceeds the {DocumentLimits.MaxSide}-pixel side or supported surface memory limit.");
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)); var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var result = Pixels.NewWorkingColor(width, height, Pixels.DepthOf(source), Pixels.ProfileOf(source));
        SKBitmap? covered = null;
        try
        {
            if (linkedMask != null)
            {
                var placement = maskPlacement ?? SKMatrix.CreateTranslation(sourceOrigin.X, sourceOrigin.Y);
                placement = placement.PostConcat(SKMatrix.CreateTranslation(-sourceOrigin.X, -sourceOrigin.Y));
                covered = Pixels.Clone(source);
                using var maskCanvas = new SKCanvas(covered);
                using var coverage = Pixels.ImageOf(linkedMask).ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, DocumentRenderer.SamplingFor(placement), placement);
                using var keep = new SKPaint { Shader = coverage, BlendMode = SKBlendMode.DstIn };
                maskCanvas.DrawRect(0, 0, covered.Width, covered.Height, keep); maskCanvas.Flush(); Pixels.Invalidate(covered);
            }
            using var canvas = new SKCanvas(result);
            canvas.Scale((float)width / bounds.Width, (float)height / bounds.Height); canvas.Translate(-bounds.Left, -bounds.Top);
            var sampling = geometry.Mesh.IsIdentity && width == bounds.Width && height == bounds.Height
                ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear);
            using var imageShader = Pixels.ImageOf(covered ?? source).ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, sampling, SKMatrix.CreateTranslation(sourceOrigin.X, sourceOrigin.Y));
            // This is one material, rather than translucent triangle layers. Src also keeps native scan-conversion
            // overlaps at shared subpixel edges from accumulating alpha. In folds the last surface wins.
            using var paint = new SKPaint { Shader = imageShader, Color = SKColors.White, IsAntialias = false, BlendMode = SKBlendMode.Src };
            if (geometry.Mesh.IsIdentity)
            {
                canvas.DrawRect(geometry.SourceRegion, paint);
            }
            else
            {
                // One subdivision count for the whole grid guarantees identical vertices along shared patch edges.
                var divisions = geometry.Subdivisions(Math.Max((double)width / bounds.Width, (double)height / bounds.Height), interactive ? .5 : Tolerance);
                foreach (var patch in geometry.Patches)
                {
                    if (renderClip is { } clip && Outside(patch, clip)) continue;
                    for (var sy = 0; sy < divisions; sy += ChunkDivisions)
                    for (var sx = 0; sx < divisions; sx += ChunkDivisions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        DrawChunk(canvas, paint, geometry, patch, sx, sy, divisions, renderClip);
                    }
                }
            }
            canvas.Flush(); cancellationToken.ThrowIfCancellationRequested();
            GC.KeepAlive(source); GC.KeepAlive(linkedMask);
            return new(result, bounds);
        }
        catch { result.Dispose(); throw; }
        finally { if (covered != null) { Pixels.Invalidate(covered); covered.Dispose(); } }
    }

    private static bool Outside(WarpGeometry.Patch patch, SKRectI clip)
    {
        var left = true;
        var right = true;
        var above = true;
        var below = true;
        foreach (var point in patch.Controls)
        {
            left &= point.X < clip.Left - 1d;
            right &= point.X > clip.Right + 1d;
            above &= point.Y < clip.Top - 1d;
            below &= point.Y > clip.Bottom + 1d;
        }
        return left || right || above || below;
    }

    private static void DrawChunk(SKCanvas canvas, SKPaint paint, WarpGeometry geometry, WarpGeometry.Patch patch,
        int startX, int startY, int divisions, SKRectI? clip)
    {
        var nx = Math.Min(ChunkDivisions, divisions - startX); var ny = Math.Min(ChunkDivisions, divisions - startY);
        var positions = new SKPoint[(nx + 1) * (ny + 1)]; var texture = new SKPoint[positions.Length]; var indices = new ushort[nx * ny * 6];
        Span<WarpPoint> columns = stackalloc WarpPoint[(ChunkDivisions + 1) * 4];
        Span<float> sourceX = stackalloc float[ChunkDivisions + 1];
        for (var x = 0; x <= nx; x++)
        {
            var u = (double)(startX + x) / divisions;
            patch.EvaluateColumn(u, columns.Slice(x * 4, 4));
            sourceX[x] = (float)geometry.SourcePoint(patch, u, 0).X;
        }
        for (var y = 0; y <= ny; y++)
        {
            var v = (double)(startY + y) / divisions;
            var sourceY = (float)geometry.SourcePoint(patch, 0, v).Y;
            for (var x = 0; x <= nx; x++)
            {
                var point = WarpGeometry.Patch.EvaluateRow(columns.Slice(x * 4, 4), v);
                var index = y * (nx + 1) + x;
                positions[index] = new((float)point.X, (float)point.Y);
                texture[index] = new(sourceX[x], sourceY);
            }
        }
        if (clip is { } bounds)
        {
            var extent = new SKRect(bounds.Left - 1f, bounds.Top - 1f, bounds.Right + 1f, bounds.Bottom + 1f);
            var positionBounds = SKRect.Create(positions[0].X, positions[0].Y, 0, 0);
            foreach (var point in positions)
                positionBounds = new(Math.Min(positionBounds.Left, point.X), Math.Min(positionBounds.Top, point.Y),
                    Math.Max(positionBounds.Right, point.X), Math.Max(positionBounds.Bottom, point.Y));
            if (!extent.IntersectsWith(positionBounds)) return;
        }
        var at = 0;
        for (var y = 0; y < ny; y++) for (var x = 0; x < nx; x++)
        {
            var a = (ushort)(y * (nx + 1) + x); var b = (ushort)(a + 1); var c = (ushort)(a + nx + 2); var d = (ushort)(a + nx + 1);
            indices[at++] = a; indices[at++] = b; indices[at++] = c; indices[at++] = a; indices[at++] = c; indices[at++] = d;
        }
        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texture, null, indices);
        canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
    }
}
