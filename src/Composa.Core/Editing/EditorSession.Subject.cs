using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    /// <summary>How Select Subject and the Object Selection tool find the subject. A tool setting, carried from tab to tab.</summary>
    public SubjectDetect Detect { get; set; } = SubjectDetect.Any;

    // The matte for a picture and a choice is kept, because committed bitmaps never change: a Select Subject after a
    // click with Object Selection, or a second click, then costs nothing. Four entries cover a layer and the whole
    // picture under both models.
    private readonly List<(object Key, SubjectDetect Detect, SKBitmap Matte)> mattes = [];

    private SKBitmap? CachedMatte(object key, SubjectDetect detect)
    {
        var at = mattes.FindIndex(m => m.Key.Equals(key) && m.Detect == detect);
        if (at < 0) return null;
        var hit = mattes[at];
        mattes.RemoveAt(at);
        mattes.Add(hit);
        return hit.Matte;
    }

    private void RememberMatte(object key, SubjectDetect detect, SKBitmap matte)
    {
        mattes.Add((key, detect, matte));
        while (mattes.Count > 4) { mattes[0].Matte.Dispose(); mattes.RemoveAt(0); }
    }

    /// <summary>
    /// The subject's matte at document size, run off the UI thread; null when nothing stands out. Over the whole
    /// picture for Select > Subject, otherwise over what the selection tools read (the whole image, or the active
    /// layer alone). Call from the UI thread. The bitmap belongs to the session's cache: copy it, never dispose it.
    /// </summary>
    public async Task<SKBitmap?> FindSubjectAsync(bool wholePicture = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var detect = SubjectFinder.Resolve(Detect);
        var (source, owned) = wholePicture ? (Composite(), false) : SelectionSample();
        // A layer's committed pixels never change, so they and their placement name their rendering; the whole
        // picture is named by the history state it shows.
        object key = owned && ActiveLayer is { Pixels: { } pixels } layer ? (pixels, layer.Transform, document.Width, document.Height) : ("composite", History.CurrentId);
        if (CachedMatte(key, detect) is { } cached) { if (owned) source.Dispose(); return cached; }
        // The composite is redrawn in place by later renders, so the model reads a copy of its own.
        var copy = owned ? source : Pixels.Clone(source);
        SKBitmap? matte;
        try { matte = await Task.Run(() => SubjectFinder.Matte(copy, detect, cancellation), cancellation); }
        finally { copy.Dispose(); }
        if (matte != null) RememberMatte(key, detect, matte);
        return matte;
    }

    /// <summary>
    /// Select > Subject with the Detect choice, the model running off the UI thread. False when nothing stands out,
    /// or when the document changed while the model ran, in which case the result is thrown away.
    /// </summary>
    public async Task<bool> SelectSubjectAsync(SelectionMode mode = SelectionMode.Replace, CancellationToken cancellation = default)
    {
        var state = History.CurrentId;
        var matte = await FindSubjectAsync(wholePicture: true, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (matte == null || History.CurrentId != state || IsInteracting) return false;
        Select(Pixels.Clone(matte), mode, "Select Subject");
        return true;
    }

    /// <summary>
    /// The Object Selection tool with the Detect choice: the connected piece of the matte under the point, with the
    /// matte's own soft edge. Landing on the backdrop deselects in Replace mode, as the plain method does.
    /// </summary>
    public async Task SelectObjectAsync(int x, int y, SelectionMode mode = SelectionMode.Replace, CancellationToken cancellation = default)
    {
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return;
        var state = History.CurrentId;
        var layerId = document.ActiveLayerId;
        var matte = await FindSubjectAsync(wholePicture: false, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (History.CurrentId != state || document.ActiveLayerId != layerId || IsInteracting) return;
        var shape = matte == null ? null : ObjectSelection.FromMatte(matte, x, y, ObjectEdgeOffset);
        if (shape == null) { if (mode == SelectionMode.Replace) Deselect(); return; }
        Select(shape, mode, "Object Selection");
    }

    /// <summary>
    /// The Object Selection tool with a dragged box: the model runs on the box alone, so a small object fills its
    /// input instead of being a few of its pixels, and everything it finds inside the box is selected with the
    /// matte's soft edge. Nothing found deselects in Replace mode, as a click on the backdrop does. The box is clamped
    /// to the canvas; one smaller than two pixels a side does nothing.
    /// </summary>
    public async Task SelectObjectInBoxAsync(SKRectI box, SelectionMode mode = SelectionMode.Replace, CancellationToken cancellation = default)
    {
        box = SKRectI.Intersect(box, document.Bounds);
        if (box.Width < 2 || box.Height < 2) return;
        var detect = SubjectFinder.Resolve(Detect);
        var state = History.CurrentId;
        var layerId = document.ActiveLayerId;
        var (source, owned) = SelectionSample();
        var crop = Pixels.NewColor(box.Width, box.Height);
        using (var canvas = new SKCanvas(crop))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            canvas.DrawBitmap(source, -box.Left, -box.Top, paint);
        if (owned) source.Dispose();
        SKBitmap? matte;
        try { matte = await Task.Run(() => SubjectFinder.Matte(crop, detect, cancellation), cancellation); }
        finally { crop.Dispose(); }
        if (cancellation.IsCancellationRequested) { matte?.Dispose(); cancellation.ThrowIfCancellationRequested(); }
        if (History.CurrentId != state || document.ActiveLayerId != layerId || IsInteracting) { matte?.Dispose(); return; }
        if (matte == null) { if (mode == SelectionMode.Replace) Deselect(); return; }
        using (matte)
        {
            var shape = Pixels.NewMask(document.Width, document.Height);
            Place(matte, shape, box.Left, box.Top);
            Select(shape, mode, "Object Selection");
        }
    }

    private static unsafe void Place(SKBitmap piece, SKBitmap into, int left, int top)
    {
        byte* src = (byte*)piece.GetPixels(), dst = (byte*)into.GetPixels();
        for (var y = 0; y < piece.Height; y++)
            Buffer.MemoryCopy(src + (long)y * piece.RowBytes, dst + (long)(top + y) * into.RowBytes + left, piece.Width, piece.Width);
    }

    // Only subject selection is ported in this fork. The unrelated upstream Remove Background
    // preview APIs require filter changes and were never part of this fork's committed source.
}
