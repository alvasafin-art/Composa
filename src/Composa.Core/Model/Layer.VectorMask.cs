using Composa.Selections;
using SkiaSharp;
using Composa.Rendering;
namespace Composa.Model;

public sealed partial class Layer
{
    private readonly Lock vectorMaskGate = new();
    private (VectorPath Path, SKBitmap? Mask, SKImage? Generation, int Width, int Height, SKBitmap Result)? combinedVectorMask;
    private (SKBitmap Source, SKImage Generation, double Density, double Feather, SKBitmap Result)? adjustedRasterMask;

    /// <summary>Non-destructive raster mask properties, evaluated before the optional vector mask.</summary>
    public SKBitmap? RenderRasterMask(bool ignoreDisabled = false)
    {
        if (Mask is not { } mask || !MaskEnabled && !ignoreDisabled) return null;
        if (MaskDensity >= 1 && MaskFeather <= 0) return mask;
        lock (vectorMaskGate)
        {
            var generation = Rendering.Pixels.ImageOf(mask);
            if (adjustedRasterMask is { } cached && ReferenceEquals(cached.Source, mask) && ReferenceEquals(cached.Generation, generation)
                && cached.Density == MaskDensity && cached.Feather == MaskFeather) return cached.Result;
            var result = Rendering.Pixels.NewMask(mask.Width, mask.Height);
            using (var canvas = new SKCanvas(result))
            using (var filter = MaskFeather > 0 ? SKImageFilter.CreateBlur((float)MaskFeather / 2, (float)MaskFeather / 2, SKShaderTileMode.Clamp) : null)
            using (var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src }) canvas.DrawBitmap(mask, 0, 0, paint);
            var density = Math.Clamp(MaskDensity, 0, 1);
            if (density != 1)
            {
                // Every row uses the same 256 alpha values, with the same rounding as the mask preview.
                Span<byte> lookup = stackalloc byte[256];
                for (var alpha = 0; alpha < lookup.Length; alpha++)
                    lookup[alpha] = (byte)Math.Clamp(Math.Round(255 - (255 - alpha) * density), 0, 255);
                var data = result.GetPixelSpan();
                int width = result.Width, height = result.Height, stride = result.RowBytes;
                for (var y = 0; y < height; y++)
                {
                    var row = data.Slice(y * stride, width);
                    for (var x = 0; x < row.Length; x++)
                        row[x] = lookup[row[x]];
                }
            }
            Rendering.Pixels.Invalidate(result);
            adjustedRasterMask = (mask, generation, MaskDensity, MaskFeather, result);
            return result;
        }
    }
    public SKBitmap? RenderMask()
    {
        var mask = AlignedRasterMask();
        if (VectorMask == null || !VectorMaskEnabled || Pixels == null) return mask;
        lock (vectorMaskGate)
        {
            var generation = mask == null ? null : Rendering.Pixels.ImageOf(mask);
            if (combinedVectorMask is { } cached && cached.Path.Equals(VectorMask) && ReferenceEquals(cached.Mask, mask) && ReferenceEquals(cached.Generation, generation) && cached.Width == Pixels.Width && cached.Height == Pixels.Height) return cached.Result;
            using var path = VectorMask.Build(Pixels.Width, Pixels.Height);
            var result = SelectionMask.FromPath(Pixels.Width, Pixels.Height, path);
            if (mask != null)
            {
                using var canvas = new SKCanvas(result); using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                canvas.DrawBitmap(mask, new SKRect(0, 0, result.Width, result.Height), paint);
            }
            combinedVectorMask = (VectorMask, mask, generation, Pixels.Width, Pixels.Height, result); return result;
        }
    }
}
