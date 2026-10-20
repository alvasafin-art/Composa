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
        public sealed record Entry(WarpMesh Mesh, SKImage Source, SKImage? Mask, double Scale, WarpedPixels Appearance);
        public Entry? Native, Preview;
    }
    private WarpCache warpCache = new();
    private Layer? warpMaskSource;

    public bool HasWarp => Pixels != null && Warp is { IsIdentity: false };
    internal SKRect WarpBounds => Pixels == null ? SKRect.Empty : HasWarp ? Warp!.Bounds(Pixels.Width, Pixels.Height) : new(0, 0, Pixels.Width, Pixels.Height);

    /// <summary>Borrowed immutable appearance node for the renderer. Independent masks remain in document space;
    /// linked raster/vector coverage follows the material, and styles are applied to its deformed silhouette.</summary>
    public Layer WarpAppearance(double scale = 1)
    {
        if (!HasWarp) return this;
        var source = RenderFilterPixels()!;
        var sourceImage = Rendering.Pixels.ImageOf(source);
        WarpedPixels appearance;
        lock (warpCache.Gate)
        {
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
            if (cached != null && cached.Mesh.Equals(Warp) && ReferenceEquals(cached.Source, sourceImage) && ReferenceEquals(cached.Mask, maskImage) && cached.Scale == scale) appearance = cached.Appearance;
            else
            {
                appearance = WarpRenderer.Render(source, Warp!, mask, scale);
                // Render bands may still read the preceding generation. Its bitmap is collected only after the
                // last borrowed appearance node releases it, never disposed while another band is drawing.
                var next = new WarpCache.Entry(Warp!, sourceImage, maskImage, scale, appearance);
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
            .PostConcat(SKMatrix.CreateTranslation(appearance.Bounds.Left, appearance.Bounds.Top)).PostConcat(Matrix);
        proxy.Transform = LayerTransform.FromMatrix(frame, appearance.Bitmap.Width, appearance.Bitmap.Height);
        proxy.FilterSource = null; proxy.SmartFilters = []; proxy.SmartFilterMask = null; proxy.FilterPaddingX = proxy.FilterPaddingY = 0;
        proxy.Shape = null; proxy.Text = null; proxy.SmartObject = null; proxy.VectorMask = null;
        if (VectorMask != null && !VectorMaskFollowsWarp)
        {
            double X(double value) => (value * Pixels!.Width - appearance.Bounds.Left) / appearance.Bounds.Width;
            double Y(double value) => (value * Pixels!.Height - appearance.Bounds.Top) / appearance.Bounds.Height;
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
        var matrix = Matrix;
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
