using Composa.Painting;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public void AddRetouchLayer(string name, SKBitmap pixels, Composa.Model.LayerTransform transform)
    {
        var layer = Composa.Model.Layer.Raster(name, pixels); layer.Transform = transform;
        Apply("New Retouch Layer", () => document.InsertAboveActive(layer)); InvalidateAll(); LayersChanged?.Invoke();
    }
    public bool BeginPatch()
    {
        if (document.Selection == null || IsEditingMask) return false;
        var area = SelectionMask.Bounds(document.Selection);
        if ((long)area.Width * area.Height > Inpaint.MaxArea) { Problem?.Invoke("Select a smaller area to patch."); return false; }
        return BeginPreview("Patch", coverCanvas: true);
    }
    public void PreviewPatch(SKPoint offset, bool final = false)
    {
        if (previewLayer == null || previewSelection == null) return;
        if (!TargetMatrix(previewLayer).TryInvert(out var inverse)) return;
        var delta = inverse.MapVector(offset);
        var translation = new SKPointI((int)Math.Round(delta.X), (int)Math.Round(delta.Y));
        Preview(source => (PatchBlend.Blend(source, source, previewSelection, translation, final ? 64 : 12), 0, 0), mix: false);
    }
}
