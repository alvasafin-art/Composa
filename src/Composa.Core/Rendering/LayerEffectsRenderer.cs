using Composa.Model;
using SkiaSharp;
using System.Runtime.CompilerServices;

namespace Composa.Rendering;

/// <summary>
/// Draws a layer's effects around its pixels. The result is the layer as it should appear (shadow and glow behind,
/// stroke around, pixels on top, overlay, inner glow and inner shadow over them) on a canvas grown by <c>Inset</c> pixels on every side,
/// so the caller places it by shifting the layer's own matrix by the same amount. The last few results are kept, so
/// redrawing the canvas does not rebuild them.
/// </summary>
public static class LayerEffectsRenderer
{
    public const long MaxPixels = 100_000_000;

    private readonly record struct Key(SKBitmap Pixels, SKBitmap? Mask, LayerEffects Effects, double FillOpacity)
    {
        public bool Includes(SKBitmap bitmap) => ReferenceEquals(Pixels, bitmap) || ReferenceEquals(Mask, bitmap);
        public bool Equals(Key other) => ReferenceEquals(Pixels, other.Pixels) && ReferenceEquals(Mask, other.Mask)
            && Effects == other.Effects && FillOpacity.Equals(other.FillOpacity);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Pixels),
            Mask == null ? 0 : RuntimeHelpers.GetHashCode(Mask), Effects, FillOpacity);
    }

    private sealed class Entry(Key key, SKBitmap image, int inset)
    {
        public Key Key { get; } = key;
        public (SKBitmap Image, int Inset) Result { get; } = (image, inset);
        public LinkedListNode<WeakReference<Entry>>? Order { get; set; }
    }

    private sealed class Pending
    {
        public Lazy<(SKBitmap Image, int Inset)?> Result { get; }
        public Pending(Key key) => Result = new(() => Complete(key, this));
    }

    // The cache cannot keep a layer's pixels (and thus its native surface) alive after
    // its last project, snapshot or render releases them. The LRU only indexes the owners.
    private static readonly ConditionalWeakTable<SKBitmap, List<Entry>> Cache = new();
    private static readonly LinkedList<WeakReference<Entry>> Lru = new();
    private static readonly Dictionary<Key, Pending> InFlight = [];
    private const int MaxEntries = 12;
    private const long Budget = 384L * 1024 * 1024;

    static LayerEffectsRenderer() => Pixels.Invalidated += bitmap =>
    {
        // A stroke in progress keeps its effects from the moment it started; the final pixels are rendered once it ends.
        if (Pixels.IsLive(bitmap)) return;
        Forget(key => key.Includes(bitmap));
    };

    internal static void Forget(IReadOnlySet<SKBitmap> owned) =>
        Forget(key => owned.Contains(key.Pixels) || (key.Mask != null && owned.Contains(key.Mask)));

    private static void Forget(Func<Key, bool> matches)
    {
        lock (Lru)
        {
            for (var node = Lru.First; node != null;)
            {
                var next = node.Next;
                if (!node.Value.TryGetTarget(out var entry)) Lru.Remove(node);
                else if (matches(entry.Key)) Remove(entry);
                node = next;
            }
            foreach (var key in InFlight.Keys.Where(matches).ToArray()) InFlight.Remove(key);
        }
    }

    /// <summary>
    /// The layer's pixels, through its mask, with its visible effects around them; null when there is nothing to
    /// draw or the result would be too large, in which case the caller draws the layer as it is.
    /// </summary>
    public static (SKBitmap Image, int Inset)? Cached(SKBitmap pixels, SKBitmap? mask, LayerEffects? effects, double fillOpacity = 1)
    {
        if (effects == null) return null;
        var visible = effects.Visible();
        if (visible.IsEmpty) return null;
        var key = new Key(pixels, mask, visible, fillOpacity);
        Pending pending;
        lock (Lru)
        {
            if (Cache.TryGetValue(pixels, out var entries) && entries.Find(e => e.Key == key) is { } hit)
            {
                if (hit.Order is { } node && node != Lru.Last) { Lru.Remove(node); Lru.AddLast(node); }
#if CANVAS_PERFORMANCE
                CanvasPerformanceTrace.EffectHit();
#endif
                return hit.Result;
            }
            if (!InFlight.TryGetValue(key, out pending!))
                InFlight.Add(key, pending = new Pending(key));
        }
        // Only threads asking for this key share the calculation. There is no cache
        // lock held while Skia builds the effect or another band draws its own layer.
        return pending.Result.Value;
    }

    private static (SKBitmap Image, int Inset)? Complete(Key key, Pending request)
    {
        try
        {
            (SKBitmap Image, int Inset)? made;
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.EffectStarted(key.Pixels, key.Mask, key.Effects, key.FillOpacity);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { made = Render(key.Pixels, key.Mask, key.Effects, key.FillOpacity); }
            finally
            {
                CanvasPerformanceTrace.StageCompleted("full effect", started);
                CanvasPerformanceTrace.EffectCompleted(key.Pixels, key.Mask, key.Effects, key.FillOpacity);
            }
#else
            made = Render(key.Pixels, key.Mask, key.Effects, key.FillOpacity);
#endif
            lock (Lru)
            {
                // Invalidation or clearing the cache can detach an in-flight request.
                // Its original callers can finish; the old image must not reenter the cache.
                if (made is { } result && InFlight.GetValueOrDefault(key) == request)
                    Remember(key, result.Image, result.Inset);
            }
            return made;
        }
        finally
        {
            lock (Lru)
                if (InFlight.GetValueOrDefault(key) == request) InFlight.Remove(key);
        }
    }

    private static void Remember(Key key, SKBitmap image, int inset)
    {
        var entry = new Entry(key, image, inset);
        Cache.GetOrCreateValue(key.Pixels).Add(entry);
        entry.Order = Lru.AddLast(new WeakReference<Entry>(entry));
        Trim();
    }

    private static void Trim()
    {
        var bytes = BytesInLru();
        while (Lru.First is { } first && (Lru.Count > MaxEntries || (Lru.Count > 1 && bytes > Budget)))
        {
            if (first.Value.TryGetTarget(out var entry))
            {
                bytes -= entry.Result.Image.ByteCount;
                Remove(entry);
            }
            else Lru.Remove(first);
        }
    }

    private static long BytesInLru()
    {
        long bytes = 0;
        for (var node = Lru.First; node != null;)
        {
            var next = node.Next;
            if (node.Value.TryGetTarget(out var entry)) bytes += entry.Result.Image.ByteCount;
            else Lru.Remove(node);
            node = next;
        }
        return bytes;
    }

    private static void Remove(Entry entry)
    {
        if (Cache.TryGetValue(entry.Key.Pixels, out var entries)) entries.Remove(entry);
        if (entry.Order is { } node) Lru.Remove(node);
        entry.Order = null;
        // An in-progress render or an SKImage wrapper can still use this result.
    }

