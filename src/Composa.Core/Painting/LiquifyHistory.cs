using Composa.Model;

namespace Composa.Painting;

/// <summary>Local stroke history bounded by the distinct shared tile arrays, not by full image snapshots.</summary>
public sealed class LiquifyHistory(LiquifyField initial)
{
    private readonly List<LiquifyField> undo = [], redo = [];
    public LiquifyField Current { get; private set; } = initial;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public long MemoryBudget { get; set; } = DocumentLimits.MaxSurfaceBytes;
    public long RetainedBytes => undo.Concat(redo).Append(Current).SelectMany(f => f.Storage).DistinctBy(s => s.Identity, ReferenceEqualityComparer.Instance).Sum(s => s.Bytes);
    public bool Push(LiquifyField next)
    {
        if (ReferenceEquals(next, Current)) return false;
        if (next.Width != Current.Width || next.Height != Current.Height) throw new ArgumentException("History grids must match.", nameof(next));
        undo.Add(Current); Current = next; redo.Clear();
        while (undo.Count > 0 && (undo.Count > 100 || RetainedBytes > MemoryBudget)) undo.RemoveAt(0);
        return true;
    }
    public void Undo() { if (!CanUndo) return; redo.Add(Current); Current = undo[^1]; undo.RemoveAt(undo.Count - 1); }
    public void Redo() { if (!CanRedo) return; undo.Add(Current); Current = redo[^1]; redo.RemoveAt(redo.Count - 1); }
}
