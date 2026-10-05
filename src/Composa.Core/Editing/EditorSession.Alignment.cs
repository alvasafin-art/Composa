using Composa.Model;
using SkiaSharp;

namespace Composa.Editing;

public enum ObjectAlignment { Left, Center, Right, Top, Middle, Bottom }
public enum AlignmentReference { Canvas, SecondSelected }

public sealed partial class EditorSession
{
    public int AlignableObjectCount => SelectedRoots().Count(l => !AlignmentBounds(l).IsEmpty);
    private static SKRect AlignmentBounds(Layer root)
    {
        var bounds = SKRect.Empty;
        foreach (var layer in Document.Flatten([root]).Where(l => l.Pixels != null))
            bounds = bounds.IsEmpty ? layer.ControlBounds : SKRect.Union(bounds, layer.ControlBounds);
        return bounds;
    }

    public void AlignObjects(ObjectAlignment alignment, AlignmentReference reference)
    {
        FinishText();
        var roots = SelectedRoots().Where(l => !AlignmentBounds(l).IsEmpty).ToList();
        if (roots.Count == 0 || reference == AlignmentReference.SecondSelected && roots.Count < 2) return;
        var orderedRoots = document.SelectionOrder.Select(id => roots.FirstOrDefault(l => Document.Flatten([l]).Any(c => c.Id == id)))
            .OfType<Layer>().Distinct().ToList();
        var anchor = orderedRoots.Count >= 2 ? orderedRoots[1] : roots.FirstOrDefault(l => l.Id == document.ActiveLayerId) ?? roots[0];
        var target = reference == AlignmentReference.Canvas ? new SKRect(0, 0, document.Width, document.Height) : AlignmentBounds(anchor);
        var moves = new List<(Layer, float, float)>();
        foreach (var root in roots)
        {
            if (reference == AlignmentReference.SecondSelected && root == anchor) continue;
            var b = AlignmentBounds(root);
            float dx = alignment switch { ObjectAlignment.Left => target.Left - b.Left, ObjectAlignment.Center => target.MidX - b.MidX, ObjectAlignment.Right => target.Right - b.Right, _ => 0 };
            float dy = alignment switch { ObjectAlignment.Top => target.Top - b.Top, ObjectAlignment.Middle => target.MidY - b.MidY, ObjectAlignment.Bottom => target.Bottom - b.Bottom, _ => 0 };
            moves.Add((root, dx, dy));
        }
        MoveObjects("Align Objects", moves);
    }

    public void DistributeObjectGaps(bool horizontal)
    {
        FinishText();
        var ordered = SelectedRoots().Select(l => (Layer: l, Bounds: AlignmentBounds(l))).Where(p => !p.Bounds.IsEmpty)
            .OrderBy(p => horizontal ? p.Bounds.Left : p.Bounds.Top).ToList();
        if (ordered.Count < 3) return;
        var span = horizontal ? ordered[^1].Bounds.Right - ordered[0].Bounds.Left : ordered[^1].Bounds.Bottom - ordered[0].Bounds.Top;
        var gap = (span - ordered.Sum(p => horizontal ? p.Bounds.Width : p.Bounds.Height)) / (ordered.Count - 1);
        var position = horizontal ? ordered[0].Bounds.Right : ordered[0].Bounds.Bottom;
        var moves = new List<(Layer, float, float)>();
        for (var i = 1; i < ordered.Count - 1; i++)
        {
            position += gap;
            var p = ordered[i];
            var delta = position - (horizontal ? p.Bounds.Left : p.Bounds.Top);
            moves.Add((p.Layer, horizontal ? delta : 0, horizontal ? 0 : delta));
            position += horizontal ? p.Bounds.Width : p.Bounds.Height;
        }
        MoveObjects(horizontal ? "Distribute Horizontal Gaps" : "Distribute Vertical Gaps", moves);
    }

    private void MoveObjects(string name, List<(Layer Root, float X, float Y)> moves)
    {
        if (IsInteracting || moves.All(m => Math.Abs(m.X) < .0001 && Math.Abs(m.Y) < .0001)) return;
        Begin(name);
        foreach (var (root, x, y) in moves)
        {
            var all = Document.Flatten([root]).ToList();
            Transform = new TransformEdit(this, all.Where(l => l.Pixels != null).ToList(), all.Where(l => l.Pixels == null && l.Mask != null).ToList());
            Transform.MoveBy(x, y);
            Transform = null;
        }
        Commit(); InvalidateAll(); LayersChanged?.Invoke();
    }
}
