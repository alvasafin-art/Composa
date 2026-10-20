using Composa.Model;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    /// <summary>Properties preview inside a caller-owned edit. Source mask pixels remain untouched.</summary>
    public void PreviewMaskProperties(Layer layer, double density, double feather)
    {
        if (layer.Mask == null || PixelsLocked(layer)) return;
        layer.MaskDensity = double.IsFinite(density) ? Math.Clamp(density, 0, 1) : 1;
        layer.MaskFeather = double.IsFinite(feather) ? Math.Clamp(feather, 0, 1000) : 0;
        Invalidate(AffectedArea(layer));
    }

    public void SetMaskProperties(Layer layer, double density, double feather)
    {
        if (layer.Mask == null || PixelsLocked(layer)) return;
        Apply("Layer Mask Properties", () => PreviewMaskProperties(layer, density, feather));
        LayersChanged?.Invoke();
    }
}
