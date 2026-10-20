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
        if (!double.IsFinite(scale) || scale <= 0 || scale > 1) throw new ArgumentOutOfRangeException(nameof(scale));
        if (linkedMask != null && (linkedMask.ColorType != SKColorType.Alpha8 || linkedMask.Width != source.Width || linkedMask.Height != source.Height))
            throw new ArgumentException("Linked warp coverage must use the original source grid.", nameof(linkedMask));
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = Geometry.RoundOut(mesh.Bounds(source.Width, source.Height));
        if (!DocumentLimits.FitsSurface(bounds.Width, bounds.Height, source.BytesPerPixel))
            throw new InvalidOperationException($"The deformed surface exceeds the {DocumentLimits.MaxSide}-pixel side or supported surface memory limit.");
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)); var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var result = Pixels.NewWorkingColor(width, height, Pixels.DepthOf(source), Pixels.ProfileOf(source));
        try
        {
            using var canvas = new SKCanvas(result);
            canvas.Scale((float)width / bounds.Width, (float)height / bounds.Height); canvas.Translate(-bounds.Left, -bounds.Top);
            var sampling = mesh.IsIdentity && width == bounds.Width && height == bounds.Height
                ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear);
            using var imageShader = Pixels.ImageOf(source).ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, sampling);
            using var maskShader = linkedMask == null ? null : Pixels.ImageOf(linkedMask).ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, sampling);
            using var combined = maskShader == null ? null : SKShader.CreateBlend(SKBlendMode.DstIn, imageShader, maskShader);
            // This is one material, rather than translucent triangle layers. Src also keeps native scan-conversion
            // overlaps at shared subpixel edges from accumulating alpha. In folds the last surface wins.
            using var paint = new SKPaint { Shader = combined ?? imageShader, Color = SKColors.White, IsAntialias = false, BlendMode = SKBlendMode.Src };
            if (mesh.IsIdentity)
            {
                var domain = mesh.Domain;
                canvas.DrawRect((float)(domain.X * source.Width), (float)(domain.Y * source.Height), (float)(domain.Width * source.Width), (float)(domain.Height * source.Height), paint);
            }
            else
            {
                // One subdivision count for the whole grid guarantees identical vertices along shared patch edges.
                var divisions = Subdivisions(mesh, source.Width, source.Height, Math.Max((double)width / bounds.Width, (double)height / bounds.Height));
                for (var y = 0; y < mesh.Rows; y++) for (var x = 0; x < mesh.Columns; x++)
                for (var sy = 0; sy < divisions; sy += ChunkDivisions) for (var sx = 0; sx < divisions; sx += ChunkDivisions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DrawChunk(canvas, paint, mesh, source.Width, source.Height, x, y, sx, sy, divisions);
                }
            }
            canvas.Flush(); cancellationToken.ThrowIfCancellationRequested();
            return new(result, bounds);
        }
        catch { result.Dispose(); throw; }
    }

    private static int Subdivisions(WarpMesh mesh, int width, int height, double scale)
    {
        Span<WarpPoint> b = stackalloc WarpPoint[16]; double curvature = 0;
        double Length(WarpPoint point) => Math.Sqrt(point.X * point.X * width * (double)width + point.Y * point.Y * height * (double)height) * scale;
        for (var y = 0; y < mesh.Rows; y++) for (var x = 0; x < mesh.Columns; x++)
        {
            mesh.PatchControls(x, y, b); double uu = 0, vv = 0, uv = 0;
            for (var j = 0; j < 4; j++) for (var i = 0; i < 2; i++) uu = Math.Max(uu, Length((b[j * 4 + i + 2] - b[j * 4 + i + 1] * 2 + b[j * 4 + i]) * 6));
            for (var j = 0; j < 2; j++) for (var i = 0; i < 4; i++) vv = Math.Max(vv, Length((b[(j + 2) * 4 + i] - b[(j + 1) * 4 + i] * 2 + b[j * 4 + i]) * 6));
            for (var j = 0; j < 3; j++) for (var i = 0; i < 3; i++) uv = Math.Max(uv, Length((b[(j + 1) * 4 + i + 1] - b[j * 4 + i + 1] - b[(j + 1) * 4 + i] + b[j * 4 + i]) * 9));
            // The second-order Taylor remainder bounds each triangle's piecewise affine error.
            curvature = Math.Max(curvature, uu + 2 * uv + vv);
        }
        var needed = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(curvature / (2 * Tolerance))));
        var divisions = 1; while (divisions < needed) divisions *= 2;
        return divisions;
    }

    private static void DrawChunk(SKCanvas canvas, SKPaint paint, WarpMesh mesh, int width, int height, int patchX, int patchY, int startX, int startY, int divisions)
    {
        var nx = Math.Min(ChunkDivisions, divisions - startX); var ny = Math.Min(ChunkDivisions, divisions - startY);
        var positions = new SKPoint[(nx + 1) * (ny + 1)]; var texture = new SKPoint[positions.Length]; var indices = new ushort[nx * ny * 6];
        for (var y = 0; y <= ny; y++) for (var x = 0; x <= nx; x++)
        {
            var u = (patchX + (double)(startX + x) / divisions) / mesh.Columns; var v = (patchY + (double)(startY + y) / divisions) / mesh.Rows;
            var p = mesh.Evaluate(u, v); var s = mesh.Domain.At(u, v); var index = y * (nx + 1) + x;
            positions[index] = new((float)(p.X * width), (float)(p.Y * height)); texture[index] = new((float)(s.X * width), (float)(s.Y * height));
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
