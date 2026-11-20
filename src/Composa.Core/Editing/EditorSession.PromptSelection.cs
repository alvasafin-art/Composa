using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private (PromptModelKind Model, ImageEmbedding Image)? promptEmbedding;
    // Selection/history snapshots share immutable coverage planes. A weak key carries
    // cutout intent through undo/redo without retaining old document-sized masks.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, object> foregroundSelections = new();
    public bool HasForegroundSelection => Selection is { } mask && foregroundSelections.TryGetValue(mask, out _);

    private void SelectForeground(SKBitmap mask, SelectionMode mode, string name)
    {
        var combined = SelectionMask.Combine(Selection, mask, mode);
        if (combined != null) foregroundSelections.GetOrCreateValue(combined);
        if (combined != mask && Selection != mask) mask.Dispose();
        SetSelection(name, combined);
    }
    public async Task<bool?> SelectPromptObjectAsync(PromptModel model, SKPointI? point, SKRectI? box, SelectionMode mode, CancellationToken cancellation = default,
        bool preciseEdges = false, IProgress<string>? progress = null, Func<bool>? canApply = null)
    {
        if (point is { } p && !document.Bounds.Contains(p.X, p.Y)) return null;
        if (point == null && box == null) return null;
        var bounds = document.Bounds;
        if (box is { } requested)
        {
            box = SKRectI.Intersect(bounds, requested);
            if (box.Value.IsEmpty) return null;
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
            var sample = new PixelBuffer(copy);
            bool VisibleIn(SKRectI region)
            {
                for (int y = region.Top; y < region.Bottom; y++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (int x = region.Left; x < region.Right; x++) if (sample.Read(x, y).A > 0) return true;
                }
                return false;
            }
            if (!VisibleIn(box ?? SKRectI.Create(point!.Value.X, point.Value.Y, 1, 1)))
                return (embedding: previous, mask: (SKBitmap?)null);
            progress?.Report("Finding object with " + model.Name + "…");
            var area = PromptSegmentation.SearchArea(bounds, box);
            while (true)
            {
                using var subset = area == bounds ? null : Pixels.Subset(copy, area);
                var guide = subset ?? copy;
                using var display = !Pixels.IsSrgb8(guide) ? Pixels.DisplayCopy(guide) : null;
                var image = display ?? guide;
                var localBox = box;
                if (localBox is { } rect) { rect.Offset(-area.Left, -area.Top); localBox = SKRectI.Intersect(rect, SKRectI.Create(image.Width, image.Height)); }
                bool checkShape = false;
                using var graphics = point == null && box != bounds && localBox is { } marks
                    ? GraphicSelection.FromBox(image, marks, cancellation) ?? RepeatedDetailSelection.FromBox(image, marks, cancellation, out checkShape) : null;
                if (graphics != null && !checkShape) return (embedding: previous, mask: (SKBitmap?)PromptSegmentation.Place(graphics, area, bounds));
                var embedding = PromptSegmentation.Encode(image, model, previous, cancellation);
                using var decoded = PromptSegmentation.Decode(embedding, model, image.Width, image.Height,
                    point is { } pt ? new SKPoint(pt.X - area.Left, pt.Y - area.Top) : null, localBox, cancellation);
                if (graphics != null && localBox is { } search && PromptSegmentation.IsBoxSilhouette(decoded, search))
                    return (embedding, mask: PromptSegmentation.Place(graphics, area, bounds));
                if (area != bounds && PromptSegmentation.TouchesCrop(SelectionMask.Bounds(decoded), area, bounds))
                {
                    // A loose or partial box may describe an object extending beyond our context.
                    area = bounds;
                    continue;
                }
                using var piece = point is { } seed ? ObjectSelection.FromMatte(decoded, seed.X - area.Left, seed.Y - area.Top, 0) : null;
                if (point is { } missed && piece == null)
                {
                    using var detail = RepeatedDetailSelection.AtPoint(image, new(missed.X - area.Left, missed.Y - area.Top), cancellation);
                    return (embedding, mask: detail == null ? null : PromptSegmentation.Place(detail, area, bounds));
                }
                // Keep the whole detected panel as a search region. Asking SAM again at
                // a higher zoom can change object identity (for example, just its hair).
                if (preciseEdges && point == null && box != bounds && localBox is { } group)
                    GroupedObjectSelection.RefineHoles(decoded, group, embedding, model, cancellation);
                var panel = preciseEdges && localBox is { } gesture ? PromptSegmentation.PromptedPanel(image, decoded, gesture) : null;
                using var panelImage = panel.HasValue ? Pixels.Subset(image, panel.Value) : null;
                using var panelMask = panel.HasValue ? Pixels.Subset(decoded, panel.Value) : null;
                if (panel is { } frame)
                {
                    var prompt = localBox!.Value; prompt.Offset(-frame.Left, -frame.Top); localBox = prompt;
                    frame.Offset(area.Left, area.Top); area = frame;
                }
                var guideRgb = panelImage ?? image;
                using var outline = PromptSegmentation.Refine(piece ?? panelMask ?? decoded, guideRgb, cancellation);
                if (preciseEdges) progress?.Report("Refining object edges…");
                using var matte = preciseEdges ? PromptSegmentation.RefineEdges(guideRgb, outline, localBox, cancellation) : null;
                var final = matte != null && !SelectionMask.Bounds(matte, 128).IsEmpty ? matte : outline;
                if (SelectionMask.Bounds(final, 128).IsEmpty) final = decoded;
                if (point is { } hit && piece != null && final.GetPixel(hit.X - area.Left, hit.Y - area.Top).Alpha < 128)
                {
                    var local = new SKPointI(hit.X - area.Left, hit.Y - area.Top);
                    using var detail = RepeatedDetailSelection.AtPoint(image, local, cancellation);
                    return (embedding, mask: PromptSegmentation.Place(detail ?? piece, area, bounds));
                }
                using var printed = preciseEdges && point == null && localBox is { } printBox && box != bounds
                    ? GraphicSelection.RefinePrintedEdges(guideRgb, final, printBox, cancellation) : null;
                return (embedding, mask: PromptSegmentation.Place(printed ?? final, area, bounds));
            }
        }, cancellation);
        using var result = found.mask;
        cancellation.ThrowIfCancellationRequested();
        if (History.CurrentId != state || Revision != revision || document.ActiveLayerId != layerId || SampleAllLayers != sampleAll || IsInteracting || canApply?.Invoke() == false) return null;
        if (found.embedding != null) promptEmbedding = (model.Kind, found.embedding);
        if (result == null || SelectionMask.IsEmpty(result)) return false;
        void CommitMask(SKBitmap mask)
        {
            if (preciseEdges) SelectForeground(Pixels.Clone(mask), mode, "Object Selection");
            else Select(Pixels.Clone(mask), mode, "Object Selection");
        }
        if (edgeOffset > 0)
        {
            using var contracted = SelectionMask.Contract(result, edgeOffset);
            if (contracted == null || SelectionMask.IsEmpty(contracted)) return false;
            CommitMask(contracted);
        }
        else if (edgeOffset < 0) { using var expanded = SelectionMask.Expand(result, -edgeOffset); CommitMask(expanded); }
        // Replace adopts its mask. The inference temporaries below are disposed on return,
        // so history and the live document must receive a separate, committed bitmap.
        else CommitMask(result);
        return true;
    }
}
