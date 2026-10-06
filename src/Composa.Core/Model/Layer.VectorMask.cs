using Composa.Selections;
using SkiaSharp;
namespace Composa.Model;

public sealed partial class Layer
{
    private readonly Lock vectorMaskGate = new();
    private (VectorPath Path, SKBitmap? Mask, int Width, int Height, SKBitmap Result)? combinedVectorMask;
    public SKBitmap? RenderMask()
    {
        var mask = MaskEnabled ? Mask : null;
        if (VectorMask == null || !VectorMaskEnabled || Pixels == null) return mask;
        lock (vectorMaskGate)
        {
            if (combinedVectorMask is { } cached && cached.Path.Equals(VectorMask) && ReferenceEquals(cached.Mask, mask) && cached.Width == Pixels.Width && cached.Height == Pixels.Height) return cached.Result;
            using var path = VectorMask.Build(Pixels.Width, Pixels.Height);
            var result = SelectionMask.FromPath(Pixels.Width, Pixels.Height, path);
            if (mask != null)
            {
                using var canvas = new SKCanvas(result); using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                canvas.DrawBitmap(mask, new SKRect(0, 0, result.Width, result.Height), paint);
            }
            combinedVectorMask = (VectorMask, mask, Pixels.Width, Pixels.Height, result); return result;
        }
    }
}
