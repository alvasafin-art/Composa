using Composa.Rendering;
using SkiaSharp;

namespace Composa.Model;

/// <summary>An immutable embedded document revision. Instances share it; edits replace it, never mutate it.</summary>
public sealed class SmartObjectSource
{
    public const int MaxDepth = 16;
    private readonly Document content;
    public Guid Id { get; }
    public SKBitmap Preview { get; }
    public int Width => content.Width;
    public int Height => content.Height;
    public Document OpenDocument() => content.Clone();

    private SmartObjectSource(Guid id, Document content, SKBitmap preview)
    { Id = id; this.content = content; Preview = preview; }

    public static SmartObjectSource Create(Document document, Guid? id = null)
    {
        if (!DocumentLimits.FitsSurface(document.Width, document.Height)) throw new ArgumentException("Invalid smart object size.");
        var identity = id ?? Guid.NewGuid();
        Validate(document, identity, 0);
        if (document.RasterPixels() + (long)document.Width * document.Height > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException($"The smart object exceeds the {DocumentLimits.DocumentBudgetMegapixels} MP document budget.");
        var snapshot = document.Clone(); snapshot.Selection = null;
        var preview = DocumentRenderer.Flatten(snapshot);
        return new(identity, snapshot, preview);
    }

    private static void Validate(Document document, Guid identity, int depth)
    {
        if (depth >= MaxDepth) throw new InvalidOperationException($"Smart objects may be nested at most {MaxDepth} levels.");
        foreach (var source in document.AllLayers().Select(layer => layer.SmartObject).OfType<SmartObjectSource>().Distinct())
        {
            if (source.Id == identity) throw new InvalidOperationException("A smart object cannot contain itself.");
            Validate(source.content, identity, depth + 1);
        }
    }

    internal void CollectBitmaps(HashSet<SKBitmap> into, HashSet<SmartObjectSource> visited)
    {
        if (!visited.Add(this)) return;
        into.Add(Preview); content.CollectBitmaps(into, visited, includeSelection: false);
    }
}
