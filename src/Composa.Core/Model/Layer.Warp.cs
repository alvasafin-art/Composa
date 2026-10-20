using Composa.Rendering;
using SkiaSharp;

namespace Composa.Model;

public sealed partial class Layer
{
    /// <summary>Immutable deformation of the retained source grid, evaluated after Smart Filters and before styles.</summary>
    public WarpMesh? Warp { get; set; }
    private sealed class WarpCache
    {
        public readonly Lock Gate = new();
        public sealed record Entry(WarpMesh Mesh, WarpGeometry Geometry, SKImage Source, SKImage? Original, SKImage? Mask, double Scale, WarpedPixels Appearance, WarpedPixels? Before);
        public Entry? Native, Preview;
    }
    private WarpCache warpCache = new();
    private Layer? warpMaskSource;

    public bool HasWarp => Pixels != null && Warp is { IsIdentity: false };
    internal SKRect WarpBounds
    {
        get
        {
            if (Pixels == null) return SKRect.Empty;
            if (!HasWarp) return new(0, 0, Pixels.Width, Pixels.Height);
            var bounds = GeometryForWarp(Warp!).Bounds;
            if (FilterSource != null) bounds.Offset(FilterPaddingX, FilterPaddingY);
            return bounds;
        }
    }

    /// <summary>Borrowed immutable appearance node for the renderer. Independent masks remain in document space;
    /// linked raster/vector coverage follows the material, and styles are applied to its deformed silhouette.</summary>
    public Layer WarpAppearance(double scale = 1)
    {
        if (!HasWarp) return this;
        var source = Pixels!;
        var sourceImage = Rendering.Pixels.ImageOf(source);
        var originalImage = HasSmartFilterMask ? Rendering.Pixels.ImageOf(FilterSource!) : null;
        WarpedPixels appearance; WarpedPixels? before;
        lock (warpCache.Gate)
        {
            var geometry = GeometryForWarp(Warp!);
            SKBitmap? mask;
            if ((Mask == null || MaskLinked && MaskFollowsWarp) && (VectorMask == null || VectorMaskFollowsWarp)) mask = RenderMask();
            else
            {
                // Coverage added after Warp (or independent of the layer) must not enter its source texture.
                warpMaskSource ??= new Layer();
                warpMaskSource.Pixels = Pixels; warpMaskSource.Transform = Transform;
                warpMaskSource.Mask = MaskLinked && MaskFollowsWarp ? Mask : null;
                warpMaskSource.MaskTransform = MaskTransform; warpMaskSource.MaskEnabled = MaskEnabled; warpMaskSource.MaskDefault = MaskDefault;
                warpMaskSource.MaskDensity = MaskDensity; warpMaskSource.MaskFeather = MaskFeather;
                warpMaskSource.VectorMask = VectorMaskFollowsWarp ? VectorMask : null; warpMaskSource.VectorMaskEnabled = VectorMaskEnabled;
                mask = warpMaskSource.RenderMask();
            }
            var maskImage = mask == null ? null : Rendering.Pixels.ImageOf(mask);
            var cached = scale == 1 ? warpCache.Native : warpCache.Preview;
            if (cached != null && cached.Mesh.Equals(Warp) && ReferenceEquals(cached.Geometry, geometry) && ReferenceEquals(cached.Source, sourceImage)
                && ReferenceEquals(cached.Original, originalImage) && ReferenceEquals(cached.Mask, maskImage) && cached.Scale == scale)
            { appearance = cached.Appearance; before = cached.Before; }
            else
            {
                var origin = FilterSource == null ? SKPoint.Empty : new SKPoint(-FilterPaddingX, -FilterPaddingY);
                SKMatrix? coverage = mask == null ? null : SKMatrix.CreateScale((float)source.Width / mask.Width, (float)source.Height / mask.Height)
                    .PostConcat(SKMatrix.CreateTranslation(origin.X, origin.Y));
                appearance = WarpRenderer.RenderRegion(source, geometry, origin, mask, coverage, scale);
                before = null;
                try
                {
                    if (originalImage != null)
                    {
                        if (ReferenceEquals(source, FilterSource) && FilterPaddingX == 0 && FilterPaddingY == 0) before = appearance;
                        else
                        {
                            var raw = FilterSource!; var extent = Rendering.Pixels.ContentBounds(raw);
                            var region = extent.IsEmpty ? new SKRect(0, 0, raw.Width, raw.Height) : new SKRect(extent.Left, extent.Top, extent.Right, extent.Bottom);
                            // A black filter mask must reproduce the original warp independently of the filter's
                            // growing border/tessellation. Both textures still land in the same output pixel grid.
                            var originalGeometry = new WarpGeometry(Warp!, raw.Width, raw.Height, region);
                            before = WarpRenderer.RenderRegion(raw, originalGeometry, SKPoint.Empty, mask, coverage, scale, appearance.Bounds);
                        }
                    }
                }
                catch { appearance.Dispose(); throw; }
                // Render bands may still read the preceding generation. Its bitmap is collected only after the
                // last borrowed appearance node releases it, never disposed while another band is drawing.
                var next = new WarpCache.Entry(Warp!, geometry, sourceImage, originalImage, maskImage, scale, appearance, before);
                if (scale == 1) warpCache.Native = next;
                else
                {
                    warpCache.Preview = next;
                    if (warpCache.Native?.Mesh.Equals(Warp) == false) warpCache.Native = null;
                }
            }
        }
        var proxy = Clone(); proxy.Warp = null; proxy.Pixels = appearance.Bitmap;
        var frame = SKMatrix.CreateScale((float)appearance.Bounds.Width / appearance.Bitmap.Width, (float)appearance.Bounds.Height / appearance.Bitmap.Height)
            .PostConcat(SKMatrix.CreateTranslation(appearance.Bounds.Left, appearance.Bounds.Top)).PostConcat(WarpMatrix);
        proxy.Transform = LayerTransform.FromMatrix(frame, appearance.Bitmap.Width, appearance.Bitmap.Height);
        proxy.FilterSource = null; proxy.SmartFilters = []; proxy.SmartFilterMask = null; proxy.FilterPaddingX = proxy.FilterPaddingY = 0;
        proxy.SmartFilterMaskPlacement = null;
        if (before != null)
        {
            proxy.FilterSource = before.Bitmap; proxy.SmartFilters = SmartFilters; proxy.SmartFilterMask = SmartFilterMask;
            proxy.SmartFilterMaskX = proxy.SmartFilterMaskY = 0;
            if (!proxy.Matrix.TryInvert(out var inverse)) throw new InvalidOperationException("The Warp appearance transform is singular.");
            proxy.SmartFilterMaskPlacement = SmartFilterMaskMatrix.PostConcat(inverse);
        }
        proxy.Shape = null; proxy.Text = null; proxy.SmartObject = null; proxy.VectorMask = null;
        if (VectorMask != null && !VectorMaskFollowsWarp)
        {
            double X(double value) => (value * Pixels!.Width - (FilterSource == null ? 0 : FilterPaddingX) - appearance.Bounds.Left) / appearance.Bounds.Width;
            double Y(double value) => (value * Pixels!.Height - (FilterSource == null ? 0 : FilterPaddingY) - appearance.Bounds.Top) / appearance.Bounds.Height;
            proxy.VectorMask = VectorMask with { Nodes = VectorMask.Nodes.Select(n => new BezierNode(X(n.X), Y(n.Y), X(n.InX), Y(n.InY), X(n.OutX), Y(n.OutY))).ToArray() };
        }
        if (scale != 1 && Effects is { } effects) proxy.Effects = ScaleWarpEffects(effects, (double)appearance.Bitmap.Width / appearance.Bounds.Width);
        if (MaskLinked && MaskFollowsWarp) proxy.Mask = null;
        else if (Mask != null) { proxy.MaskLinked = false; proxy.PlaceMask(MaskMatrix); }
        return proxy;
    }

