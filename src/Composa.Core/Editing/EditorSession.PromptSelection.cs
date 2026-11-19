using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private (PromptModelKind Model, ImageEmbedding Image)? promptEmbedding;
    public async Task SelectPromptObjectAsync(PromptModel model, SKPointI? point, SKRectI? box, SelectionMode mode, CancellationToken cancellation = default,
        bool preciseEdges = false, IProgress<string>? progress = null, Func<bool>? canApply = null)
    {
        if (point is { } p && !document.Bounds.Contains(p.X, p.Y)) return;
        if (point == null && box == null) return;
        var bounds = document.Bounds;
        if (box is { } requested)
        {
            box = SKRectI.Intersect(bounds, requested);
            if (box.Value.IsEmpty) return;
        }
        cancellation.ThrowIfCancellationRequested();
        if (preciseEdges && !TrimapMatting.IsAvailable)
            throw new InvalidOperationException("Precise edges requires the bundled AI Matting model. Reinstall the complete Composa package.");
        var state = History.CurrentId; var revision = Revision; var layerId = document.ActiveLayerId;
        var sampleAll = SampleAllLayers; var edgeOffset = ObjectEdgeOffset;
        // Matting reads the immutable original RGB, before the active layer's old masks/styles.
        var (source, owned) = preciseEdges ? (MaskRefinement.Source(document.Clone(), sampleAll), true) : SelectionSample();
        using var copy = owned ? source : Pixels.Clone(source);
        var previous = promptEmbedding is { } cached && cached.Model == model.Kind ? cached.Image : null;
        var found = await Task.Run(() =>
        {
            progress?.Report("Finding object with " + model.Name + "…");
            var area = PromptSegmentation.SearchArea(bounds, box);
            while (true)
            {
                using var subset = area == bounds ? null : PromptSegmentation.Subset(copy, area);
                var guide = subset ?? copy;
                using var display = !Pixels.IsSrgb8(guide) ? Pixels.DisplayCopy(guide) : null;
                var image = display ?? guide;
                var embedding = PromptSegmentation.Encode(image, model, previous, cancellation);
                var localBox = box;
                if (localBox is { } rect) { rect.Offset(-area.Left, -area.Top); localBox = rect; }
                using var decoded = PromptSegmentation.Decode(embedding, model, image.Width, image.Height,
                    point is { } pt ? new SKPoint(pt.X - area.Left, pt.Y - area.Top) : null, localBox, cancellation);
                if (area != bounds && PromptSegmentation.TouchesCrop(SelectionMask.Bounds(decoded), area, bounds))
                {
                    // A loose or partial box may describe an object extending beyond our context.
                    area = bounds;
                    continue;
                }
                using var piece = point is { } seed ? ObjectSelection.FromMatte(decoded, seed.X - area.Left, seed.Y - area.Top, 0) : null;
                if (point.HasValue && piece == null) return (embedding, mask: (SKBitmap?)null);
                using var outline = PromptSegmentation.Refine(piece ?? decoded, image, cancellation);
                if (preciseEdges) progress?.Report("Refining object edges…");
                using var matte = preciseEdges ? PromptSegmentation.RefineEdges(image, outline, cancellation) : null;
                return (embedding, mask: PromptSegmentation.Place(matte ?? outline, area, bounds));
            }
        }, cancellation);
        using var result = found.mask;
        cancellation.ThrowIfCancellationRequested();
        if (History.CurrentId != state || Revision != revision || document.ActiveLayerId != layerId || SampleAllLayers != sampleAll || IsInteracting || canApply?.Invoke() == false) return;
        promptEmbedding = (model.Kind, found.embedding);
        if (result == null) return;
        if (edgeOffset > 0)
        {
            using var contracted = SelectionMask.Contract(result, edgeOffset);
            if (contracted != null) Select(Pixels.Clone(contracted), mode, "Object Selection");
        }
        else if (edgeOffset < 0) { using var expanded = SelectionMask.Expand(result, -edgeOffset); Select(Pixels.Clone(expanded), mode, "Object Selection"); }
        // Replace adopts its mask. The inference temporaries below are disposed on return,
        // so history and the live document must receive a separate, committed bitmap.
        else Select(Pixels.Clone(result), mode, "Object Selection");
    }
}
