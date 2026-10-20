using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    /// <summary>Adopts both owned full-canvas planes on success, in one undo step. The source stays immutable.</summary>
    public Layer AddRefinedCutout(SKBitmap colors, SKBitmap mask, Guid? hideSource = null)
    {
        if (colors.Width != document.Width || colors.Height != document.Height || mask.Width != document.Width || mask.Height != document.Height
            || mask.ColorType != SKColorType.Alpha8 || colors.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32)
            || !Pixels.ProfileOf(colors).Equals(document.Profile)) throw new ArgumentException("The refined cutout must use the document canvas and RGB profile.");
        if (document.RasterPixels() + ((long)colors.ByteCount + mask.ByteCount + 3) / 4 > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException("The refined cutout exceeds the document raster memory budget.");
        var layer = Layer.Raster(document.UniqueName("Refined cutout"), colors); layer.Mask = mask;
        var hidden = hideSource is { } id ? document.Find(id) : null;
        Apply("Select and Mask", () =>
        {
            if (hidden != null)
            {
                var siblings = document.SiblingsOf(hidden.Id)!; siblings.Insert(siblings.IndexOf(hidden) + 1, layer);
                hidden.Visible = false; document.SetActive(layer.Id);
            }
            else document.InsertAboveActive(layer);
        });
        InvalidateAll(); LayersChanged?.Invoke(); return layer;
    }
}