    private static LayerEffects ScaleWarpEffects(LayerEffects effects, double scale) => effects with
    {
        Stroke = effects.Stroke is { } stroke ? stroke with { Size = stroke.Size * scale } : null,
        Shadow = effects.Shadow is { } shadow ? shadow with { Distance = shadow.Distance * scale, Blur = shadow.Blur * scale } : null,
        InnerShadow = effects.InnerShadow is { } inner ? inner with { Distance = inner.Distance * scale, Blur = inner.Blur * scale } : null,
        OuterGlow = effects.OuterGlow is { } outer ? outer with { Size = outer.Size * scale } : null,
        InnerGlow = effects.InnerGlow is { } glow ? glow with { Size = glow.Size * scale } : null
    };

    internal Layer WarpAppearanceForView(float viewScale)
    {
        if (!HasWarp) return this;
        var matrix = WarpMatrix;
        // A projective transform can magnify an arbitrarily small region near its horizon. Keep its native cache.
        if (matrix.Persp0 != 0 || matrix.Persp1 != 0) return WarpAppearance();
        double a = matrix.ScaleX, b = matrix.SkewX, c = matrix.SkewY, d = matrix.ScaleY;
        var sum = a * a + b * b + c * c + d * d; var determinant = a * d - b * c;
        var magnification = Math.Sqrt((sum + Math.Sqrt(Math.Max(0, sum * sum - 4 * determinant * determinant))) / 2) * viewScale;
        var scale = 1.0;
        while (scale / 2 >= magnification && scale * Math.Max(Pixels!.Width, Pixels.Height) > 2) scale /= 2;
        return WarpAppearance(scale);
    }

    /// <summary>Coverage for picking the actual visible material, excluding styles such as its cast shadow.</summary>
    public float HitCoverage(SKPoint documentPoint, float viewScale = 1)
    {
        var appearance = WarpAppearanceForView(viewScale);
        if (appearance.Pixels == null || !appearance.Matrix.TryInvert(out var inverse)) return 0;
        var point = inverse.MapPoint(documentPoint); var x = (int)Math.Floor(point.X); var y = (int)Math.Floor(point.Y);
        var material = appearance.RenderFilterPixels()!;
        if (x < 0 || y < 0 || x >= material.Width || y >= material.Height) return 0;
        var alpha = Text != null && !HasWarp ? 1 : new PixelBuffer(material).Read(x, y).A;
        if (appearance.RenderMask() is { } mask)
        {
            var mx = Math.Min(mask.Width - 1, (int)(point.X * mask.Width / material.Width));
            var my = Math.Min(mask.Height - 1, (int)(point.Y * mask.Height / material.Height));
            alpha *= mask.GetPixel(mx, my).Alpha / 255f;
        }
        return alpha;
    }
}
