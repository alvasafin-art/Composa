using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private (object Key, PromptModelKind Model, ImageEmbedding Image)? promptEmbedding;
    public async Task SelectPromptObjectAsync(PromptModel model, SKPointI? point, SKRectI? box, SelectionMode mode, CancellationToken cancellation = default)
    {
        if (point is { } p && !document.Bounds.Contains(p.X, p.Y)) return;
        if (point == null && box == null) return;
        var state = History.CurrentId; var layerId = document.ActiveLayerId;
        object key = SampleAllLayers || ActiveLayer == null ? state : (object)(ActiveLayer.Pixels!, ActiveLayer.Transform, ActiveLayer.VectorMask!, ActiveLayer.VectorMaskEnabled, ActiveLayer.FillOpacity);
        var (source, owned) = SelectionSample();
        using var copy = owned ? source : Pixels.Clone(source);
        var cached = promptEmbedding is { } previous && previous.Key.Equals(key) && previous.Model == model.Kind ? previous.Image : null;
        var found = await Task.Run(() =>
        {
            var embedding = cached ?? PromptSegmentation.Encode(copy, model, cancellation);
            var mask = PromptSegmentation.Decode(embedding, model, copy.Width, copy.Height, point is { } pt ? new SKPoint(pt.X, pt.Y) : null, box, cancellation);
            return (embedding, mask);
        }, cancellation);
        using var result = found.mask;
        cancellation.ThrowIfCancellationRequested();
        if (History.CurrentId != state || document.ActiveLayerId != layerId || IsInteracting) return;
        promptEmbedding = (key, model.Kind, found.embedding);
        if (ObjectEdgeOffset > 0)
        {
            using var contracted = SelectionMask.Contract(result, ObjectEdgeOffset);
            if (contracted != null) Select(contracted, mode, "Object Selection");
        }
        else if (ObjectEdgeOffset < 0) { using var expanded = SelectionMask.Expand(result, -ObjectEdgeOffset); Select(expanded, mode, "Object Selection"); }
        else Select(result, mode, "Object Selection");
    }
}
