using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public void AddSmartFilterMask(Layer layer, bool hideAll = false)
    {
        if (PixelsLocked(layer) || layer.FilterSource == null || layer.Pixels == null || layer.SmartFilterMask != null) return;
        Apply("Add Smart Filter Mask", () =>
        {
            var mask = document.Selection != null ? SelectionInLayerSpace(layer)! : Pixels.NewMask(layer.Pixels.Width, layer.Pixels.Height, hideAll ? (byte)0 : (byte)255);
            var candidate = document.Clone(); candidate.Find(layer.Id)!.SmartFilterMask = mask;
            if (candidate.RasterPixels() > DocumentLimits.DocumentPixelBudget) { mask.Dispose(); throw new InvalidOperationException("The smart filter mask exceeds the document raster budget."); }
            layer.SmartFilterMask = mask; layer.SmartFilterMaskEnabled = true;
            layer.SmartFilterMaskDefault = document.Selection != null || hideAll ? (byte)0 : (byte)255;
            layer.SmartFilterMaskX = -layer.FilterPaddingX; layer.SmartFilterMaskY = -layer.FilterPaddingY;
        });
        if (layer == ActiveLayer) EditingSmartFilterMask = true;
        Invalidate(AffectedArea(layer)); LayersChanged?.Invoke();
    }

    public void DeleteSmartFilterMask(Layer layer)
    {
        if (PixelsLocked(layer) || layer.SmartFilterMask == null) return;
        Apply("Delete Smart Filter Mask", () => layer.SmartFilterMask = null);
        if (layer == ActiveLayer) EditingSmartFilterMask = false;
        Invalidate(AffectedArea(layer)); LayersChanged?.Invoke();
    }

    public void SetSmartFilterMaskEnabled(Layer layer, bool enabled)
    {
        if (PixelsLocked(layer) || layer.SmartFilterMask == null || layer.SmartFilterMaskEnabled == enabled) return;
        Apply(enabled ? "Enable Smart Filter Mask" : "Disable Smart Filter Mask", () => layer.SmartFilterMaskEnabled = enabled);
        Invalidate(AffectedArea(layer)); LayersChanged?.Invoke();
    }
}
