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
            try { made = Render(key.Pixels, key.Mask, key.Effects, key.FillOpacity); }
            finally { CanvasPerformanceTrace.EffectCompleted(key.Pixels, key.Mask, key.Effects, key.FillOpacity); }
#else
            made = Render(key.Pixels, key.Mask, key.Effects, key.FillOpacity);
#endif
            lock (Lru)
            {
                // Invalidation or clearing the cache can detach an in-flight request.
                // Its original callers can finish; the old image must not reenter the cache.
                if (made is { } result && InFlight.GetValueOrDefault(key) == request)
                {
                    var entry = new Entry(key, result.Image, result.Inset);
                    Cache.GetOrCreateValue(key.Pixels).Add(entry);
                    entry.Order = Lru.AddLast(new WeakReference<Entry>(entry));
                    Trim();
                }
            }
            return made;
        }
        finally
        {
            lock (Lru)
                if (InFlight.GetValueOrDefault(key) == request) InFlight.Remove(key);
        }
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

    internal sealed record Patch(Guid LayerId, SKBitmap Image, SKPointI Offset) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }

    // Four sigmas cover the CPU Gaussian sampling footprint with room for filtering
    // a subpixel shadow offset. Stroke morphology has its own, integral radius.
    internal static int PatchHalo(LayerEffects effects) => (int)Math.Ceiling(Math.Max(
        Math.Max(effects.Stroke?.Size ?? 0, (effects.OuterGlow?.Size ?? 0) * 2),
        (effects.Shadow?.Distance ?? 0) + (effects.Shadow?.Blur ?? 0) * 2)) + 4;

    /// <summary>Only unscaled raster content with spatially local effects takes the native-view patch path.</summary>
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

    internal static Patch? RenderPatch(Layer layer, SKRectI localTarget, LayerEffects effects)
    {
        var pixels = layer.Pixels!;
        var inset = Math.Max(2, effects.Margin());
        var full = new SKRectI(-inset, -inset, pixels.Width + inset, pixels.Height + inset);
        if ((long)full.Width * full.Height > MaxPixels) return null;
        // The output tile sees exactly the same source neighbourhood as the full renderer.
        // Add space for dilation/erosion and Skia's filter sampling on a fractional shadow offset.
        var halo = PatchHalo(effects);
        localTarget.Inflate(halo, halo);
        var region = SKRectI.Intersect(localTarget, full);
        if (region.IsEmpty) return null;
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.EffectPatch();
#endif
        return new(layer.Id, RenderArea(pixels, layer.RenderMask(), effects, layer.FillOpacity, region),
            new SKPointI(region.Left, region.Top));
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
        var moved = Pixels.NewMask(coverage.Width, coverage.Height);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.SurfaceAllocated("effect shifted coverage", moved);
#endif
        using var canvas = new SKCanvas(moved);
        using var paint = new SKPaint();
        if (sigma > 0.01f) paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
        canvas.DrawBitmap(coverage, dx, dy, paint);
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
