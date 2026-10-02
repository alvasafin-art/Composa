using Jint;

namespace Composa.App.Automation;

/// <summary>Bounds interpreter allocations; editor callbacks have their own document/pixel budgets.</summary>
internal sealed class ScriptMemoryBudget(long limit) : Constraint
{
    private readonly Dictionary<int, long> baseline = [];
    private long allocated;
    public override void Reset() { baseline.Clear(); allocated = 0; }
    public override void Check()
    {
        var thread = Environment.CurrentManagedThreadId; var now = GC.GetAllocatedBytesForCurrentThread();
        if (baseline.TryGetValue(thread, out var previous)) allocated += Math.Max(0, now - previous);
        baseline[thread] = now;
        if (allocated > limit) throw new InvalidOperationException("Script interpreter memory budget exceeded.");
    }
    public T Native<T>(Func<T> callback)
    {
        Check();
        try { return callback(); }
        finally
        {
            // Keep accumulated JS allocations; exclude only native editor work on this thread.
            baseline[Environment.CurrentManagedThreadId] = GC.GetAllocatedBytesForCurrentThread();
        }
    }
}
