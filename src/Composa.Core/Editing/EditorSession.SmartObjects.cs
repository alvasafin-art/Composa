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
        if (roots.Any(root => Document.Flatten([root]).Any(PixelsLocked))) throw new InvalidOperationException("Unlock the selected layers before converting them to a smart object.");
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
            inner.Mask = null; inner.VectorMask = null; inner.Effects = null; inner.Visible = true; inner.Opacity = 1; inner.FillOpacity = 1; inner.Blend = BlendMode.Normal; inner.Clipped = false;
            content.Layers.Add(inner); content.SetActive(inner.Id);
            instance = original.Clone(); instance.Text = null; instance.Shape = null;
            instance.FilterSource = null; instance.SmartFilters = []; instance.FilterPaddingX = instance.FilterPaddingY = 0;
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
        var candidate = document.Clone();
        try
        {
            foreach (var layer in instances)
            {
                var target = candidate.Find(layer.Id)!;
                var oldSource = layer.FilterSource ?? layer.Pixels!;
                if (layer.FilterSource != null)
                {
                    target.Transform = SmartFilterSourceTransform(layer);
                    if (layer.VectorMask is { } vector)
                        target.VectorMask = vector with { Nodes = vector.Nodes.Select(n => new BezierNode((n.X * layer.Pixels!.Width - layer.FilterPaddingX) / oldSource.Width, (n.Y * layer.Pixels.Height - layer.FilterPaddingY) / oldSource.Height,
                            (n.InX * layer.Pixels.Width - layer.FilterPaddingX) / oldSource.Width, (n.InY * layer.Pixels.Height - layer.FilterPaddingY) / oldSource.Height, (n.OutX * layer.Pixels.Width - layer.FilterPaddingX) / oldSource.Width, (n.OutY * layer.Pixels.Height - layer.FilterPaddingY) / oldSource.Height)).ToArray() };
                }
                if (layer.Mask is { } mask)
                {
                    var resized = Pixels.NewMask(replacement.Width, replacement.Height);
                    using var canvas = new SKCanvas(resized);
                    var sx = (float)replacement.Width / oldSource.Width; var sy = (float)replacement.Height / oldSource.Height;
                    canvas.Scale(sx, sy); canvas.DrawBitmap(mask, -layer.FilterPaddingX, -layer.FilterPaddingY); target.Mask = resized;
                }
                target.SmartObject = replacement; target.Pixels = replacement.Preview;
                if (layer.FilterSource != null) target.FilterSource = replacement.Preview;
                target.FilterPaddingX = target.FilterPaddingY = 0;
            }
            foreach (var layer in instances.Where(l => l.FilterSource != null)) RenderSmartFilters(candidate.Find(layer.Id)!, candidate);
            EnsureSmartBudget(candidate);
        }
        catch
        {
            replacement.Preview.Dispose();
            foreach (var layer in instances)
            {
                var target = candidate.Find(layer.Id)!;
                if (target.Mask != null && !ReferenceEquals(target.Mask, layer.Mask)) target.Mask.Dispose();
                if (target.Pixels != null && !ReferenceEquals(target.Pixels, layer.Pixels) && !ReferenceEquals(target.Pixels, replacement.Preview)) target.Pixels.Dispose();
            }
            throw;
        }
        Apply("Update Smart Object", () =>
        {
            foreach (var layer in instances)
            {
                var target = candidate.Find(layer.Id)!;
                layer.SmartObject = replacement; layer.Pixels = target.Pixels; layer.Mask = target.Mask; layer.VectorMask = target.VectorMask;
                layer.FilterSource = target.FilterSource; layer.FilterPaddingX = target.FilterPaddingX; layer.FilterPaddingY = target.FilterPaddingY; layer.Transform = target.Transform;
            }
        });
        InvalidateAll(); LayersChanged?.Invoke();
    }

    public Layer DuplicateSmartObjectIndependent(Layer layer)
    {
        if (document.Find(layer.Id) != layer || layer.SmartObject is not { } source) throw new ArgumentException("Select a smart object.");
        var independent = SmartObjectSource.Create(source.OpenDocument());
        var copy = layer.Clone(newIds: true); copy.Name += " independent copy"; copy.SmartObject = independent;
        if (copy.FilterSource != null) copy.FilterSource = independent.Preview; else copy.Pixels = independent.Preview;
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
