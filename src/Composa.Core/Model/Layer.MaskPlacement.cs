using Composa.Rendering;
using SkiaSharp;

namespace Composa.Model;

public sealed partial class Layer
{
    private (SKBitmap Source, SKImage Generation, SKMatrix Matrix, int Width, int Height, byte Outside, SKBitmap Result)? placedRasterMask;

    public SKMatrix MaskMatrix
    {
        get
        {
            if (Mask is not { } mask) return SKMatrix.Identity;
            if (MaskTransform == null)
                return Pixels == null ? SKMatrix.Identity : Transform.Matrix(mask.Width, mask.Height);
            var matrix = MaskTransform.Matrix(mask.Width, mask.Height);
            return MaskLinked && Pixels != null ? matrix.PostConcat(Transform.Matrix(1, 1)) : matrix;
        }
    }

    /// <summary>Changes the stored frame while keeping the mask in the supplied document position.</summary>
    public void PlaceMask(SKMatrix documentMatrix)
    {
        if (Mask is not { } mask) return;
        if (MaskLinked && Pixels != null)
        {
            if (!Transform.Matrix(1, 1).TryInvert(out var inverse))
                throw new InvalidOperationException("A mask cannot be linked to a singular layer transform.");
            documentMatrix = documentMatrix.PostConcat(inverse);
        }
        MaskTransform = LayerTransform.FromMatrix(documentMatrix, mask.Width, mask.Height);
    }

    /// <summary>Moves imported/copied content as a whole, including masks that are independent within it.</summary>
    public void TranslateContent(double dx, double dy)
    {
        var maskMatrix = MaskMatrix;
        if (Pixels != null) Transform = Transform.Translated(dx, dy);
        if (Mask != null && (!MaskLinked || Pixels == null))
            PlaceMask(maskMatrix.PostConcat(SKMatrix.CreateTranslation((float)dx, (float)dy)));
    }

    /// <summary>Coverage in the layer's source grid, including the independent mask placement.</summary>
    public SKBitmap? AlignedRasterMask(bool ignoreDisabled = false)
    {
        var source = RenderRasterMask(ignoreDisabled);
        if (source == null || Pixels == null) return source;
        if (MaskTransform == null) return source;
        if (!Matrix.TryInvert(out var inverse)) return source;
        var matrix = MaskMatrix.PostConcat(inverse);
        lock (vectorMaskGate)
        {
            var generation = Rendering.Pixels.ImageOf(source);
            var outside = (byte)Math.Clamp(Math.Round(255 - (255 - MaskDefault) * Math.Clamp(MaskDensity, 0, 1)), 0, 255);
            if (placedRasterMask is { } cached && ReferenceEquals(cached.Source, source) && ReferenceEquals(cached.Generation, generation)
                && cached.Matrix == matrix && cached.Width == Pixels.Width && cached.Height == Pixels.Height && cached.Outside == outside) return cached.Result;
            var result = Rendering.Pixels.NewMask(Pixels.Width, Pixels.Height, outside);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                canvas.Concat(in matrix);
                canvas.DrawImage(generation, 0, 0, DocumentRenderer.SamplingFor(matrix), paint);
            }
            placedRasterMask = (source, generation, matrix, Pixels.Width, Pixels.Height, outside, result);
            return result;
        }
    }
}
