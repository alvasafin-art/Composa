using Composa.Model;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    /// <summary>Preview inside an existing edit; the caller owns Begin/Commit/Cancel.</summary>
    public void PreviewBlending(Layer layer, double fill, BlendIfSettings ranges)
    {
        if (IsLocked(layer)) return;
        layer.FillOpacity = double.IsFinite(fill) ? Math.Clamp(fill, 0, 1) : 1;
        layer.BlendIf = ranges.Normalized();
        Invalidate(AffectedArea(layer));
    }

    public void SetBlending(Layer layer, double fill, BlendIfSettings ranges)
    {
        if (IsLocked(layer)) return;
        Apply("Blending Options", () => PreviewBlending(layer, fill, ranges));
        LayersChanged?.Invoke();
    }
}
