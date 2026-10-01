using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public Layer ConvertToSmartObject()
    {
        FinishText();
        var roots = SelectedRoots();
        if (roots.Count == 0) throw new InvalidOperationException("Select layers to convert to a smart object.");
        if (roots.Count == 1 && roots[0].IsSmartObject) return roots[0];
        var siblings = document.SiblingsOf(roots[0].Id)!;
        if (roots.Any(layer => document.SiblingsOf(layer.Id) != siblings))
            throw new InvalidOperationException("Group layers in the same folder before converting them to a smart object.");
        Document content;
        Layer instance;
        if (roots.Count == 1 && roots[0].Pixels is { } pixels)
        {
            var original = roots[0];
            content = new Document(pixels.Width, pixels.Height) { Resolution = document.Resolution };
            var inner = original.Clone(newIds: true);
            inner.Transform = LayerTransform.Identity(pixels.Width, pixels.Height);
            inner.Mask = null; inner.Effects = null; inner.Visible = true; inner.Opacity = 1; inner.Blend = BlendMode.Normal; inner.Clipped = false;
            content.Layers.Add(inner); content.SetActive(inner.Id);
            instance = original.Clone(); instance.Text = null; instance.Shape = null;
        }
        else
        {
            // Preserve document-space adjustment/group masks and the complete layer structure.
            content = new Document(document.Width, document.Height) { Resolution = document.Resolution };
            content.Layers.AddRange(roots.Select(layer => layer.Clone(newIds: true)));
            content.SetActive(content.Layers[^1].Id);
            instance = new Layer { Kind = LayerKind.Raster, Name = roots.Count == 1 ? roots[0].Name : "Smart Object",
                Transform = LayerTransform.Identity(document.Width, document.Height) };
        }
        var source = SmartObjectSource.Create(content);
        instance.SmartObject = source; instance.Pixels = source.Preview;
        var candidate = document.Clone();
        var candidateSiblings = candidate.SiblingsOf(roots[0].Id)!;
        candidateSiblings.RemoveAll(layer => roots.Any(root => root.Id == layer.Id));
        candidateSiblings.Add(instance);
        try { EnsureSmartBudget(candidate); }
        catch { source.Preview.Dispose(); throw; }
        Apply("Convert to Smart Object", () =>
        {
            var index = siblings.IndexOf(roots[^1]);
            siblings.Insert(index + 1, instance);
            foreach (var root in roots) siblings.Remove(root);
            document.SetActive(instance.Id);
        });
        EditingMask = false; InvalidateAll(); LayersChanged?.Invoke(); return instance;
    }

    public void UpdateSmartObject(SmartObjectSource expected, Document content)
    {
        FinishText();
        var instances = document.AllLayers().Where(layer => layer.SmartObject?.Id == expected.Id).ToList();
        if (instances.Count == 0) throw new InvalidOperationException("This smart object no longer exists in the parent document.");
        if (instances.Any(layer => !ReferenceEquals(layer.SmartObject, expected)))
            throw new InvalidOperationException("The parent smart object changed (for example through Undo). Reopen its contents before saving; your open contents remain available.");
        var replacement = SmartObjectSource.Create(content, expected.Id);
        var changes = instances.Select(layer => (Layer: layer, Mask: ResizeSmartMask(layer.Mask, expected, replacement))).ToList();
        var candidate = document.Clone();
        foreach (var (layer, mask) in changes)
        {
            var target = candidate.Find(layer.Id)!; target.SmartObject = replacement; target.Pixels = replacement.Preview; target.Mask = mask;
        }
        try { EnsureSmartBudget(candidate); }
        catch
        {
            replacement.Preview.Dispose();
            foreach (var (layer, mask) in changes) if (mask != null && !ReferenceEquals(mask, layer.Mask)) mask.Dispose();
            throw;
        }
        Apply("Update Smart Object", () =>
        {
            foreach (var (layer, mask) in changes)
            { layer.SmartObject = replacement; layer.Pixels = replacement.Preview; layer.Mask = mask; }
        });
        InvalidateAll(); LayersChanged?.Invoke();
    }

    public Layer DuplicateSmartObjectIndependent(Layer layer)
    {
        if (document.Find(layer.Id) != layer || layer.SmartObject is not { } source) throw new ArgumentException("Select a smart object.");
        var independent = SmartObjectSource.Create(source.OpenDocument());
        var copy = layer.Clone(newIds: true); copy.Name += " independent copy"; copy.SmartObject = independent; copy.Pixels = independent.Preview;
        var candidate = document.Clone(); candidate.InsertAboveActive(copy);
        try { EnsureSmartBudget(candidate); }
        catch { independent.Preview.Dispose(); throw; }
        Apply("New Smart Object via Copy", () =>
        {
            var siblings = document.SiblingsOf(layer.Id)!; siblings.Insert(siblings.IndexOf(layer) + 1, copy); document.SetActive(copy.Id);
        });
        InvalidateAll(); LayersChanged?.Invoke(); return copy;
    }

    private static SKBitmap? ResizeSmartMask(SKBitmap? mask, SmartObjectSource before, SmartObjectSource after)
    {
        if (mask == null || before.Width == after.Width && before.Height == after.Height) return mask;
        // Masks are in the instance's source grid, while its placement stays unchanged.
        var resized = Pixels.NewMask(after.Width, after.Height);
        using var canvas = new SKCanvas(resized);
        canvas.DrawImage(Pixels.ImageOf(mask), new SKRect(0, 0, after.Width, after.Height), new SKSamplingOptions(SKFilterMode.Linear));
        return resized;
    }

    private static void EnsureSmartBudget(Document candidate)
    {
        if (candidate.RasterPixels() > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException($"The embedded content exceeds the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget.");
    }

    public void ReloadEmbeddedContents(Document content)
    {
        FinishInteraction();
        if (IsModified) throw new InvalidOperationException("Save or discard these contents before reopening the parent version.");
        History.Clear(); History.BaseName = "Smart Object Contents";
        Restore(content.Clone()); Revision++; MarkEmbeddedSaved();
    }
}
