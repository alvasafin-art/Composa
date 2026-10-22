using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private (object Key, PromptModelKind Model, ImageEmbedding Image)? promptEmbedding;
    public async Task SelectPromptObjectAsync(PromptModel model, SKPointI? point, SKRectI? box, SelectionMode mode, CancellationToken cancellation = default,
        bool preciseEdges = false, IProgress<string>? progress = null, Func<bool>? canApply = null)
    {
        if (point is { } p && !document.Bounds.Contains(p.X, p.Y)) return;
        if (point == null && box == null) return;
        cancellation.ThrowIfCancellationRequested();
        if (preciseEdges && !TrimapMatting.IsAvailable)
            throw new InvalidOperationException("Precise edges requires the bundled AI Matting model. Reinstall the complete Composa package.");
        var state = History.CurrentId; var revision = Revision; var layerId = document.ActiveLayerId;
        var sampleAll = SampleAllLayers; var edgeOffset = ObjectEdgeOffset;
        object key = SampleAllLayers || ActiveLayer == null ? state : (object)(document.Width, document.Height,
            ActiveLayer.Pixels!, ActiveLayer.Transform, ActiveLayer.VectorMask!, ActiveLayer.VectorMaskEnabled, ActiveLayer.FillOpacity);
        key = (key, preciseEdges);
        // Matting reads the immutable original RGB, before the active layer's old masks/styles.
        var (source, owned) = preciseEdges ? (MaskRefinement.Source(document.Clone(), sampleAll), true) : SelectionSample();
        using var copy = owned ? source : Pixels.Clone(source);
        var cached = promptEmbedding is { } previous && previous.Key.Equals(key) && previous.Model == model.Kind ? previous.Image : null;
        var found = await Task.Run(() =>
        {
            progress?.Report("Finding object with " + model.Name + "…");
            using var modelImage = preciseEdges && !Pixels.IsSrgb8(copy) ? Pixels.DisplayCopy(copy) : null;
            var embedding = cached ?? PromptSegmentation.Encode(modelImage ?? copy, model, cancellation);
            using var decoded = PromptSegmentation.Decode(embedding, model, copy.Width, copy.Height, point is { } pt ? new SKPoint(pt.X, pt.Y) : null, box, cancellation);
            using var piece = point is { } seed ? ObjectSelection.FromMatte(decoded, seed.X, seed.Y, 0) : null;
            var mask = PromptSegmentation.Refine(piece ?? decoded, copy, cancellation);
            if (!preciseEdges) return (embedding, mask);
            using (mask)
            {
                progress?.Report("Refining object edges…");
                return (embedding, mask: TrimapMatting.Refine(copy, mask, cancellation: cancellation, fineDetail: true));
            }
        }, cancellation);
        using var result = found.mask;
        cancellation.ThrowIfCancellationRequested();
        if (History.CurrentId != state || Revision != revision || document.ActiveLayerId != layerId || SampleAllLayers != sampleAll || IsInteracting || canApply?.Invoke() == false) return;
        promptEmbedding = (key, model.Kind, found.embedding);
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
