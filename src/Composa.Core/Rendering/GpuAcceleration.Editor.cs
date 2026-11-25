using Composa.Model;
using Composa.Painting;
using SkiaSharp;

namespace Composa.Rendering;

public static partial class GpuAcceleration
{
    internal static bool EditorEnabled => editorEnabled;
    internal static bool CanCompositeGpu => editorEnabled && RequestEditorDevice() is { CompositeAvailable: true };

    internal static bool TryComposite(SKBitmap destination, SKRectI area, IReadOnlyList<GpuCompositeLayer> layers) =>
        editorEnabled && ReadyDevice is { CompositeAvailable: true } backend && backend.TryComposite(destination, area, layers, retainInputs: true);

    internal static bool CompositeInputsReady(IReadOnlyList<GpuCompositeLayer> layers) =>
        ReadyDevice is { CompositeAvailable: true } backend && backend.CompositeInputsReady(layers);

    public static void ReleaseCanvasCache()
    {
        if (ReadyDevice is { } backend) _ = Task.Run(backend.ClearCompositeInputs);
    }

    internal static bool TryBrush(SKBitmap source, SKBitmap destination, SKRectI area, StrokeCoverage coverage, SKColorF color, float opacity)
    {
        var pixels = (long)area.Width * area.Height;
        if (!editorEnabled || pixels is < 100_000 or > 2_000_000 || RequestEditorDevice() is not { PaintAvailable: true } backend)
            return false;
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        if (!backend.TryPaint(source, destination, area, coverage, color, opacity)) return false;
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("GPU brush pixels", started);
#endif
        return true;
    }

    internal static bool TryLayerEffectBlur(SKBitmap source, SKBitmap destination, float dx, float dy, float sigma)
    {
        if (!editorEnabled || (long)source.Width * source.Height < 100_000
            || sigma is not (>= 5 and <= 128) || !IsWholePixel(dx) || !IsWholePixel(dy)
            || RequestEditorDevice() is not { AlphaBlurAvailable: true } backend) return false;
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        if (backend.TryAlphaBlur(source, destination, RoundOffset(dx), RoundOffset(dy), AlphaWindow(sigma)))
        {
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.StageCompleted("GPU effect blur", started);
#endif
            return true;
        }
        destination.Erase(SKColors.Transparent);
        return false;
    }

    internal static bool TryStrokeRing(SKBitmap source, SKBitmap destination, int reach, bool inside)
    {
        if (!editorEnabled || reach < 9 || (long)source.Width * source.Height < 200_000
            || RequestEditorDevice() is not { StrokeRingAvailable: true } backend) return false;
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        if (!backend.TryStrokeRing(source, destination, reach, inside)) return false;
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("GPU layer stroke", started);
#endif
        return true;
    }

    private static bool ValidateStrokeRing(OpenClDevice backend)
    {
        using var source = Pixels.NewMask(73, 91);
        using var output = Pixels.NewLike(source);
        new Random(45).NextBytes(source.GetPixelSpan());
        foreach (var inside in new[] { false, true })
        foreach (var reach in new[] { 9, 30 })
        {
            using var reference = Pixels.NewLike(source);
            LayerEffectsRenderer.DrawStrokeRing(source, reference, reach, inside);
            if (!backend.TryStrokeRing(source, output, reach, inside)
                || !reference.GetPixelSpan().SequenceEqual(output.GetPixelSpan())) return false;
        }
        return true;
    }

    internal static bool TryCoverage(StrokeCoverage coverage, SKRectI area, SKPoint center, float radius, float hardness,
        float[] falloff, float flow, out bool changed, GpuBrushPixels? pixels = null)
    {
        changed = false;
        if (!editorEnabled || (long)area.Width * area.Height is < 100_000 or > 2_000_000
            || radius <= 2 || radius * (1 - hardness) < 1
            || RequestEditorDevice() is not { CoverageAvailable: true } backend
            || pixels.HasValue && !backend.PaintAvailable) return false;
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        if (!backend.TryBrushCoverage(coverage, area, center, radius, hardness, falloff, flow, out changed, pixels)) return false;
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted(pixels.HasValue ? "GPU brush coverage and pixels" : "GPU brush coverage", started);
#endif
        return true;
    }

    private static int RoundOffset(float value) => (int)MathF.Round(value);

    // The shadow dial can produce tiny trigonometric residuals at cardinal angles.
    private static bool IsWholePixel(float value) => float.IsFinite(value) && MathF.Abs(value - RoundOffset(value)) < 0.00001f;

    // This is the box-filter width used by the raster Skia blur at these sigmas.
    private static int AlphaWindow(float sigma) => (int)Math.Floor(sigma * 3 * Math.Sqrt(2 * Math.PI) / 4 + .5);

    private static bool ValidateAlphaBlur(OpenClDevice backend)
    {
        using var input = Pixels.NewMask(311, 279);
        using var output = Pixels.NewLike(input);
        using var reference = Pixels.NewLike(input);
        new Random(31).NextBytes(input.GetPixelSpan());
        foreach (var (sigma, dx, dy) in new[] { (5f, 0, 0), (14f, 0, 0), (41f, -9, 43), (128f, 11, -16) })
        {
            reference.Erase(SKColors.Transparent);
            LayerEffectsRenderer.DrawShifted(input, reference, dx, dy, sigma);
            if (!backend.TryAlphaBlur(input, output, dx, dy, AlphaWindow(sigma))
                || !reference.GetPixelSpan().SequenceEqual(output.GetPixelSpan())) return false;
        }
        return true;
    }

    private static bool PaintReferenceMatches(OpenClDevice backend)
    {
        using var input = SampleRaster(137, 111, ColorDepth.Eight, 71);
        var settings = new BrushSettings { Size = 97, Opacity = .63, Flow = .71, Hardness = .54 };
        return BrushStroke.MatchesGpu(backend, input, settings, BrushMode.Paint)
            && BrushStroke.MatchesGpu(backend, input, settings, BrushMode.Erase);
    }

    private static bool CompositeReferenceMatches(OpenClDevice backend)
    {
        foreach (var depth in new[] { ColorDepth.Eight, ColorDepth.Sixteen })
        {
            using var bottom = SampleRaster(121, 87, depth, 13);
            using var top = SampleRaster(83, 73, depth, 54);
            using var expected = Pixels.NewLike(bottom);
            using var actual = Pixels.NewLike(bottom);
            using (var canvas = new SKCanvas(expected))
            {
                canvas.DrawImage(Pixels.ImageOf(bottom), 0, 0);
                canvas.DrawImage(Pixels.ImageOf(top), -12, 31);
            }
            GpuCompositeLayer[] layers = [new(bottom, bottom.Info.Rect, new(0, 0)), new(top, new(12, 0, 83, 56), new(0, 31))];
            if (!backend.TryComposite(actual, actual.Info.Rect, layers, retainInputs: false)
                || !actual.GetPixelSpan().SequenceEqual(expected.GetPixelSpan())) return false;
        }
        return true;
    }

    private static SKBitmap SampleRaster(int width, int height, ColorDepth depth, int seed)
    {
        var random = new Random(seed);
        var image = Pixels.NewColor(width, height, depth);
        var pixels = new PixelBuffer(image);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var alpha = random.NextSingle();
            pixels.Write(x, y, new(alpha * random.NextSingle(), alpha * random.NextSingle(), alpha * random.NextSingle(), alpha));
        }
        return image;
    }
}
