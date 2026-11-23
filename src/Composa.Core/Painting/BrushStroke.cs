using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

public enum BrushMode { Paint, Erase, Clone, Heal, Blur, Smudge, Dodge, Burn, Liquify, Healing }

public sealed record BrushSettings
{
    /// <summary>Diameter in document pixels.</summary>
    public double Size { get; init; } = 40;
    /// <summary>0 (fully soft) … 1 (hard edge).</summary>
    public double Hardness { get; init; } = 0.8;
    /// <summary>The most a whole stroke can cover, 0…1. For Blur, Smudge, Dodge and Burn this is the strength.</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>Paint deposited by each dab, before the whole stroke's opacity ceiling.</summary>
    public double Flow { get; init; } = 1;
    public bool PressureSize { get; init; } = true;
    public bool PressureFlow { get; init; }
    public bool FullCursor { get; init; }
    public double PressureRadius(double pressure) => PressureSize ? 0.15 + 0.85 * Math.Clamp(pressure, 0, 1) : 1;
    public double CursorRadius(double pressure = 1) => Size / 2 * PressureRadius(pressure) *
        (FullCursor ? 1 : (1 + Math.Clamp(Hardness, 0, 1)) / 2);
    /// <summary>Distance between dabs as a fraction of the diameter.</summary>
    public double Spacing { get; init; } = 0.08;
    /// <summary>
    /// 0…100. The brush trails the pointer on a string of this length (in screen points) and only moves once the
    /// pointer pulls it taut, so a shaky hand still draws a smooth line. 0 follows the pointer exactly.
    /// </summary>
    public double Smoothing { get; init; }
}

/// <summary>A stroke planned in advance, as <see cref="Editing.EditorSession.PaintStrokes"/> takes them.</summary>
public sealed record PlannedStroke(IReadOnlyList<SKPoint> Points, BrushSettings Brush, SKColor Color, BrushMode Mode = BrushMode.Paint);

/// <summary>
/// One drag of a brush-like tool over a bitmap (layer pixels or a mask). The stroke works on a private copy of
/// the bitmap and recomputes touched pixels from the untouched original plus the coverage painted so far, so
/// overlapping dabs never build up past the stroke's opacity.
/// </summary>
public sealed unsafe partial class BrushStroke : IDisposable
{
    private readonly SKBitmap original;
    private readonly BrushSettings settings;
    private readonly BrushMode mode;
    private readonly bool isMask;
    private readonly StrokeCoverage coverage;
    private float dabPressure = 1;
    private readonly int width, height, bytesPerPixel;
    private readonly float fullRadius;
    private float radius;
    private float lastPressure = 1;
    private readonly float[] premulColor = new float[4];
    private readonly byte maskValue;
    private SKBitmap? blurred;
    private float[]? carry;
    private int carrySize;
    private byte[] pushBytes = [];
    private PixelValue[] pushColors = [];
    private SKPoint? last;
    private SKPoint? lastDab;
    private float residual;
    // One small radial table per stroke, not a diameter-sized stamp per pressure change.
    // Squared-radius lookup removes sqrt/cos from the soft brush's hot inner loop.
    private const int FalloffSteps = 2048;
    private readonly float[] falloff = new float[FalloffSteps + 1];
    private readonly int flatFalloffIndex;
    private List<Stamp>? strokeStamps;
    private StampTiles? stampTiles;
    private readonly float hardness;

    /// <summary>The bitmap being painted; becomes the layer's new pixels when the stroke commits.</summary>
    public SKBitmap Working { get; }
    /// <summary>Everything the stroke has touched so far, in bitmap coordinates.</summary>
    public SKRectI Touched { get; private set; } = SKRectI.Empty;

    private readonly byte* selectionPixels;
    private readonly int selectionWidth, selectionHeight, selectionStride;
    /// <summary>Document-space selection limiting the stroke. Its storage lives for the whole stroke.</summary>
    public SKBitmap? Selection
    {
        get;
        init
        {
            field = value;
            if (value == null) return;
            selectionPixels = (byte*)value.GetPixels();
            selectionWidth = value.Width;
            selectionHeight = value.Height;
            selectionStride = value.RowBytes;
        }
    }
    private readonly bool selectionTranslationOnly = true;
    public SKMatrix ToDocument
    {
        get;
        init
        {
            field = value;
            selectionTranslationOnly = value == SKMatrix.CreateTranslation(value.TransX, value.TransY);
        }
    } = SKMatrix.Identity;
    public bool LockTransparency { get; init; }

