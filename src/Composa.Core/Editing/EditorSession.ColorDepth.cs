using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    /// <summary>Converts working pixels and filter sources as one undoable edit. Embedded smart-object
    /// originals keep their own mode; only their outer compatibility appearance changes precision.</summary>
    public void ChangeColorDepth(ColorDepth depth)
    {
        if (!Enum.IsDefined(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        FinishInteraction();
        if (document.Depth == depth && document.AllLayers().All(l =>
            (l.Pixels == null || Pixels.DepthOf(l.Pixels) == depth) && (l.FilterSource == null || Pixels.DepthOf(l.FilterSource) == depth))) return;
        ValidateCanvasDepth(document.Width, document.Height, depth);
        var sources = document.AllLayers().SelectMany(l => new[] { l.Pixels, l.FilterSource }).OfType<SKBitmap>()
            .Where(b => Pixels.DepthOf(b) != depth).ToHashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        ValidateDepthBudget(sources, depth);
        var converted = new Dictionary<SKBitmap, SKBitmap>(ReferenceEqualityComparer.Instance);
        var next = document.Clone(); next.Depth = depth;
        try
        {
            SKBitmap? Convert(SKBitmap? image)
            {
                if (image == null || !sources.Contains(image)) return image;
                if (!converted.TryGetValue(image, out var result)) converted[image] = result = Pixels.ConvertDepth(image, depth);
                return result;
            }
            foreach (var layer in next.AllLayers()) { layer.Pixels = Convert(layer.Pixels); layer.FilterSource = Convert(layer.FilterSource); }
            if (next.RasterPixels() > DocumentLimits.DocumentPixelBudget) throw new InvalidOperationException("The converted document exceeds its raster memory budget.");
        }
        catch { foreach (var bitmap in converted.Values) bitmap.Dispose(); throw; }
        Apply(depth == ColorDepth.Sixteen ? "16 Bits/Channel" : "8 Bits/Channel", () => document = next);
        InvalidateAll(); LayersChanged?.Invoke();
    }

    private static void ValidateCanvasDepth(int width, int height, ColorDepth depth)
    {
        if (!DocumentLimits.FitsSurface(width, height, depth == ColorDepth.Sixteen ? 16 : 4))
            throw new InvalidOperationException($"This RGB mode supports at most {DocumentLimits.MaxSide:N0} pixels a side and {DocumentLimits.MaxSurfaceBytes / (depth == ColorDepth.Sixteen ? 16 : 4) / 1_000_000:N0} megapixels per surface.");
    }

    private void ValidateDepthBudget(HashSet<SKBitmap> converting, ColorDepth depth, Layer? onlyLayer = null)
    {
        var retained = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<SmartObjectSource>(ReferenceEqualityComparer.Instance);
        foreach (var layer in document.AllLayers())
        {
            layer.SmartObject?.CollectBitmaps(retained, visited);
            if (onlyLayer != null)
            {
                if (!ReferenceEquals(layer, onlyLayer) && layer.Pixels != null) retained.Add(layer.Pixels);
                if (layer.FilterSource != null) retained.Add(layer.FilterSource);
            }
        }
        var units = document.RasterPixels();
        foreach (var image in converting)
        {
            ValidateCanvasDepth(image.Width, image.Height, depth);
            if (!retained.Contains(image)) units -= ((long)image.ByteCount + 3) / 4;
            units += (long)image.Width * image.Height * (depth == ColorDepth.Sixteen ? 4 : 1);
        }
        if (units > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException($"The conversion exceeds the document's {DocumentLimits.DocumentBudgetMegapixels:N0} MP raster budget. Merge layers or reduce image dimensions first.");
    }

    private void PreparePixelTarget(Layer layer)
    {
        if (!IsInteracting || IsEditingMask || layer.IsLive || layer.Pixels is not { } image || Pixels.DepthOf(image) == document.WorkingDepth && Pixels.ProfileOf(image).Equals(document.Profile)) return;
        ValidateDepthBudget(new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance) { image }, document.WorkingDepth, layer);
        layer.Pixels = Pixels.ConvertWorking(image, document.WorkingDepth, document.Profile);
    }
}
