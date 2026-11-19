using SkiaSharp;

namespace Composa.Model;

/// <summary>
/// Snapshot-based undo. Snapshots share bitmaps with the live document, so an entry only costs the pixels an
/// edit actually replaced. The oldest entries go once the distinct bitmaps held exceed the memory budget.
/// </summary>
/// <remarks>
/// Every state the document passes through has an id, so a state can be recognised again after undoing back to it:
/// that is how a document knows it is back where it was saved. An entry carries the id of the state after its step,
/// and keeps it as it moves between the undo and redo lists.
/// </remarks>
public sealed class History
{
    /// <summary>An undo entry holds the state before its step; a redo entry holds the state after it. Either way <c>Id</c> names the state after the step.</summary>
    private sealed record Entry(string Name, Document State, long Id);

    /// <summary>One row of the History panel: a state, named after the step that led to it.</summary>
    public readonly record struct Step(string Name, long Id);

    private readonly List<Entry> undo = [];
    private readonly List<Entry> redo = [];
    private long nextId = 1;
    /// <summary>The state before the oldest step kept.</summary>
    private long baseId;

    private int sinceCollect;
    // Count each shared bitmap once across the undo side. A multi-step scrub drops this index
    // and rebuilds it on the next edit, keeping scrubbing independent of the layer-tree size.
    private Dictionary<SKBitmap, int>? bitmapUses;
    private readonly HashSet<SKBitmap> stateBitmaps = new(ReferenceEqualityComparer.Instance);
    private long heldBytes;

    public long MemoryBudget { get; set; } = 3L * 1024 * 1024 * 1024;
    public int MaxEntries { get; set; } = 100;

    /// <summary>How the document began, which names the first row: "New Canvas", "Open". Once older steps are dropped the first row is named after the last one dropped.</summary>
    public string BaseName { get; set; } = "Open";
    /// <summary>Whether steps were dropped from the start of the list, so the first row is no longer where the document began.</summary>
    public bool EarlierStepsDropped { get; private set; }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string UndoName => undo.Count > 0 ? undo[^1].Name : "";
    public string RedoName => redo.Count > 0 ? redo[^1].Name : "";
    public int Count => undo.Count;

    /// <summary>The id of the state the document is in.</summary>
    public long CurrentId => undo.Count > 0 ? undo[^1].Id : baseId;

    /// <summary>
    /// Every state kept, oldest first: the one before the oldest step, then the state after each step. The document
    /// is in the one at <see cref="CurrentIndex"/>; those after it are what Redo brings back.
    /// </summary>
    public IReadOnlyList<Step> Steps
    {
        get
        {
            var steps = new List<Step>(1 + undo.Count + redo.Count) { new(BaseName, baseId) };
            steps.AddRange(undo.Select(e => new Step(e.Name, e.Id)));
            for (var i = redo.Count - 1; i >= 0; i--) steps.Add(new Step(redo[i].Name, redo[i].Id));
            return steps;
        }
    }

    public int CurrentIndex => undo.Count;

    /// <summary>Records the state as it was before an edit named <paramref name="name"/>.</summary>
    public void Push(string name, Document before)
    {
        if (bitmapUses == null)
        {
            bitmapUses = new(ReferenceEqualityComparer.Instance);
            heldBytes = 0;
            foreach (var entry in undo) CountBitmaps(entry.State, adding: true);
        }
        CountBitmaps(before, adding: true);
        undo.Add(new Entry(name, before, nextId++));
        redo.Clear();
        Trim();
    }

    public Document? Undo(Document current)
    {
        if (undo.Count == 0) return null;
        var entry = undo[^1];
        CountBitmaps(entry.State, adding: false);
        undo.RemoveAt(undo.Count - 1);
        redo.Add(entry with { State = current });
        return entry.State;
    }

    public Document? Redo(Document current)
    {
        if (redo.Count == 0) return null;
        var entry = redo[^1];
        CountBitmaps(current, adding: true);
        redo.RemoveAt(redo.Count - 1);
        undo.Add(entry with { State = current });
        return entry.State;
    }

    /// <summary>
    /// The state at <paramref name="index"/> in <see cref="Steps"/>, reached by undoing or redoing as many steps as
    /// it takes, or null when that is where the document already is or there is no such state. The states passed
    /// through go to the other list untouched, exactly as one Undo or Redo at a time would put them there.
    /// </summary>
    public Document? GoTo(int index, Document current)
    {
        if (index < 0 || index > undo.Count + redo.Count || index == undo.Count) return null;
        if (Math.Abs(undo.Count - index) > 1) bitmapUses = null;
        var state = current;
        while (undo.Count > index) state = Undo(state)!;
        while (undo.Count < index) state = Redo(state)!;
        return state;
    }

    /// <summary>Folds the newest entry into the one before it, so two consecutive edits undo as one step named <paramref name="name"/>.</summary>
    public void MergeLast(string name)
    {
        if (undo.Count < 2) return;
        var before = undo[^2].State;
        var after = undo[^1].Id;
        CountBitmaps(undo[^1].State, adding: false);
        undo.RemoveRange(undo.Count - 2, 2);
        undo.Add(new Entry(name, before, after));
    }

    /// <summary>Drops the newest undo entry without applying it, for edits that turned out to change nothing.</summary>
    public void DiscardLast()
    {
        if (undo.Count == 0) return;
        CountBitmaps(undo[^1].State, adding: false);
        undo.RemoveAt(undo.Count - 1);
    }

    public void Clear()
    {
        baseId = CurrentId;
        bitmapUses = null;
        undo.Clear();
        redo.Clear();
    }

    private void Trim()
    {
        var dropped = false;
        while (undo.Count > MaxEntries) { DropOldest(); dropped = true; }
        while (undo.Count > 1 && heldBytes > MemoryBudget) { DropOldest(); dropped = true; }
        // Bitmaps live in native memory the garbage collector cannot see, so it gets a nudge when history lets go of
        // some; they cannot simply be disposed here because newer snapshots may still share them.
        if (dropped && ++sinceCollect >= 8) { sinceCollect = 0; GC.Collect(2, GCCollectionMode.Optimized, blocking: false); }
    }

    /// <summary>The oldest state goes; the one after it, named after the step that made it, becomes the first.</summary>
    private void DropOldest()
    {
        baseId = undo[0].Id;
        BaseName = undo[0].Name;
        EarlierStepsDropped = true;
        CountBitmaps(undo[0].State, adding: false);
        undo.RemoveAt(0);
    }

    private void CountBitmaps(Document snapshot, bool adding)
    {
        if (bitmapUses == null) return;
        snapshot.CollectBitmaps(stateBitmaps);
        foreach (var bitmap in stateBitmaps)
        {
            var count = bitmapUses.GetValueOrDefault(bitmap);
            if (adding)
            {
                if (count == 0) heldBytes += bitmap.ByteCount;
                bitmapUses[bitmap] = count + 1;
            }
            else if (count == 1)
            {
                heldBytes -= bitmap.ByteCount;
                bitmapUses.Remove(bitmap);
            }
            else bitmapUses[bitmap] = count - 1;
        }
        stateBitmaps.Clear();
    }
}
