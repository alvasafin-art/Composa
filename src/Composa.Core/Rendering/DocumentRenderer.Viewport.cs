using Composa.ColorManagement;
using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

public static partial class DocumentRenderer
{
    internal sealed record ViewportNode(Layer Layer, SKBitmap? Mask = null, ViewportNode[]? Children = null,
        (SKBitmap Image, int Inset)? Effect = null);

    /// <summary>An immutable draw list for the presentation thread. It borrows committed bitmaps.</summary>
    public sealed class ViewportScene
    {
        private readonly ViewportNode[] layers;
        private readonly RenderView view;

        internal ViewportScene(ViewportNode[] layers, RenderView view) => (this.layers, this.view) = (layers, view);

        public void Draw(SKCanvas canvas)
        {
            canvas.Save();
            if (view.OutputOffset is { } offset) canvas.Translate(offset);
            canvas.Scale(view.Scale);
            if (view.OutputOffset == null) canvas.Translate(-view.Origin.X, -view.Origin.Y);
            DrawNodes(layers, canvas);
            canvas.Restore();
        }

        private static void DrawNodes(ViewportNode[] nodes, SKCanvas canvas)
        {
            foreach (var node in nodes)
            {
                var layer = node.Layer;
                if (node.Children == null && node.Mask == null)
                {
                    DrawPixels(layer, canvas, layer.Opacity, layer.Blend,
                        node.Effect is { } effect ? new EffectImage(effect.Image, effect.Inset) : null, highPrecision: false);
                    continue;
                }
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha(ToByte(layer.Opacity)), BlendMode = layer.Blend.ToSkia() };
                canvas.SaveLayer(paint);
                if (node.Children is { } children) DrawNodes(children, canvas);
                else
                {
                    DrawPixels(layer, canvas, 1, BlendMode.Normal, effects: null, highPrecision: false);
                    DrawRasterMask(layer, canvas, node.Mask!);
                }
                canvas.Restore();
            }
        }
    }

    internal static ViewportScene? CaptureViewport(Document document, SKSizeI size, RenderView view)
    {
        if (document.WorkingDepth != ColorDepth.Eight || !document.Profile.Equals(RgbProfile.Srgb)) return null;
        List<ViewportNode> visible = [];
        long textureBytes = 0;
        var visibleBounds = SKRect.Create(view.Origin.X, view.Origin.Y, size.Width / view.Scale, size.Height / view.Scale);
        visibleBounds.Inflate(Tile.SamplingFringe / view.Scale, Tile.SamplingFringe / view.Scale);
        return Collect(document.Layers, visible, 0) && visible.Count > 0 ? new ViewportScene([.. visible], view) : null;

        bool Collect(IReadOnlyList<Layer> nodes, List<ViewportNode> destination, int depth)
        {
            if (depth >= 8) return false; // Bound simultaneously open GPU group surfaces.
            foreach (var layer in nodes)
            {
                if (layer.Clipped) return false;
                if (!layer.Visible || layer.Opacity <= 0) continue;
                if (!layer.BlendIf.IsIdentity || layer.Blend.IsCustom()) return false;
                if (layer.IsGroup)
                {
                    if (layer.Mask != null || layer.VectorMask != null) return false;
                    if (layer.Blend == BlendMode.PassThrough)
                    {
                        if (layer.Opacity < 1 || !Collect(layer.Children, destination, depth + 1)) return false;
                    }
                    else
                    {
                        List<ViewportNode> children = [];
                        if (!Collect(layer.Children, children, depth + 1)) return false;
                        if (children.Count != 0)
                            destination.Add(new(new Layer { Opacity = layer.Opacity, Blend = layer.Blend }, Children: [.. children]));
                    }
                    continue;
                }
                if (layer.Kind != LayerKind.Raster || layer.HasWarp || layer.HasSmartFilterMask) return false;
                if (layer.Pixels is not { } pixels) continue;
                if (layer.Transform.Distort != null)
                {
                    var source = SKRect.Create(pixels.Width, pixels.Height);
                    source.Inflate(layer.EffectMargin, layer.EffectMargin);
                    if (!Geometry.HasProjectiveCoverage(layer.Matrix, source)) return false;
                }
                if (!visibleBounds.IntersectsWith(layer.VisibleBounds)) continue;
                if (pixels.ColorType != SKColorType.Rgba8888 || pixels.AlphaType != SKAlphaType.Premul
                    || !RgbProfile.SameSpace(pixels.ColorSpace, document.Profile.Space) || Pixels.IsLive(pixels)) return false;
                // Directly aligned masks require neither a full-plane resample nor a new cache on input.
                if (layer.VectorMask != null || layer.Mask is { } sourceMask &&
                    (layer.MaskTransform != null || layer.MaskDensity != 1 || layer.MaskFeather != 0 || !layer.MaskLinked ||
                        sourceMask.Width != pixels.Width || sourceMask.Height != pixels.Height || Pixels.IsLive(sourceMask))) return false;
                var mask = layer.RenderMask();
                (SKBitmap Image, int Inset)? effect = null;
                if (layer.Effects != null)
                {
                    // Building an effect on the input thread would bring back the hitch this path avoids.
                    effect = LayerEffectsRenderer.Cached(pixels, mask, layer.Effects, layer.FillOpacity, existingOnly: true);
                    if (effect == null) return false;
                    mask = null; // The effect image already contains the mask.
                }

                // DrawBitmap selects this same pyramid level. Bound texture uploads on shared-memory devices too.
                var scale = ScaleOf(layer.Matrix) * view.Scale;
                if (!double.IsFinite(scale) || scale <= 0) return false;
                var width = effect?.Image.Width ?? pixels.Width;
                var height = effect?.Image.Height ?? pixels.Height;
                var levels = scale < .5 ? (int)Math.Floor(Math.Log2(1 / scale)) : 0;
                while (levels-- > 0 && width > 64 && height > 64)
                {
                    width /= 2;
                    height /= 2;
                }
                textureBytes += (long)width * height * (mask != null ? 5 : 4);
                if (textureBytes > 128 * 1024 * 1024 || width > 8192 || height > 8192) return false;
                destination.Add(new(layer.Clone(), mask, Effect: effect));
            }
            return true;
        }
    }
}
