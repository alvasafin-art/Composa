using System.Diagnostics;
using System.Security.Cryptography;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

Console.WriteLine($"Logical processors: {Environment.ProcessorCount}");
GpuAcceleration.Configure(filters: false, editor: false);

if (args is ["final-io", var fixtures, var output])
{
    FinalIo.Run(fixtures, output);
    return;
}

if (args is ["heavy-edits"] || args is ["heavy-edits", "short"])
{
    HeavyEdits.Run(shortRun: args.Length == 2);
    return;
}

using var mask = Pixels.NewMask(3840, 2160, 161);
var maskLayer = new Layer { Mask = mask, MaskFeather = 14 };
foreach (var density in new[] { 1.0, .72 })
{
    var first = Stopwatch.StartNew();
    var actual = AdjustMask(density);
    first.Stop();
    Report($"4K mask feather / density {density:F2}", () => AdjustMask(density), actual, first.Elapsed.TotalMilliseconds, ownsImage: true);
}

foreach (var depth in new[] { ColorDepth.Eight, ColorDepth.Sixteen })
{
    var document = new Document(3200, 2000) { Depth = depth };
    using var bitmap = Pixels.NewColor(3200, 2000, depth);
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(new SKColor(160, 59, 90, 160));
        using var paint = new SKPaint { Color = SKColors.SkyBlue };
        canvas.DrawOval(1200, 1200, 1000, 700, paint);
    }
    document.Layers.Add(Layer.Raster("Photo", bitmap));
    var adjustment = Layer.ForAdjustment(new InvertAdjustment());
    document.Layers.Add(adjustment);
    using var target = Pixels.NewColor(1600, 1000);
    foreach (var mode in new[] { "full", "opacity", "mask+opacity" })
    {
        adjustment.Opacity = mode == "full" ? 1 : .63;
        adjustment.Mask = mode == "mask+opacity" ? mask : null;
        var time = Stopwatch.StartNew();
        Draw();
        time.Stop();
        Report($"1600x1000 {depth} invert {mode}", () => { Draw(); return target; }, target, time.Elapsed.TotalMilliseconds);
    }
    void Draw() => DocumentRenderer.Render(document, target, target.Info.Rect, new RenderView(.5f, SKPoint.Empty));
}

foreach (var count in new[] { 8, 16, 24 })
{
    var document = new Document(1600, 1000);
    for (int i = 0; i < count; i++)
    {
        var pixels = Pixels.NewColor(420, 330);
        using var canvas = new SKCanvas(pixels);
        using var paint = new SKPaint { Color = new SKColor((byte)(223 - 4 * i), (byte)(52 + 4 * i), 142, 185) };
        canvas.DrawOval(205, 165, 185, 145, paint);
        document.Layers.Add(Layer.Raster($"Layer {i}", pixels, i * 38 % 1090, i * 53 % 650));
        document.Layers[^1].Effects = new LayerEffects { Shadow = new() { Blur = 20, Distance = 12 }, Stroke = new() { Size = 4 } };
    }
    using var target = Pixels.NewColor(1280, 800);
    LayerEffectsRenderer.ClearCache();
    var time = Stopwatch.StartNew();
    Draw();
    time.Stop();
    Report($"1280x800 {count} styled layers", () => { Draw(); return target; }, target, time.Elapsed.TotalMilliseconds);
    LayerEffectsRenderer.ClearCache();
    foreach (var layer in document.Layers) layer.Pixels!.Dispose();
    void Draw() => DocumentRenderer.Render(document, target, target.Info.Rect, new RenderView(.8f, SKPoint.Empty));
}

foreach (var depth in new[] { ColorDepth.Eight, ColorDepth.Sixteen })
{
    using var pixels = Pixels.NewColor(2560, 1440, depth);
    pixels.Erase(new SKColor(121, 204, 137, 190));
    var overlay = new LayerEffects { ColorOverlay = new() { Color = 0xFF4BC93C, Opacity = .71 } };
    var time = Stopwatch.StartNew();
    var first = DrawOverlay();
    time.Stop();
    Report($"2560x1440 {depth} color overlay", DrawOverlay, first, time.Elapsed.TotalMilliseconds, ownsImage: true);
    SKBitmap DrawOverlay() => LayerEffectsRenderer.Render(pixels, null, overlay, .74)!.Value.Image;
}

using (var input = Pixels.NewColor(1060, 700, ColorDepth.Sixteen))
{
    using (var canvas = new SKCanvas(input))
    {
        canvas.Clear(SKColors.CornflowerBlue);
        using var paint = new SKPaint { Color = SKColors.Beige };
        canvas.DrawOval(450, 270, 310, 165, paint);
    }
    foreach (var clamp in new[] { false, true })
    {
        var settings = new FilterSettings { Kind = FilterKind.MotionBlur, Radius = 42, Angle = 31, ClampEdges = clamp };
        var time = Stopwatch.StartNew();
        var first = DrawBlur();
        time.Stop();
        Report($"1060x700 RGB16 motion blur clamp={clamp}", DrawBlur, first, time.Elapsed.TotalMilliseconds, ownsImage: true, repeats: 9);
        SKBitmap DrawBlur() => ImageFilters.Run(input, settings).Result;
    }
}

SKBitmap AdjustMask(double density)
{
    maskLayer.MaskDensity = density;
    // A fresh layer reproduces a cold render after editing the mask properties.
    var layer = maskLayer.Clone();
    return layer.RenderRasterMask()!;
}

static void Report(string name, Func<SKBitmap> render, SKBitmap first, double firstMs, bool ownsImage = false, int repeats = 21)
{
    var hash = Convert.ToHexString(SHA256.HashData(first.GetPixelSpan()));
    if (ownsImage) first.Dispose();
    for (int i = 0; i < 4; i++)
    {
        var image = render();
        if (ownsImage) image.Dispose();
    }
#if CANVAS_PERFORMANCE
    CanvasPerformanceTrace.Reset();
#endif
    var times = new List<double>();
    for (int i = 0; i < repeats; i++)
    {
        var begin = Stopwatch.GetTimestamp();
        var image = render();
        times.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
        if (ownsImage) image.Dispose();
    }
    times.Sort();
    var effects = "";
#if CANVAS_PERFORMANCE
    var trace = CanvasPerformanceTrace.Snapshot();
    effects = $" effects={trace.Calculations} hits={trace.Hits}";
#endif
    Console.WriteLine($"{name}: first {firstMs:F2} ms, p50 {times[repeats / 2]:F2} ms, p95 {times[(int)Math.Ceiling(repeats * .95) - 1]:F2} ms{effects} {hash}");
}