    /// <summary>Clone source pixels (same format as the target) and the offset from target to source coordinates.</summary>
    public SKBitmap? CloneSource { get; init; }
    public SKPointI CloneOffset { get; set; }
    internal SKPoint CloneFraction { get; init; }

    public BrushStroke(SKBitmap target, BrushSettings settings, BrushMode mode, SKColor color, double scale = 1)
        : this(target, settings, mode, color, scale, Pixels.Clone(target)) { }

    internal BrushStroke(SKBitmap target, BrushSettings settings, BrushMode mode, SKColor color, double scale, SKBitmap working)
    {
        original = target;
        Working = working;
        this.settings = settings;
        this.mode = mode;
        isMask = target.ColorType == SKColorType.Alpha8;
        bytesPerPixel = target.BytesPerPixel;
        width = target.Width;
        height = target.Height;
        coverage = new StrokeCoverage(width, height);
        radius = fullRadius = (float)Math.Max(0.5, settings.Size / 2 / Math.Max(1e-6, scale));
        hardness = (float)Math.Clamp(settings.Hardness, 0, 1);
        for (var i = 0; i <= FalloffSteps; i++)
        {
            var distance = MathF.Sqrt(i / (float)FalloffSteps);
            var t = Math.Clamp((distance - hardness) / Math.Max(1e-3f, 1 - hardness), 0, 1);
            falloff[i] = 0.5f * (1 + MathF.Cos(MathF.PI * t));
            if (falloff[i] == 1) flatFalloffIndex = i;
        }
        var alpha = color.Alpha / 255f;
        premulColor[0] = color.Red * alpha; premulColor[1] = color.Green * alpha; premulColor[2] = color.Blue * alpha; premulColor[3] = color.Alpha;
        maskValue = (byte)((color.Red * 54 + color.Green * 183 + color.Blue * 19) >> 8);
    }

    /// <summary>Extends the stroke to a point (bitmap coordinates). Returns the changed rectangle.</summary>
    /// <param name="pressure">Pen pressure 0…1; it scales the brush size. Mice report 1.</param>
    public SKRectI AddPoint(SKPoint point, float pressure = 1) => FinishDamage(AddPoint(point, pressure, null));

    internal SKRectI AddPoints(ReadOnlySpan<(SKPoint Point, float Pressure)> points)
    {
        // Small dabs are cheaper to color in the per-segment path. Both
        // paths publish the combined damage once for the input event.
        var stamps = fullRadius >= StrokeCoverage.TileSize ? strokeStamps ??= new List<Stamp>(StampsPerBatch) : null;
        var dirty = SKRectI.Empty;
        foreach (var (point, pressure) in points)
            dirty = Geometry.Union(dirty, AddPoint(point, pressure, stamps));
        if (stamps != null) dirty = Geometry.Union(dirty, FlushStamps(stamps));
        return FinishDamage(dirty);
    }

