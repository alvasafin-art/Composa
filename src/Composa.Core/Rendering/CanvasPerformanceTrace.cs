#if CANVAS_PERFORMANCE
using System.Diagnostics;
using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

/// <summary>Opt-in diagnostics for the repeatable canvas benchmark.</summary>
public static class CanvasPerformanceTrace
{
    private static readonly object Sync = new();
    private static readonly Dictionary<EffectKey, int> Running = [];
    private static readonly Dictionary<string, (long Count, long Bytes, long Largest)> Allocations = [];
    private static int hits, calculations, overlapping, patches;
    private static long frameAt;

    private readonly record struct EffectKey(SKBitmap Pixels, SKBitmap? Mask, LayerEffects Effects, double FillOpacity);
    public sealed record Surface(string Owner, long Count, long Bytes, long Largest);
    public sealed record Sample(int Hits, int Calculations, int MaxSameKey, int Patches, long CachedBytes, Surface[] Surfaces);

    public static long FrameAt => Interlocked.Read(ref frameAt);
    public static void FrameCompleted() => Interlocked.Exchange(ref frameAt, Stopwatch.GetTimestamp());
    internal static void EffectHit() { lock (Sync) hits++; }
    internal static void EffectPatch() { lock (Sync) patches++; }

    internal static void SurfaceAllocated(string owner, SKBitmap bitmap)
    {
        lock (Sync)
        {
            var previous = Allocations.GetValueOrDefault(owner);
            long bytes = (long)bitmap.RowBytes * bitmap.Height;
            Allocations[owner] = (previous.Count + 1, previous.Bytes + bytes, Math.Max(previous.Largest, bytes));
        }
    }

    internal static void EffectStarted(SKBitmap pixels, SKBitmap? mask, LayerEffects effects, double fillOpacity)
    {
        lock (Sync)
        {
            var key = new EffectKey(pixels, mask, effects, fillOpacity);
            var simultaneous = Running.GetValueOrDefault(key) + 1;
            Running[key] = simultaneous;
            calculations++;
            overlapping = Math.Max(overlapping, simultaneous);
        }
    }

    internal static void EffectCompleted(SKBitmap pixels, SKBitmap? mask, LayerEffects effects, double fillOpacity)
    {
        lock (Sync)
        {
            var key = new EffectKey(pixels, mask, effects, fillOpacity);
            if (Running[key] == 1) Running.Remove(key);
            else Running[key]--;
        }
    }

    public static Sample Snapshot()
    {
        var cacheBytes = LayerEffectsRenderer.MeasureCacheBytes();
        lock (Sync) return new(hits, calculations, overlapping, patches, cacheBytes,
            Allocations.Select(pair => new Surface(pair.Key, pair.Value.Count, pair.Value.Bytes, pair.Value.Largest)).ToArray());
    }

    public static void Reset()
    {
        lock (Sync)
        {
            hits = calculations = overlapping = patches = 0;
            Allocations.Clear();
        }
    }
}
#endif