#if CANVAS_PERFORMANCE
    internal static long MeasureCacheBytes() { lock (Lru) return BytesInLru(); }
#endif

    /// <summary>Drops every cached result, for tests and when memory is short.</summary>
    public static void ClearCache()
    {
        lock (Lru)
        {
            Cache.Clear();
            Lru.Clear();
            InFlight.Clear();
        }
    }

    internal sealed record Patch(Guid LayerId, SKBitmap Image, int Inset, SKPointI Offset, SKSize Scale) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }

    internal sealed class StrokeBase(SKBitmap image, int inset, SKPointI origin, LayerEffects effects, double fillOpacity) : IDisposable
    {
        private SKBitmap? firstPatch;
        private SKRectI firstDestination;
        public LayerEffects Effects { get; } = effects;
        internal SKBitmap? Updated { get; set; }
        internal SKRectI Pending { get; set; }
        internal int Inset => inset;
        internal double FillOpacity => fillOpacity;

        internal void AddDamage(SKRectI changed)
        {
            if (changed.IsEmpty) return;
            var halo = PatchHalo(Effects);
            changed.Inflate(halo, halo);
            Pending = Geometry.Union(Pending, changed);
        }

        internal SKBitmap? Prepare(Layer layer, SKRectI full)
        {
            if (Updated != null) return Updated;
            if ((long)full.Width * full.Height > MaxPixels || full.Width > Document.MaxSide * 2 || full.Height > Document.MaxSide * 2)
                return null;
            var matrix = layer.Matrix;
            var previous = SKRectI.Create(origin.X - (int)matrix.TransX, origin.Y - (int)matrix.TransY, image.Width, image.Height);
            var bounds = SKRectI.Create(full.Width, full.Height);
            if (!bounds.Contains(previous)) return null;
            var result = previous == bounds ? Pixels.Clone(image) : Pixels.NewLike(layer.Pixels!, full.Width, full.Height);
            try
            {
                if (previous != bounds) DocumentRenderer.CopyRows(image, SKPointI.Empty, result, previous);
                if (firstPatch is { } first) DocumentRenderer.CopyRows(first, SKPointI.Empty, result, firstDestination);
            }
            catch { result.Dispose(); throw; }
            firstPatch?.Dispose();
            firstPatch = null;
            return Updated = result;
        }

        internal void Apply(Layer layer, SKBitmap patch, SKRectI region, SKRectI valid, SKRectI full)
        {
            if (Pending.IsEmpty) return;
            var changed = SKRectI.Intersect(Pending, full);
            if (changed.IsEmpty || !valid.Contains(changed)) return;
            var source = new SKPointI(changed.Left - region.Left, changed.Top - region.Top);
            changed.Offset(inset, inset);
            if (Updated == null && firstPatch == null)
            {
                // Let the first dab reach the screen before allocating the full cache image.
                firstPatch = Pixels.Subset(patch, SKRectI.Create(source.X, source.Y, changed.Width, changed.Height));
                firstDestination = changed;
            }
            else
            {
                if (Prepare(layer, full) is not { } result) return;
                DocumentRenderer.CopyRows(patch, source, result, changed);
            }
            Pending = SKRectI.Empty;
        }

        public void Dispose()
        {
            firstPatch?.Dispose();
            Updated?.Dispose();
        }
    }

    internal static StrokeBase? CaptureStrokeBase(Layer layer)
    {
        if (PatchEffects(layer) is not { } effects) return null;
        var key = new Key(layer.Pixels!, layer.RenderMask(), effects, layer.FillOpacity);
        lock (Lru)
        {
            if (!Cache.TryGetValue(key.Pixels, out var entries) || entries.Find(entry => entry.Key == key) is not { } hit)
                return null;
            var matrix = layer.Matrix;
            return new(hit.Result.Image, hit.Result.Inset, new SKPointI((int)matrix.TransX, (int)matrix.TransY), effects, layer.FillOpacity);
        }
    }

    internal static bool CacheStroke(Layer layer, StrokeBase basis)
    {
        if (PatchEffects(layer) != basis.Effects || layer.FillOpacity != basis.FillOpacity) return false;
        var pixels = layer.Pixels!;
        var mask = layer.RenderMask();
        var inset = basis.Inset;
        var full = new SKRectI(-inset, -inset, pixels.Width + inset, pixels.Height + inset);
        if (!basis.Pending.IsEmpty)
        {
            var changed = SKRectI.Intersect(basis.Pending, full);
            var region = changed;
            var halo = PatchHalo(basis.Effects);
            region.Inflate(halo, halo);
            region = SKRectI.Intersect(region, full);
            if (changed.IsEmpty || region == full || basis.Prepare(layer, full) == null) return false;
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.EffectPatch();
#endif
            using var patch = RenderArea(pixels, mask, basis.Effects, layer.FillOpacity, region);
            basis.Apply(layer, patch, region, changed, full);
        }
        if (basis.Prepare(layer, full) is not { } result) return false;
        var key = new Key(pixels, mask, basis.Effects, layer.FillOpacity);
        lock (Lru)
        {
            if (InFlight.ContainsKey(key) ||
                (Cache.TryGetValue(pixels, out var entries) && entries.Exists(entry => entry.Key == key))) return false;
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.SurfaceAllocated("effect retained after stroke", result);
#endif
            Remember(key, result, inset);
            basis.Updated = null;
            return true;
        }
    }

    // Four sigmas cover the CPU Gaussian sampling footprint with room for filtering
    // a subpixel shadow offset. Stroke morphology has its own, integral radius.
    internal static int PatchHalo(LayerEffects effects) => (int)Math.Ceiling(Math.Max(
        Math.Max(effects.Stroke?.Size ?? 0, (effects.OuterGlow?.Size ?? 0) * 2),
        (effects.Shadow?.Distance ?? 0) + (effects.Shadow?.Blur ?? 0) * 2)) + 4;

    /// <summary>Only translated raster content with spatially local effects takes the patch path.</summary>
    internal static LayerEffects? PatchEffects(Layer layer)
    {
        if (layer.Pixels == null || layer.IsSmartObject || layer.FilterSource != null || layer.HasWarp
            || layer.VectorMask != null || layer.MaskTransform != null || layer.MaskFeather > 0) return null;
        var m = layer.Matrix;
        if (m.ScaleX != 1 || m.ScaleY != 1 || m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1
            || m.TransX != MathF.Round(m.TransX) || m.TransY != MathF.Round(m.TransY)) return null;
        var effects = layer.Effects?.Visible();
        return effects is { IsEmpty: false, GradientOverlay: null, ColorOverlay: null, InnerShadow: null, InnerGlow: null }
            ? effects : null;
    }

    internal static Patch? RenderPatch(Layer layer, SKRectI localTarget, LayerEffects effects, float scale, bool nativeGrid,
        StrokeBase? stroke = null)
    {
        var pixels = layer.Pixels!;
        var inset = Math.Max(2, effects.Margin());
        var full = new SKRectI(-inset, -inset, pixels.Width + inset, pixels.Height + inset);
        if ((long)full.Width * full.Height > MaxPixels || full.Width > Document.MaxSide * 2 || full.Height > Document.MaxSide * 2)
            return null;
        var mipWidth = full.Width;
        var mipHeight = full.Height;
        var stride = 1;
        while (scale < .5f && scale * stride <= .5f && mipWidth > 64 && mipHeight > 64)
        {
            stride *= 2;
            mipWidth /= 2;
            mipHeight /= 2;
        }
        // The output tile sees exactly the same source neighbourhood as the full renderer.
        // Include the extra source pixels needed for reduction and linear sampling of the mip.
        var halo = PatchHalo(effects) + (nativeGrid ? 0 : 2 * stride);
        var region = localTarget;
        region.Inflate(halo, halo);
        region = SKRectI.Intersect(region, full);
        if (region.IsEmpty) return null;
        // Reduction averages the same 2×2 blocks as the full image, starting at its top-left.
        region.Left = full.Left + (region.Left - full.Left) / stride * stride;
        region.Top = full.Top + (region.Top - full.Top) / stride * stride;
        region.Right = Math.Min(full.Right, full.Left + (region.Right - full.Left + stride - 1) / stride * stride);
        region.Bottom = Math.Min(full.Bottom, full.Top + (region.Bottom - full.Top + stride - 1) / stride * stride);
        if (region.Width < stride || region.Height < stride) return null;
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.EffectPatch();
#endif
        var image = RenderArea(pixels, layer.RenderMask(), effects, layer.FillOpacity, region);
        try
        {
            if (stroke?.Effects == effects && stroke.FillOpacity == layer.FillOpacity)
                stroke.Apply(layer, image, region, localTarget, full);
            for (var step = stride; step > 1; step /= 2)
            {
                var half = Pixels.Halve(image);
                image.Dispose();
                image = half;
            }
            var offset = new SKPointI((region.Left - full.Left) / stride, (region.Top - full.Top) / stride);
            if (nativeGrid) return new(layer.Id, image, inset, offset, new SKSize(1, 1));

            // The scaled draw must have the same origin and dimensions as a full effect draw.
            // Translating a cropped image changes Skia's fractional sampling rounding.
            // Only the part this view can sample has been rendered into this bitmap.
            var positioned = Pixels.NewLike(image, mipWidth, mipHeight);
            try
            {
                DocumentRenderer.CopyRows(image, SKPointI.Empty, positioned,
                    SKRectI.Create(offset.X, offset.Y, image.Width, image.Height));
#if CANVAS_PERFORMANCE
                CanvasPerformanceTrace.SurfaceAllocated("effect view sampling grid", positioned);
#endif
            }
            catch { positioned.Dispose(); throw; }
            image.Dispose();
            return new(layer.Id, positioned, inset, SKPointI.Empty, new SKSize((float)full.Width / mipWidth, (float)full.Height / mipHeight));
        }
        catch { image.Dispose(); throw; }
    }

    /// <summary>Renders the effects without the cache. <paramref name="visible"/> should come from <see cref="LayerEffects.Visible"/>.</summary>
    public static (SKBitmap Image, int Inset)? Render(SKBitmap pixels, SKBitmap? mask, LayerEffects visible, double fillOpacity = 1)
    {
        var inset = visible.Margin();
        if (inset == 0) inset = 2;
        long width = pixels.Width + inset * 2L, height = pixels.Height + inset * 2L;
        if (width * height > MaxPixels || width > Document.MaxSide * 2 || height > Document.MaxSide * 2) return null;
        return (RenderArea(pixels, mask, visible, fillOpacity, new SKRectI(-inset, -inset,
            pixels.Width + inset, pixels.Height + inset)), inset);
    }

    private static SKBitmap RenderArea(SKBitmap pixels, SKBitmap? mask, LayerEffects visible, double fillOpacity, SKRectI area)
    {
        var width = area.Width;
        var height = area.Height;
        var x = -area.Left;
        var y = -area.Top;
        var result = Pixels.NewLike(pixels, width, height);
        using var canvas = new SKCanvas(result);

        // The layer as it is shown: its pixels through its mask, with room around them for the effects.
        using var shown = Pixels.NewLike(pixels, width, height);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.SurfaceAllocated("effect result", result);
        CanvasPerformanceTrace.SurfaceAllocated("effect masked source", shown);
#endif
        using (var shownCanvas = new SKCanvas(shown))
        {
            shownCanvas.DrawBitmap(pixels, x, y);
            if (mask != null)
            {
                using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
                shownCanvas.DrawBitmap(mask, SKRect.Create(x, y, pixels.Width, pixels.Height), keep);
            }
        }
        // Its coverage: white where the layer is.
        using var coverage = Pixels.NewMask((int)width, (int)height);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.SurfaceAllocated("effect coverage", coverage);
#endif
        using (var coverageCanvas = new SKCanvas(coverage)) coverageCanvas.DrawBitmap(shown, 0, 0);

        if (visible.Shadow is { Opacity: > 0 } shadow)
        {
            var (dx, dy) = shadow.Offset;
            using var moved = Shifted(coverage, (float)dx, (float)dy, (float)(shadow.Blur / 2));
            Tint(canvas, moved, shadow.Color, shadow.Opacity, pixels.ColorSpace);
        }
        if (visible.OuterGlow is { Opacity: > 0, Size: > 0 } glow)
        {
            // The shape softened on every side, with the shape itself cut out so a translucent layer is not lit from
            // behind by its own glow. Half the size as sigma puts the visible edge of the glow about a size away.
            using var soft = Shifted(coverage, 0, 0, (float)(glow.Size / 2));
            using (var cutCanvas = new SKCanvas(soft))
            using (var cut = new SKPaint { BlendMode = SKBlendMode.DstOut })
                cutCanvas.DrawBitmap(coverage, 0, 0, cut);
            Tint(canvas, soft, glow.Color, glow.Opacity, pixels.ColorSpace);
        }
        // An outside stroke sits behind the layer's own pixels; an inside one is drawn over them, or the pixels would
        // simply cover it.
        var stroke = visible.Stroke is { Size: > 0, Opacity: > 0 } s ? s : null;
        if (stroke is { Inside: false })
        {
            using var ring = Ring(coverage, stroke);
            Tint(canvas, ring, stroke.Color, stroke.Opacity, pixels.ColorSpace);
        }
        if (visible.GradientOverlay is { Opacity: > 0 } gradient)
        {
            var radians = gradient.Angle * Math.PI / 180;
            var dx = (float)Math.Cos(radians); var dy = (float)-Math.Sin(radians);
            var center = new SKPoint(x + pixels.Width / 2f, y + pixels.Height / 2f);
            var reach = (Math.Abs(dx) * pixels.Width + Math.Abs(dy) * pixels.Height) / 2f * (float)(gradient.Scale / 100);
            var colors = (gradient.Ramp ?? GradientRamp.Between(gradient.StartColor, gradient.EndColor)).ShaderColors(gradient.Reverse);
            using var shader = gradient.Radial
                ? Pixels.WorkingGradient(shown, center, center, colors, true, Math.Max(pixels.Width, pixels.Height) / 2f * (float)(gradient.Scale / 100))
                : Pixels.WorkingGradient(shown, new SKPoint(center.X - dx * reach, center.Y - dy * reach), new SKPoint(center.X + dx * reach, center.Y + dy * reach), colors, false);
            using var shownCanvas = new SKCanvas(shown);
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.SrcATop, Color = SKColors.White.WithAlpha((byte)Math.Round(gradient.Opacity * 255)) };
            shownCanvas.DrawRect(SKRect.Create(0, 0, shown.Width, shown.Height), paint);
        }
        if (visible.ColorOverlay is { Opacity: > 0 } overlay)
        {
            MixColorOverlay(shown, overlay, fillOpacity);
            canvas.DrawBitmap(shown, 0, 0);
        }
        else using (var fill = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(fillOpacity, 0, 1) * 255)) })
            canvas.DrawBitmap(shown, 0, 0, fill);
        if (visible.InnerGlow is { Opacity: > 0, Size: > 0 } innerGlow)
        {
            // The shape softened inward: what is inside it but not inside its blurred self, kept to the layer's own shape.
            using var soft = Shifted(coverage, 0, 0, (float)(innerGlow.Size / 2));
            using var inside = Pixels.NewMask((int)width, (int)height);
            using (var insideCanvas = new SKCanvas(inside))
            {
                insideCanvas.DrawBitmap(coverage, 0, 0);
                using var cut = new SKPaint { BlendMode = SKBlendMode.DstOut };
                insideCanvas.DrawBitmap(soft, 0, 0, cut);
            }
            Tint(canvas, inside, innerGlow.Color, innerGlow.Opacity, pixels.ColorSpace);
        }
        if (visible.InnerShadow is { Opacity: > 0 } inner)
        {
            // What lies outside the layer, moved and softened, kept to the layer's own shape.
            var (dx, dy) = inner.Offset;
            using var moved = Shifted(coverage, (float)dx, (float)dy, (float)(inner.Blur / 2));
            using var inside = Pixels.NewMask((int)width, (int)height);
            using (var insideCanvas = new SKCanvas(inside))
            {
                insideCanvas.DrawBitmap(coverage, 0, 0);
                using var cut = new SKPaint { BlendMode = SKBlendMode.DstOut };
                insideCanvas.DrawBitmap(moved, 0, 0, cut);
            }
            Tint(canvas, inside, inner.Color, inner.Opacity, pixels.ColorSpace);
        }
        if (stroke is { Inside: true })
        {
            using var ring = Ring(coverage, stroke);
            Tint(canvas, ring, stroke.Color, stroke.Opacity, pixels.ColorSpace);
        }
        canvas.Flush();
        return result;
    }

    private static unsafe void MixColorOverlay(SKBitmap image, ColorOverlayEffect overlay, double fillOpacity)
    {
        var color = new SKColor(overlay.Color); var amount = (float)Math.Clamp(overlay.Opacity, 0, 1);
        var keep = (float)Math.Clamp(fillOpacity, 0, 1) * (1 - amount); var data = (byte*)image.GetPixels();
        if (image.ColorType == SKColorType.RgbaF32)
        {
            var pixels = new PixelBuffer(image);
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var p = pixels.Read(x, y);
                pixels.Write(x, y, new(p.R * keep + color.Red / 255f * p.A * amount,
                    p.G * keep + color.Green / 255f * p.A * amount, p.B * keep + color.Blue / 255f * p.A * amount, p.A * (keep + amount)));
            }
            return;
        }
        for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++)
        {
            var p = data + (long)y * image.RowBytes + x * 4; var alpha = p[3];
            p[0] = (byte)Math.Clamp(p[0] * keep + color.Red * alpha / 255f * amount + .5f, 0, 255);
            p[1] = (byte)Math.Clamp(p[1] * keep + color.Green * alpha / 255f * amount + .5f, 0, 255);
            p[2] = (byte)Math.Clamp(p[2] * keep + color.Blue * alpha / 255f * amount + .5f, 0, 255);
            p[3] = (byte)Math.Clamp(alpha * (keep + amount) + .5f, 0, 255);
        }
    }

    /// <summary>The coverage moved by an offset and softened by a Gaussian of <paramref name="sigma"/>.</summary>
    private static SKBitmap Shifted(SKBitmap coverage, float dx, float dy, float sigma)
    {
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var moved = Pixels.NewMask(coverage.Width, coverage.Height);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.SurfaceAllocated("effect shifted coverage", moved);
#endif
        using var canvas = new SKCanvas(moved);
        using var paint = new SKPaint();
        if (sigma > 0.01f) paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
        canvas.DrawBitmap(coverage, dx, dy, paint);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted(dx == 0 && dy == 0 ? "stationary blur" : "offset blur", started);
#endif
        return moved;
    }

    /// <summary>
    /// Where a stroke lands: the shape grown (or shrunk) by its size, less the shape itself. A square reach, not a
    /// round one: a round one eats into the corners of a rectangle, which reads as a wobbly edge.
    /// </summary>
    private static SKBitmap Ring(SKBitmap coverage, StrokeEffect stroke)
    {
        var reach = Math.Max(1, (int)Math.Round(stroke.Size));
        var ring = Pixels.NewMask(coverage.Width, coverage.Height);
        using var canvas = new SKCanvas(ring);
        using var cut = new SKPaint { BlendMode = SKBlendMode.DstOut };
        if (stroke.Inside)
        {
            using var shrink = SKImageFilter.CreateErode(reach, reach);
            cut.ImageFilter = shrink;
            canvas.DrawBitmap(coverage, 0, 0);
            canvas.DrawBitmap(coverage, 0, 0, cut);
        }
        else
        {
            using var grow = SKImageFilter.CreateDilate(reach, reach);
            using var grown = new SKPaint { ImageFilter = grow };
            canvas.DrawBitmap(coverage, 0, 0, grown);
            canvas.DrawBitmap(coverage, 0, 0, cut);
        }
        return ring;
    }

    /// <summary>Fills the coverage with a color at an opacity.</summary>
    private static void Tint(SKCanvas canvas, SKBitmap coverage, uint color, double opacity, SKColorSpace? workingSpace)
    {
        var tint = new SKColor(color).WithAlpha((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255));
        // The color-filter overload only accepts implicit sRGB. Compose shaders so wide-gamut
        // working colors survive, and the coverage still supplies exactly the mask's alpha.
        using var mask = coverage.ToShader();
        using var colorShader = SKShader.CreateColor((SKColorF)tint, workingSpace);
        using var shader = SKShader.CreateBlend(SKBlendMode.SrcIn, mask, colorShader);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(SKRect.Create(0, 0, coverage.Width, coverage.Height), paint);
    }
}
