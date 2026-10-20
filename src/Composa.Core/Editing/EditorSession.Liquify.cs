using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public bool CanLiquify => !IsStroking && !IsPreviewing && !IsEditingMask && !IsEditingComponent && EditableLayer is { Pixels: not null } layer
        && !EffectiveLocks(layer).HasFlag(Model.LayerLocks.Transparency);

    /// <summary>Adopts a completed off-thread Liquify render as one edit, only while the captured source is current.
    /// Returns false without taking ownership when the input is stale or the field has no deformation.</summary>
    public bool ApplyLiquify(SKBitmap source, LiquifyField field, SKBitmap result)
    {
        if (!CanLiquify || ActiveLayer is not { } layer || !ReferenceEquals(layer.Pixels, source) || !field.HasDeformation) return false;
        if (ReferenceEquals(source, result) || result.Width != source.Width || result.Height != source.Height
            || result.ColorType != (document.WorkingDepth == Model.ColorDepth.Sixteen ? SKColorType.RgbaF32 : SKColorType.Rgba8888) || !document.Profile.Equals(Pixels.ProfileOf(result))
            || field.Width != source.Width || field.Height != source.Height) throw new ArgumentException("The Liquify result must retain its source grid, depth and profile.", nameof(result));
        var candidate = document.Clone(); candidate.Find(layer.Id)!.Pixels = result;
        if (candidate.RasterPixels() > Model.DocumentLimits.DocumentPixelBudget) throw new InvalidOperationException("The Liquify result exceeds the document raster memory budget.");
        Apply("Liquify", () => layer.Pixels = result);
        Invalidate(AffectedArea(layer)); LayersChanged?.Invoke(); return true;
    }
}
