using Composa.IO.Psd;
using Composa.Model;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public const int MaxLayers = 10_000;

    /// <summary>A Photoshop file opened on its own: the file's canvas becomes the document.</summary>
    public static EditorSession OpenPhotoshop(PsdImport import, string name) => new(import.ToDocument()) { SuggestedName = name };

    /// <summary>
    /// A Photoshop file dropped into an existing document: its layers arrive inside one folder named after the file,
    /// centered on <paramref name="center"/> when given, as one undoable step.
    /// </summary>
    public Layer PlacePhotoshop(PsdImport import, string name, SKPoint? center = null)
    {
        var incoming = import.Layers;
        if (!import.Profile.Equals(document.Profile))
        {
            var contents = new EditorSession(import.ToDocument());
            contents.ConvertColorProfile(document.Profile, flatten: false); incoming = contents.Document.Layers;
            if (document.RasterPixels() + contents.Document.RasterPixels() > DocumentLimits.DocumentPixelBudget) throw PsdException.TooLarge();
        }
        if (document.AllLayers().Count() + Document.Flatten(incoming).Count() + 1 > MaxLayers) throw PsdException.TooLarge();
        var group = Layer.Group(name);
        Apply("Import Photoshop File", () =>
        {
            if (center is { } at)
            {
                var box = SKRect.Empty;
                foreach (var leaf in Document.Flatten(incoming).Where(l => l.Pixels != null))
                    box = box.IsEmpty ? leaf.Bounds : SKRect.Union(box, leaf.Bounds);
                if (!box.IsEmpty)
                {
                    double dx = Math.Round(at.X - box.MidX), dy = Math.Round(at.Y - box.MidY);
                    foreach (var leaf in Document.Flatten(incoming)) leaf.TranslateContent(dx, dy);
                }
            }
            group.Children.AddRange(incoming);
            document.InsertAboveActive(group);
        });
        InvalidateAll();
        LayersChanged?.Invoke();
        return group;
    }
}