    private SKRectI AddPoint(SKPoint point, float pressure, List<Stamp>? collected)
    {
        pressure = Math.Clamp(pressure, 0, 1);
        var dirty = SKRectI.Empty;
        if (last is not { } from)
        {
            lastPressure = pressure;
            dabPressure = pressure;
            radius = RadiusFor(pressure);
            dirty = Dab(point);
            last = point;
            residual = 0;
        }
        else
        {
            var diameter = RadiusFor(Math.Min(pressure, lastPressure)) * 2;
            var spacing = Math.Max(0.5f, diameter * (float)settings.Spacing);
            float dx = point.X - from.X, dy = point.Y - from.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length <= 0) return dirty;
            // Nearby dabs can share one color pass. For long moves, keep the work on the
            // tiles along the stroke so each pixel gets one color pass even on a diagonal.
            var deferColor = collected == null && length <= diameter
                && mode is (BrushMode.Paint or BrushMode.Erase or BrushMode.Clone or BrushMode.Heal or BrushMode.Healing);
            var travelled = spacing - residual;
            var batch = collected;
            if (batch == null && mode is (BrushMode.Paint or BrushMode.Erase) && !deferColor && length - travelled >= 3 * spacing)
                batch = strokeStamps ??= new List<Stamp>(StampsPerBatch);
            while (travelled <= length)
            {
                var t = travelled / length;
                dabPressure = lastPressure + (pressure - lastPressure) * t;
                radius = RadiusFor(dabPressure);
                var center = new SKPoint(from.X + dx * t, from.Y + dy * t);
                if (batch == null) dirty = Geometry.Union(dirty, Dab(center, deferColor));
                else
                {
                    var bounds = StampBounds(center);
                    if (!bounds.IsEmpty) batch.Add(new(center, radius, DabFlow(), bounds));
                    if (batch.Count == StampsPerBatch) dirty = Geometry.Union(dirty, FlushStamps(batch));
                }
                travelled += spacing;
            }
            if (batch != null && collected == null) dirty = Geometry.Union(dirty, FlushStamps(batch));
            if (deferColor && !dirty.IsEmpty) Recompute(dirty);
            residual = length - (travelled - spacing);
            last = point;
            lastPressure = pressure;
        }
        return dirty;
    }

    private SKRectI FinishDamage(SKRectI dirty)
    {
        if (!dirty.IsEmpty)
        {
            Touched = Geometry.Union(Touched, dirty);
            Pixels.InvalidateCaches(Working);
        }
        return dirty;
    }

    private float RadiusFor(float pressure) => Math.Max(0.5f, fullRadius * (float)settings.PressureRadius(pressure));

    /// <summary>A straight segment from the last point, for Shift-click lines.</summary>
    public SKRectI LineTo(SKPoint point) => AddPoint(point);

    private float Falloff(float distance) => Falloff(distance, radius);

    private float Falloff(float distance, float radius)
    {
        var edge = Math.Clamp(radius - distance + 0.5f, 0, 1);
        if (hardness >= 0.995f) return edge;
        var inner = radius * hardness;
        if (distance <= inner) return edge;
        var t = Math.Clamp((distance - inner) / Math.Max(1e-3f, radius - inner), 0, 1);
        return 0.5f * (1 + MathF.Cos(MathF.PI * t)) * edge;
    }

    private float CoverageAt(float dx, float dy, float radius)
    {
        var squared = dx * dx + dy * dy;
        var outer = radius + 0.5f;
        if (squared >= outer * outer) return 0;
        // Subpixel integration only for small stamps or a nearly hard rim. Broad soft
        // brushes keep a single cheap lookup per pixel and allocate no extra images.
        if (radius <= 2 || (radius * (1 - hardness) < 1 && squared > (radius - 1) * (radius - 1)))
            return (Falloff(MathF.Sqrt((dx - 0.25f) * (dx - 0.25f) + (dy - 0.25f) * (dy - 0.25f)), radius)
                + Falloff(MathF.Sqrt((dx + 0.25f) * (dx + 0.25f) + (dy - 0.25f) * (dy - 0.25f)), radius)
                + Falloff(MathF.Sqrt((dx - 0.25f) * (dx - 0.25f) + (dy + 0.25f) * (dy + 0.25f)), radius)
                + Falloff(MathF.Sqrt((dx + 0.25f) * (dx + 0.25f) + (dy + 0.25f) * (dy + 0.25f)), radius)) * 0.25f;
        if (hardness >= 0.995f) return Math.Clamp(radius - MathF.Sqrt(squared) + 0.5f, 0, 1);
        var position = Math.Min(FalloffSteps, squared / (radius * radius) * FalloffSteps);
        var index = Math.Min(FalloffSteps - 1, (int)position);
        var value = falloff[index] + (falloff[index + 1] - falloff[index]) * (position - index);
        if (squared > (radius - 0.5f) * (radius - 0.5f)) value *= Math.Clamp(radius - MathF.Sqrt(squared) + 0.5f, 0, 1);
        return value;
    }

    private SKRectI StampBounds(SKPoint center) => Geometry.Intersect(new SKRectI(
        (int)MathF.Floor(center.X - radius - 1), (int)MathF.Floor(center.Y - radius - 1),
        (int)MathF.Ceiling(center.X + radius + 1), (int)MathF.Ceiling(center.Y + radius + 1)), new SKRectI(0, 0, width, height));

    private float DabFlow() => (float)Math.Clamp(settings.Flow, 0, 1) * (settings.PressureFlow ? dabPressure : 1);

    // At these sizes the vectorized local work takes less time than moving brush
    // coverage and pixels between the editor and a separate OpenCL buffer.
    private bool PreferCpuCoverage(SKRectI area) =>
        (long)area.Width * area.Height < 640_000 && CanVectorizeCoverage(buildsUp: true, radius);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private SKRectI Dab(SKPoint center, bool deferColor = false)
    {
        var rect = StampBounds(center);
        if (rect.IsEmpty) return rect;
        // Track the individual footprint even when one input event crosses a long diagonal.
        LayerPixelStorage.Changed(Working, rect);
        if (mode == BrushMode.Smudge) return Smudge(center, rect) ? rect : SKRectI.Empty;
        if (mode == BrushMode.Liquify) return Push(center, rect);

        var changed = false;
        var flow = DabFlow();
        if (GpuAcceleration.EditorEnabled && !PreferCpuCoverage(rect)
            && mode is (BrushMode.Paint or BrushMode.Erase or BrushMode.Clone or BrushMode.Healing)
            && GpuAcceleration.TryCoverage(coverage, rect, center, radius, hardness, falloff, flow, out changed))
        {
            if (!changed) return SKRectI.Empty;
            if (!deferColor) Recompute(rect);
            return rect;
        }
        changed = AccumulateCoverage(rect, center, flow);
        if (!changed) return SKRectI.Empty;
        if (!deferColor) Recompute(rect);
        return rect;
    }

    private bool AccumulateCoverage(SKRectI rect, SKPoint center, float flow)
    {
        var buildsUp = mode is BrushMode.Paint or BrushMode.Erase or BrushMode.Clone or BrushMode.Healing;
        var bands = BrushBands(rect);
        if (bands == 1) return AccumulateBand(rect, center, radius, flow, buildsUp);

        // Each worker owns complete rows of coverage tiles, including their lazy allocation.
        var changed = 0;
        Parallel.For(0, bands, band =>
        {
            if (AccumulateBand(Band(rect, band, bands), center, radius, flow, buildsUp))
                Interlocked.Exchange(ref changed, 1);
        });
        return changed != 0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private bool AccumulateBand(SKRectI rect, SKPoint center, float radius, float flow, bool buildsUp)
    {
        var changed = false;
        var outer = radius + .5f;
        // Even the strongest part of the dab cannot change coverage once the
        // maximum possible increment rounds to zero. Leave room for float rounding.
        var saturated = buildsUp && flow >= 0 ? ushort.MaxValue - Math.Min(ushort.MaxValue, (int)(.49f / Math.Max(flow, 1e-8f)))
            : ushort.MaxValue;
        if (saturated == 0) return false;
        var vectorize = CanVectorizeCoverage(buildsUp, radius);
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var dy = y + .5f - center.Y;
            for (var x = rect.Left; x < rect.Right;)
            {
                var end = Math.Min(rect.Right, (x | (StrokeCoverage.TileSize - 1)) + 1);
                var left = x;
                // Skip segments completely outside the dab, without allocating a coverage tile.
                var closest = Math.Clamp(center.X, left + .5f, end - .5f) - center.X;
                x = end;
                if (closest * closest + dy * dy >= outer * outer) continue;

                var row = coverage.WritableRowSegment(left, y, end - left);
                var offset = 0;
                if (vectorize && row.Length >= 16)
                    changed |= AccumulateVectors(row, left, center.X, dy, radius, flow, saturated, out offset);

                for (; offset < row.Length; offset++)
                {
                    var old = row[offset];
                    if (old >= saturated) continue;
                    var amount = CoverageAt(left + offset + .5f - center.X, dy, radius);
                    if (amount <= 0) continue;
                    var accumulated = buildsUp
                        ? old + (ushort.MaxValue - old) * amount * flow
                        : Math.Max(old, amount * ushort.MaxValue);
                    var next = (ushort)Math.Clamp((int)MathF.Round(accumulated), 0, ushort.MaxValue);
                    if (next == old) continue;
                    row[offset] = next;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private const int StampsPerBatch = 64;
    private readonly record struct Stamp(SKPoint Center, float Radius, float Flow, SKRectI Bounds);
    private sealed class StampTile
    {
        public List<Stamp> Stamps { get; } = [];
        public SKRectI Damage { get; set; }
    }

    private sealed class StampTiles
    {
        private readonly Dictionary<SKPointI, int> indices = [];
        private readonly List<StampTile> jobs = [];
        public int Count => indices.Count;
        public StampTile this[int index] => jobs[index];

        public StampTile Get(SKPointI position)
        {
            if (indices.TryGetValue(position, out var index)) return jobs[index];
            index = indices.Count;
            indices.Add(position, index);
            if (index == jobs.Count) jobs.Add(new StampTile());
            var tile = jobs[index];
            tile.Stamps.Clear();
            tile.Damage = SKRectI.Empty;
            return tile;
        }

        public void Clear() => indices.Clear();
    }

    private static readonly ParallelOptions TileWorkers = new() { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) };

    private SKRectI FlushStamps(List<Stamp> stamps)
    {
        if (stamps.Count == 0) return SKRectI.Empty;
        var dirty = RenderBatch(stamps);
        stamps.Clear();
        return dirty;
    }

    private SKRectI RenderBatch(List<Stamp> stamps)
    {
        // Tile boundaries are aligned with StrokeCoverage, so workers never share a writable tile.
        // A worker keeps stamp order within each tile to preserve 16-bit coverage rounding.
        const int tileSize = StrokeCoverage.TileSize * 2;
        var tiles = stampTiles ??= new StampTiles();
        tiles.Clear();
        long work = 0;
        foreach (var stamp in stamps)
        {
            var bounds = stamp.Bounds;
            work += (long)bounds.Width * bounds.Height;
            for (var y = bounds.Top / tileSize; y <= (bounds.Bottom - 1) / tileSize; y++)
            for (var x = bounds.Left / tileSize; x <= (bounds.Right - 1) / tileSize; x++)
            {
                var job = tiles.Get(new SKPointI(x, y));
                var part = SKRectI.Intersect(bounds, SKRectI.Create(x * tileSize, y * tileSize, tileSize, tileSize));
                job.Stamps.Add(stamp with { Bounds = part });
            }
        }

        // One dispatch for the entire segment avoids synchronizing CPU bands or waiting for
        // a GPU readback on every overlapping dab. The color calculation uses the same coverage.
        if (work < 96_000 || tiles.Count == 1 || Environment.ProcessorCount == 1)
            for (var i = 0; i < tiles.Count; i++) RenderTile(tiles[i]);
        else
            Parallel.For(0, tiles.Count, TileWorkers, i => RenderTile(tiles[i]));

        var dirty = SKRectI.Empty;
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (tile.Damage.IsEmpty) continue;
            LayerPixelStorage.Changed(Working, tile.Damage);
            dirty = Geometry.Union(dirty, tile.Damage);
        }
        return dirty;
    }

    private void RenderTile(StampTile tile)
    {
        var dirty = SKRectI.Empty;
        foreach (var stamp in tile.Stamps)
            if (AccumulateBand(stamp.Bounds, stamp.Center, stamp.Radius, stamp.Flow, buildsUp: true))
                dirty = Geometry.Union(dirty, stamp.Bounds);
        if (dirty.IsEmpty) return;
        RecomputeBand(dirty);
        tile.Damage = dirty;
    }

    private static int BrushBands(SKRectI rect)
    {
        if ((long)rect.Width * rect.Height < 96_000) return 1;
        var tileRows = (rect.Bottom - 1) / StrokeCoverage.TileSize - rect.Top / StrokeCoverage.TileSize + 1;
        return Math.Min(tileRows, Math.Min(Environment.ProcessorCount, 8));
    }

    private static SKRectI Band(SKRectI rect, int band, int bands)
    {
        const int tile = StrokeCoverage.TileSize;
        var first = rect.Top / tile;
        var rows = (rect.Bottom - 1) / tile - first + 1;
        return new SKRectI(rect.Left, Math.Max(rect.Top, (first + rows * band / bands) * tile),
            rect.Right, Math.Min(rect.Bottom, (first + rows * (band + 1) / bands) * tile));
    }

    private float SelectionAt(int x, int y)
    {
        if (selectionPixels == null) return 1;
        if (selectionTranslationOnly)
        {
            x = (int)MathF.Floor((x + 0.5f) + ToDocument.TransX);
            y = (int)MathF.Floor((y + 0.5f) + ToDocument.TransY);
        }
        else
        {
            var p = ToDocument.MapPoint(x + 0.5f, y + 0.5f);
            x = (int)MathF.Floor(p.X);
            y = (int)MathF.Floor(p.Y);
        }
        if ((uint)x >= (uint)selectionWidth || (uint)y >= (uint)selectionHeight) return 0;
        return selectionPixels[(long)y * selectionStride + x] / 255f;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void Recompute(SKRectI rect)
    {
        if (GpuAcceleration.EditorEnabled && !PreferCpuCoverage(rect) && mode is (BrushMode.Paint or BrushMode.Erase)
            && selectionPixels == null && !LockTransparency && Working.ColorType == SKColorType.Rgba8888
            && GpuAcceleration.TryBrush(original, Working, rect, coverage, GpuPaintColor, (float)Math.Clamp(settings.Opacity, 0, 1)))
            return;
        if (mode == BrushMode.Blur && blurred == null) blurred = MakeBlurred();
        var bands = mode is BrushMode.Paint or BrushMode.Erase ? BrushBands(rect) : 1;
        if (bands == 1) RecomputeBand(rect);
        else Parallel.For(0, bands, band => RecomputeBand(Band(rect, band, bands)));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void RecomputeBand(SKRectI rect)
    {
        if (Working.ColorType == SKColorType.RgbaF32) { RecomputeHighPrecision(rect); return; }
        var src = (byte*)original.GetPixels();
        var dst = (byte*)Working.GetPixels();
        var sourceStride = original.RowBytes;
        var outputStride = Working.RowBytes;
        var opacity = (float)Math.Clamp(settings.Opacity, 0, 1);
        byte* clone = CloneSource != null ? (byte*)CloneSource.GetPixels() : null;
        int cloneWidth = CloneSource?.Width ?? 0, cloneHeight = CloneSource?.Height ?? 0, cloneStride = CloneSource?.RowBytes ?? 0;
        byte* soft = blurred != null ? (byte*)blurred.GetPixels() : null;
        Span<float> source = stackalloc float[4];
        if (mode == BrushMode.Paint)
        {
            if (isMask) source[0] = maskValue;
            else premulColor.CopyTo(source);
        }

        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var input = src + (long)y * sourceStride;
            var output = dst + (long)y * outputStride;
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var c = coverage[x, y] / 257f;
                if (c == 0) continue;
                var a = c / 255f * opacity * SelectionAt(x, y);
                if (LockTransparency && mode == BrushMode.Erase) a = 0;
                var index = x * bytesPerPixel;
                if (a <= 0) { for (var i = 0; i < bytesPerPixel; i++) output[index + i] = input[index + i]; continue; }

                switch (mode)
                {
                    case BrushMode.Erase:
                        source.Clear();
                        break;
                    case BrushMode.Heal:
                        // Shown as a dark veil while dragging; the heal itself runs when the stroke ends.
                        source[0] = source[1] = source[2] = 0; source[3] = 255; a *= 0.45f;
                        break;
                    case BrushMode.Clone:
                    case BrushMode.Healing:
                        int cx = x + CloneOffset.X, cy = y + CloneOffset.Y;
                        if (clone == null || cx < 0 || cy < 0 || cx >= cloneWidth || cy >= cloneHeight) { source.Clear(); a = 0; }
                        else
                        {
                            var ci = (long)cy * cloneStride + cx * bytesPerPixel;
                            if (CloneFraction == SKPoint.Empty)
                                for (var i = 0; i < bytesPerPixel; i++) source[i] = clone[ci + i];
                            else
                            {
                                var right = cx + 1 < cloneWidth ? bytesPerPixel : 0;
                                var below = cy + 1 < cloneHeight ? cloneStride : 0;
                                float fx = CloneFraction.X, fy = CloneFraction.Y;
                                for (var i = 0; i < bytesPerPixel; i++)
                                {
                                    var top = clone[ci + i] * (1 - fx) + clone[ci + right + i] * fx;
                                    var bottom = clone[ci + below + i] * (1 - fx) + clone[ci + below + right + i] * fx;
                                    source[i] = top * (1 - fy) + bottom * fy;
                                }
                            }
                        }
                        break;
                    case BrushMode.Blur:
                        for (var i = 0; i < bytesPerPixel; i++) source[i] = soft![(long)y * outputStride + index + i];
                        break;
                    case BrushMode.Dodge:
                    case BrushMode.Burn:
                        if (isMask) source[0] = mode == BrushMode.Dodge ? input[index] + (255 - input[index]) * 0.5f : input[index] * 0.5f;
                        else
                        {
                            float alpha = input[index + 3];
                            for (var i = 0; i < 3; i++)
                                source[i] = mode == BrushMode.Dodge ? input[index + i] + (alpha - input[index + i]) * 0.5f : input[index + i] * 0.5f;
                            source[3] = alpha;
                        }
                        break;
                }
                for (var i = 0; i < bytesPerPixel; i++)
                    output[index + i] = (byte)Math.Clamp(input[index + i] * (1 - a) + source[i] * a + 0.5f, 0, 255);
                if (LockTransparency && !isMask) Pixels.PreserveAlpha(output + index, input[index + 3]);
            }
        }
    }

    private SKBitmap MakeBlurred()
    {
        if (original.ColorType == SKColorType.RgbaF32)
        {
            var high = Pixels.Clone(original);
            Filters.ImageFilters.BlurInPlace(high, Math.Max(1.5f, radius * .25f));
            return high;
        }
        var result = new SKBitmap(original.Info);
        result.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(result);
        var sigma = Math.Max(1.5f, radius * 0.25f);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp), BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(original, 0, 0, paint);
        return result;
    }

    private SKColorF GpuPaintColor => mode == BrushMode.Erase ? default
        : new(premulColor[0], premulColor[1], premulColor[2], premulColor[3]);

    internal static bool MatchesGpu(OpenClDevice backend, SKBitmap source, BrushSettings settings, BrushMode mode)
    {
        using var stroke = new BrushStroke(source, settings, mode, new SKColor(219, 31, 139, 193));
        using var expected = stroke.Working;
        stroke.AddPoint(new(51.23f, 47.49f), .88f);
        stroke.AddPoint(new(99.1f, 66.6f), .72f);
        using var actual = Pixels.Clone(source);
        return backend.TryPaint(source, actual, stroke.Touched, stroke.coverage, stroke.GpuPaintColor, (float)settings.Opacity)
            && expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan());
    }

    internal static bool CoverageMatchesGpu(OpenClDevice backend)
    {
        using var source = Pixels.NewMask(181, 137);
        var settings = new BrushSettings { Size = 116, Flow = .71, Hardness = .54 };
        using var stroke = new BrushStroke(source, settings, BrushMode.Paint, SKColors.White);
        using var pixels = stroke.Working;
        var accelerated = new StrokeCoverage(source.Width, source.Height);
        var bounds = source.Info.Rect;
        foreach (var point in new SKPoint[] { new(51.23f, 47.49f), new(99.1f, 66.6f), new(133.31f, 111.44f) })
        {
            var area = stroke.Dab(point, deferColor: true);
            if (!backend.TryBrushCoverage(accelerated, area, point, stroke.radius, stroke.hardness, stroke.falloff,
                (float)settings.Flow, out _)) return false;
        }
        var expected = new ushort[source.Width * source.Height];
        var actual = new ushort[expected.Length];
        stroke.coverage.CopyTo(bounds, expected);
        accelerated.CopyTo(bounds, actual);
        return expected.AsSpan().SequenceEqual(actual);
    }

    // Smudge drags a buffer of paint along: each dab lays the carried pixels down, then picks up what was beneath.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private bool Smudge(SKPoint center, SKRectI rect)
    {
        if (Working.ColorType == SKColorType.RgbaF32) return SmudgeHighPrecision(center, rect);
        var size = (int)MathF.Ceiling(fullRadius * 2 + 3);
        var dst = (byte*)Working.GetPixels();
        var stride = Working.RowBytes;
        var alphaSource = LockTransparency && !isMask ? (byte*)original.GetPixels() : null;
        var alphaStride = original.RowBytes;
        int originX = (int)MathF.Floor(center.X - radius - 1), originY = (int)MathF.Floor(center.Y - radius - 1);
        if (carry == null)
        {
            carrySize = size;
            carry = new float[size * size * 4];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                int px = Math.Clamp(originX + x, 0, width - 1), py = Math.Clamp(originY + y, 0, height - 1);
                for (var i = 0; i < bytesPerPixel; i++) carry[(y * size + x) * 4 + i] = dst[(long)py * stride + px * bytesPerPixel + i];
            }
            return false; // The first dab only picks paint up.
        }
        var strength = (float)Math.Clamp(settings.Opacity, 0, 1) * 0.95f;
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        {
            int lx = x - originX, ly = y - originY;
            if (lx < 0 || ly < 0 || lx >= carrySize || ly >= carrySize) continue;
            float dx = x + 0.5f - center.X, dy = y + 0.5f - center.Y;
            var c = Falloff(MathF.Sqrt(dx * dx + dy * dy)) * SelectionAt(x, y);
            var index = (long)y * stride + x * bytesPerPixel;
            var slot = (ly * carrySize + lx) * 4;
            for (var i = 0; i < bytesPerPixel; i++)
            {
                float under = dst[index + i];
                carry[slot + i] = carry[slot + i] * strength + under * (1 - strength);
                if (c > 0) dst[index + i] = (byte)Math.Clamp(under + (carry[slot + i] - under) * c + 0.5f, 0, 255);
            }
            if (alphaSource != null) Pixels.PreserveAlpha(dst + index, alphaSource[(long)y * alphaStride + x * 4 + 3]);
        }
        return true;
    }

    // Liquify pushes pixels along the drag: every pixel under the brush is re-read from a little way back along the
    // movement, most strongly at the center.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private SKRectI Push(SKPoint center, SKRectI rect)
    {
        if (Working.ColorType == SKColorType.RgbaF32) return PushHighPrecision(center, rect);
        var previous = lastDab;
        lastDab = center;
        if (previous is not { } from) return SKRectI.Empty;
        float dx = center.X - from.X, dy = center.Y - from.Y;
        if (dx == 0 && dy == 0) return SKRectI.Empty;
        var strength = (float)Math.Clamp(settings.Opacity, 0, 1);
        var reach = (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy))) + 2;
        var area = Geometry.Intersect(new SKRectI(rect.Left - reach, rect.Top - reach, rect.Right + reach, rect.Bottom + reach), new SKRectI(0, 0, width, height));
        int aw = area.Width, ah = area.Height;
        var dst = (byte*)Working.GetPixels();
        var stride = Working.RowBytes;
        var alphaSource = LockTransparency && !isMask ? (byte*)original.GetPixels() : null;
        var alphaStride = original.RowBytes;
        var length = aw * ah * bytesPerPixel;
        if (pushBytes.Length < length) pushBytes = new byte[length];
        var snapshot = pushBytes;
        for (var y = 0; y < ah; y++)
            fixed (byte* row = &snapshot[y * aw * bytesPerPixel])
                Buffer.MemoryCopy(dst + (long)(y + area.Top) * stride + (long)area.Left * bytesPerPixel, row, aw * bytesPerPixel, aw * bytesPerPixel);

        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        {
            float px = x + 0.5f - center.X, py = y + 0.5f - center.Y;
            var weight = Falloff(MathF.Sqrt(px * px + py * py)) * strength * SelectionAt(x, y);
            if (weight <= 0) continue;
            float sx = x - dx * weight - area.Left, sy = y - dy * weight - area.Top;
            int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
            float tx = sx - x0, ty = sy - y0;
            int xa = Math.Clamp(x0, 0, aw - 1), xb = Math.Clamp(x0 + 1, 0, aw - 1), ya = Math.Clamp(y0, 0, ah - 1), yb = Math.Clamp(y0 + 1, 0, ah - 1);
            var index = (long)y * stride + x * bytesPerPixel;
            for (var i = 0; i < bytesPerPixel; i++)
            {
                var top = snapshot[(ya * aw + xa) * bytesPerPixel + i] * (1 - tx) + snapshot[(ya * aw + xb) * bytesPerPixel + i] * tx;
                var bottom = snapshot[(yb * aw + xa) * bytesPerPixel + i] * (1 - tx) + snapshot[(yb * aw + xb) * bytesPerPixel + i] * tx;
                dst[index + i] = (byte)Math.Clamp(top * (1 - ty) + bottom * ty + 0.5f, 0, 255);
            }
            if (alphaSource != null) Pixels.PreserveAlpha(dst + index, alphaSource[(long)y * alphaStride + x * 4 + 3]);
        }
        return rect;
    }

    /// <summary>The painted coverage as an Alpha8 mask, for tools that finish their work after the drag (healing).</summary>
    public SKBitmap CoverageMask()
    {
        var mask = Pixels.NewMask(width, height);
        var dst = (byte*)mask.GetPixels();
        var stride = mask.RowBytes;
        for (var y = Touched.Top; y < Touched.Bottom; y++)
            for (var x = Touched.Left; x < Touched.Right; x++) dst[(long)y * stride + x] = (byte)((coverage[x, y] + 128) / 257);
        return mask;
    }

    public void Dispose() => blurred?.Dispose();
}
