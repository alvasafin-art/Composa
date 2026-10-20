using Composa.Rendering;
using SkiaSharp;

namespace Composa.Model;

public sealed partial class Layer
{
    private sealed class FilterMaskCache
    {
        public readonly Lock Gate = new();
        public (SKBitmap Mask, SKImage Generation, int Width, int Height, SKMatrix Matrix, byte Outside, SKBitmap Result)? Stencil;
        public (SKImage Pixels, SKImage Source, SKImage Stencil, SKBitmap Result)? Pixels;
    }
    private FilterMaskCache filterMaskCache = new();
    // A render proxy can place the existing source mask into a reduced or deformed appearance grid without
    // replacing/resampling its editable source plane. Never serialized as document data.
    internal SKMatrix? SmartFilterMaskPlacement { get; set; }

    public bool HasSmartFilterMask => FilterSource != null && SmartFilters.Length > 0 && SmartFilterMask != null && SmartFilterMaskEnabled;
    public SKMatrix SmartFilterMaskMatrix => SKMatrix.CreateTranslation((float)(SmartFilterMaskX + FilterPaddingX), (float)(SmartFilterMaskY + FilterPaddingY)).PostConcat(Matrix);

    private SKBitmap FilterMaskStencil()
    {
        var mask = SmartFilterMask!; var pixels = Pixels!;
        var generation = Rendering.Pixels.ImageOf(mask);
        var matrix = SmartFilterMaskPlacement ?? SKMatrix.CreateTranslation((float)(SmartFilterMaskX + FilterPaddingX), (float)(SmartFilterMaskY + FilterPaddingY));
        lock (filterMaskCache.Gate)
        {
            if (filterMaskCache.Stencil is { } cached && ReferenceEquals(cached.Mask, mask) && ReferenceEquals(cached.Generation, generation)
                && cached.Width == pixels.Width && cached.Height == pixels.Height && cached.Matrix == matrix && cached.Outside == SmartFilterMaskDefault) return cached.Result;
            var result = Rendering.Pixels.NewMask(pixels.Width, pixels.Height, SmartFilterMaskDefault);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                canvas.SetMatrix(in matrix);
                canvas.DrawImage(generation, 0, 0, DocumentRenderer.SamplingFor(matrix), paint);
            }
            filterMaskCache.Stencil = (mask, generation, pixels.Width, pixels.Height, matrix, SmartFilterMaskDefault, result);
            return result;
        }
    }

    /// <summary>Draws the filter replacement crossfaded with its untouched source. The mask controls filter influence,
    /// including alpha; it never hides the source. Shader composition avoids a full color buffer during each dab.</summary>
    internal bool DrawSmartFilterPixels(SKCanvas canvas, SKPaint paint)
    {
        if (!HasSmartFilterMask || Pixels == null) return false;
        var sampling = DocumentRenderer.SamplingFor(canvas.TotalMatrix);
        var stencil = FilterMaskStencil();
        using var mask = Rendering.Pixels.ImageOf(stencil).ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, sampling);
        using var before = Rendering.Pixels.ImageOf(FilterSource!).ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal,
            sampling, SKMatrix.CreateTranslation(FilterPaddingX, FilterPaddingY));
        using var after = Rendering.Pixels.ImageOf(Pixels).ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, sampling);
        using var originalPart = SKShader.CreateBlend(SKBlendMode.DstOut, before, mask);
        using var filteredPart = SKShader.CreateBlend(SKBlendMode.DstIn, after, mask);
        using var combined = SKShader.CreateBlend(SKBlendMode.Plus, originalPart, filteredPart);
        using var mixedPaint = new SKPaint { Shader = combined, ColorF = paint.ColorF, BlendMode = paint.BlendMode, IsAntialias = paint.IsAntialias };
        canvas.DrawRect(0, 0, Pixels.Width, Pixels.Height, mixedPaint);
        GC.KeepAlive(stencil); GC.KeepAlive(this); return true;
    }

    /// <summary>Immutable appearance pixels for effects and interchange paths that need an actual bitmap.</summary>
    public SKBitmap? RenderFilterPixels()
    {
        if (!HasSmartFilterMask || Pixels == null) return Pixels;
        var image = Rendering.Pixels.ImageOf(Pixels); var source = Rendering.Pixels.ImageOf(FilterSource!);
        var stencil = Rendering.Pixels.ImageOf(FilterMaskStencil());
        lock (filterMaskCache.Gate)
        {
            if (filterMaskCache.Pixels is { } cached && ReferenceEquals(cached.Pixels, image) && ReferenceEquals(cached.Source, source) && ReferenceEquals(cached.Stencil, stencil)) return cached.Result;
            var result = Rendering.Pixels.NewLike(Pixels);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Src }) DrawSmartFilterPixels(canvas, paint);
            filterMaskCache.Pixels = (image, source, stencil, result); return result;
        }
    }

    /// <summary>A small owned working-profile preview without materializing a full color mask result.</summary>
    public SKBitmap RenderFilterPreview(int maxSide)
    {
        if (Pixels == null) throw new InvalidOperationException("The layer has no pixels.");
        if (maxSide < 1 || maxSide > DocumentLimits.MaxSide) throw new ArgumentOutOfRangeException(nameof(maxSide));
        var scale = Math.Min(1, Math.Min((double)maxSide / Pixels.Width, (double)maxSide / Pixels.Height));
        var width = Math.Max(1, (int)Math.Round(Pixels.Width * scale)); var height = Math.Max(1, (int)Math.Round(Pixels.Height * scale));
        var result = Rendering.Pixels.NewWorkingColor(width, height, ColorDepth.Eight, Rendering.Pixels.ProfileOf(Pixels));
        using var canvas = new SKCanvas(result); canvas.Scale((float)width / Pixels.Width, (float)height / Pixels.Height);
        using var paint = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Src };
        if (!DrawSmartFilterPixels(canvas, paint)) canvas.DrawImage(Rendering.Pixels.ImageOf(Pixels), 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        return result;
    }
}
