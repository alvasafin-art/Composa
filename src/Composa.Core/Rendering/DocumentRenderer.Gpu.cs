using Composa.ColorManagement;
using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

public static partial class DocumentRenderer
{
    internal static bool TryRenderGpu(Document document, SKBitmap target, SKRectI area, RenderView view, RenderOptions? options)
    {
        if (!GpuAcceleration.EditorEnabled || options?.EffectPatch != null || view.Scale != 1
            || !Whole(view.Origin.X) || !Whole(view.Origin.Y)) return false;
        var depth = document.WorkingDepth;
        var colorType = depth == ColorDepth.Sixteen ? SKColorType.RgbaF32 : SKColorType.Rgba8888;
        var pixelBytes = depth == ColorDepth.Sixteen ? 16 : 4;
        var pixelCount = (long)area.Width * area.Height;
        if (pixelCount < (depth == ColorDepth.Sixteen ? 720_000 : 400_000)
            || pixelCount * pixelBytes > OpenClDevice.MaxCompositeBytes) return false;
        var layers = new List<GpuCompositeLayer>();
        var documentX = (int)view.Origin.X + area.Left;
        var documentY = (int)view.Origin.Y + area.Top;
        var viewport = SKRectI.Create(documentX, documentY, area.Width, area.Height);
        if (!Collect(document.Layers) || layers.Count < 2) return false;
        // A small two-layer update is cheaper in Skia when the readback also needs a row copy.
        if (layers.Count == 2 && pixelCount < 1_000_000 && target.RowBytes != area.Width * target.BytesPerPixel) return false;
        if (!GpuAcceleration.CanCompositeGpu) return false;
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        if (target.ColorType == colorType && RgbProfile.SameSpace(target.ColorSpace, document.Profile.Space))
        {
            // If a driver fails during readback, the normal renderer overwrites the entire area.
            if (!GpuAcceleration.TryComposite(target, area, layers)) return false;
        }
        else
        {
            if (!GpuAcceleration.CompositeInputsReady(layers)) return false;
            using var scratch = Pixels.NewWorkingColor(area.Width, area.Height, depth, document.Profile);
            if (!GpuAcceleration.TryComposite(scratch, scratch.Info.Rect, layers)) return false;
            CopyRows(scratch, SKPointI.Empty, target, area);
        }
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("GPU canvas composite", started);
#endif
        return true;

        bool Collect(IReadOnlyList<Layer> nodes)
        {
            foreach (var original in nodes)
            {
                var layer = Resolve(original, options);
                if (layer.Clipped) return false;
                if (!layer.Visible || layer.Opacity <= 0) continue;
                if (layer.Opacity != 1 || !layer.BlendIf.IsIdentity || layer.Warp != null) return false;
                if (layer.IsGroup && layer.Blend == BlendMode.PassThrough && layer.Mask == null && layer.VectorMask == null)
                {
                    if (!Collect(layer.Children)) return false;
                    continue;
                }
                if (layer.Kind != LayerKind.Raster || layer.Blend != BlendMode.Normal
                    || layer.HasSmartFilterMask || layer.Transform.Distort != null) return false;
                if (layer.Pixels is not { } pixels) continue;
                if (Pixels.IsLive(pixels)) return false;
                var matrix = layer.Matrix;
                if (matrix != SKMatrix.CreateTranslation(matrix.TransX, matrix.TransY)
                    || !Whole(matrix.TransX) || !Whole(matrix.TransY)) return false;
                var x = (int)matrix.TransX;
                var y = (int)matrix.TransY;
                if (EffectsOf(layer, options) is { } effect)
                {
                    pixels = effect.Image;
                    x -= effect.Inset;
                    y -= effect.Inset;
                }
                else if (layer.Mask != null || layer.VectorMask != null || layer.FillOpacity != 1) return false;
                if (pixels.ColorType != colorType || pixels.AlphaType != SKAlphaType.Premul
                    || !RgbProfile.SameSpace(pixels.ColorSpace, document.Profile.Space)) return false;
                var overlap = Geometry.Intersect(viewport, SKRectI.Create(x, y, pixels.Width, pixels.Height));
                if (overlap.IsEmpty) continue;
                layers.Add(new(pixels, SKRectI.Create(overlap.Left - x, overlap.Top - y, overlap.Width, overlap.Height),
                    new(overlap.Left - documentX, overlap.Top - documentY)));
            }
            return true;
        }
    }

    private static bool Whole(float coordinate) => float.IsFinite(coordinate) && coordinate == MathF.Truncate(coordinate);
}
