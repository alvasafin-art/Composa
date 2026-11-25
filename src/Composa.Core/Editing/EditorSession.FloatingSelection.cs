using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private Layer? floatLayer;
    private SKBitmap? floatBase, floatPixels, floatSelection, floatPreview, floatOriginal;
    private SKPointI floatOffset;
    private SKPointI floatOrigin;
    private SKRectI floatPreviewArea;

    public bool IsMovingPixels => floatLayer != null;

    /// <summary>True when dragging inside the selection should move the selected pixels rather than the whole layer.</summary>
    public bool CanMovePixels =>
        document.Selection != null && !IsEditingMask && !IsEditingComponent && ActiveLayer is { Pixels: { } pixels, IsLive: false } layer
        && layer.Transform.IsPureTranslation(pixels.Width, pixels.Height) && document.IsEffectivelyVisible(layer);

    /// <summary>Lifts the selected pixels off the active layer so they can be dragged; with <paramref name="duplicate"/> the originals stay.</summary>
    public bool BeginMovePixels(bool duplicate)
    {
        if (!CanMovePixels || ActiveLayer is not { } layer) return false;
        Begin(duplicate ? "Duplicate Selection" : "Move Selection Pixels");
        EnsureCoversCanvas(layer);
        var original = layer.Pixels!;
        using var selection = SelectionInLayerSpace(layer)!;
        var bounds = SelectionMask.Bounds(selection);
        if (bounds.IsEmpty) { Cancel(); return false; }

        floatPixels = Pixels.NewLike(original, bounds.Width, bounds.Height);
        using (var canvas = new SKCanvas(floatPixels))
        {
            canvas.DrawBitmap(original, -bounds.Left, -bounds.Top);
            using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
            canvas.DrawBitmap(selection, -bounds.Left, -bounds.Top, keep);
        }
        floatBase = Pixels.Clone(original);
        if (!duplicate)
        {
            using var canvas = new SKCanvas(floatBase);
            using var remove = new SKPaint { BlendMode = SKBlendMode.DstOut };
            canvas.DrawBitmap(selection, 0, 0, remove);
        }
        floatOrigin = new SKPointI(bounds.Left, bounds.Top);
        floatSelection = document.Selection;
        floatOriginal = original;
        floatOffset = SKPointI.Empty;
        floatPreviewArea = SKRectI.Empty;
        floatLayer = layer;
        return true;
    }

    public void MovePixelsBy(int dx, int dy)
    {
        if (floatLayer is not { } layer || floatBase == null || floatPixels == null) return;
        var next = new SKPointI(dx, dy);
        if (next == floatOffset) return;
        var previous = floatOffset;
        var damage = previous == SKPointI.Empty ? SKRectI.Empty : floatPreviewArea;
        floatOffset = next;
        // Back at the start nothing has moved: with a feathered selection, re-compositing the lifted pixels over their
        // own hole would otherwise leave a faint translucent seam.
        SKBitmap? moved = null;
        if (dx != 0 || dy != 0)
        {
            if (floatPreview == null) floatPreview = Pixels.Clone(floatBase);
            else if (!floatPreviewArea.IsEmpty)
                DocumentRenderer.CopyRows(floatBase, new SKPointI(floatPreviewArea.Left, floatPreviewArea.Top), floatPreview, floatPreviewArea);

            moved = floatPreview;
            var x = floatOrigin.X + dx;
            var y = floatOrigin.Y + dy;
            using (var canvas = new SKCanvas(moved)) canvas.DrawBitmap(floatPixels, x, y);
            floatPreviewArea = SKRectI.Intersect(SKRectI.Create(x, y, floatPixels.Width, floatPixels.Height),
                SKRectI.Create(moved.Width, moved.Height));
            damage = Geometry.Union(damage, floatPreviewArea);
            Pixels.Invalidate(moved, damage);
        }
        layer.Pixels = moved ?? floatOriginal!;
        document.Selection = SelectionMask.Translate(floatSelection!, dx, dy) ?? floatSelection;
        if (layer.Effects != null || layer.HasWarp || layer.LayoutFrame != null || layer.FilterSource != null)
            Invalidate(AffectedArea(layer));
        else
        {
            // The removal at the source only changes when entering or returning from a displaced position.
            if (previous == SKPointI.Empty || next == SKPointI.Empty)
                damage = Geometry.Union(damage, SKRectI.Create(floatOrigin.X, floatOrigin.Y, floatPixels.Width, floatPixels.Height));
            if (!damage.IsEmpty)
            {
                damage = Geometry.RoundOut(layer.Matrix.MapRect(damage));
                var margin = DamageMargin;
                damage.Inflate(margin, margin);
                Invalidate(damage);
            }
        }
        SelectionChanged?.Invoke();
    }

    public void EndMovePixels(bool keep)
    {
        if (floatLayer == null) return;
        if (floatOffset == SKPointI.Empty) keep = false;
        floatLayer = null;
        floatPixels?.Dispose();
        floatBase?.Dispose();
        if (!keep && floatPreview != null) { Pixels.Invalidate(floatPreview); floatPreview.Dispose(); }
        floatBase = floatPixels = floatSelection = floatPreview = floatOriginal = null;
        if (keep) { Commit(); LayersChanged?.Invoke(); SelectionChanged?.Invoke(); }
        else Cancel();
    }
}
