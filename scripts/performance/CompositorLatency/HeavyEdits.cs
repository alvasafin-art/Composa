using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

internal static class HeavyEdits
{
    private const int Width = 3840, Height = 2160, Repeats = 7;

    public static void Run()
    {
        MeasureDeformation(ColorDepth.Eight, RgbProfile.Srgb);
        MeasureDeformation(ColorDepth.Sixteen, RgbProfile.DisplayP3);
        MeasureComposite(ColorDepth.Eight, RgbProfile.Srgb);
        MeasureComposite(ColorDepth.Sixteen, RgbProfile.DisplayP3);
        MeasureSample(ColorDepth.Eight, RgbProfile.Srgb);
        MeasureSample(ColorDepth.Sixteen, RgbProfile.Srgb);
        MeasureSample(ColorDepth.Sixteen, RgbProfile.DisplayP3);
    }

    private static void MeasureDeformation(ColorDepth depth, RgbProfile profile)
    {
        var label = Label(depth, profile);
        var session = Scene(depth, profile, complex: false);
        using var view = Pixels.NewColor(1056, 768);
        var mapping = new RenderView(1, new SKPoint(1350, 720));
        var edit = session.BeginWarp(4, 4);
        var identity = edit.Mesh;
        var point = identity.Points[12];
        var changes = Enumerable.Range(0, Repeats + 2)
            .Select(i => identity.WithPoint(12, point + new WarpPoint(.018 + i * .003, .023))).ToArray();

        var at = 0;
        Measure(label + " Warp change mesh", () => edit.SetMesh(changes[at++]));
        at = 0;
        Measure(label + " Warp drag render", () =>
            session.RenderViewportFrame(view, view.Info.Rect, mapping, interactiveWarp: true),
            () => Hash(view), () => edit.SetMesh(changes[at++]));
        Measure(label + " Warp repeat preview", () =>
            session.RenderViewportFrame(view, view.Info.Rect, mapping, interactiveWarp: true), () => Hash(view));
        session.CommitWarp();
        at = 0;
        Measure(label + " Warp commit and native frame", () =>
        {
            session.CommitWarp();
            session.RenderView(view, view.Info.Rect, mapping);
        }, () => Hash(view), () => { edit = session.BeginWarp(); edit.SetMesh(changes[at++]); });

        var liquify = Scene(depth, profile, complex: false);
        var source = liquify.ActiveLayer!.Pixels!;
        var field = new LiquifyField(source.Width, source.Height);
        var from = new SKPoint(1230, 770);
        var to = new SKPoint(1355, 832);
        Measure(label + " Liquify push", () => _ = field.Stroke(from, to, LiquifyTool.Push, 590));
        field = field.Stroke(from, to, LiquifyTool.Push, 590);
        SKBitmap? result = null;
        Measure(label + " Liquify preview 1280", () => result = field.Render(source, 1280),
            () => Hash(result!), () => result?.Dispose());
        result!.Dispose();
        result = null;
        Measure(label + " Liquify native raster", () => result = field.Render(source),
            () => Hash(result!), () => result?.Dispose());
        result!.Dispose();
        Measure(label + " Liquify adoption", () =>
        {
            if (!liquify.ApplyLiquify(source, field, result!)) throw new InvalidOperationException("Liquify input was rejected.");
        }, () => Hash(liquify.ActiveLayer!.Pixels!), () =>
        {
            if (liquify.CanUndo) liquify.Undo();
            result = field.Render(source);
        });
    }

    private static void MeasureComposite(ColorDepth depth, RgbProfile profile)
    {
        var label = Label(depth, profile);
        var session = Scene(depth, profile, complex: true);
        var active = session.ActiveLayer!;
        session.AddSmartFilter(active, new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 9 });
        session.AddSmartFilterMask(active);
        using var target = Pixels.NewColor(1440, 810);
        var view = new RenderView(.375f, SKPoint.Empty);
        Measure(label + " 4K stack cold styles", () => session.RenderView(target, target.Info.Rect, view),
            () => Hash(target), LayerEffectsRenderer.ClearCache);
        Measure(label + " 4K stack warm", () => session.RenderView(target, target.Info.Rect, view), () => Hash(target));
        Measure(label + " Smart Filter recompute", () => session.ChangeSmartFilter(active, active.SmartFilters[0].Id,
            new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 9 }), () => Hash(active.Pixels!));
        SKBitmap? full = null;
        Measure(label + " full 4K stack", () => full = session.Flatten(), () => Hash(full!), () => full?.Dispose());
        full!.Dispose();
        LayerEffectsRenderer.ClearCache();
    }

    private static void MeasureSample(ColorDepth depth, RgbProfile profile)
    {
        var label = Label(depth, profile);
        var session = Scene(depth, profile, complex: true);
        using var warmView = Pixels.NewColor(1440, 810);
        session.RenderView(warmView, warmView.Info.Rect, new RenderView(.375f, SKPoint.Empty));
        var fresh = session;
        SKColor color = default;
        Measure(label + " first eyedropper", () => color = fresh.SampleColor(1497, 916), () => color.ToString(),
            () => fresh = new EditorSession(session.Document.Clone()));
        _ = session.Composite();
        Measure(label + " eyedropper cached composite", () => color = session.SampleColor(1497, 916), () => color.ToString(), n: 25);
    }

    private static EditorSession Scene(ColorDepth depth, RgbProfile profile, bool complex)
    {
        var document = new Document(Width, Height) { Depth = depth, Profile = profile };
        document.Layers.Add(Layer.Raster("Backdrop", Surface(Width, Height, depth, profile, 0)));
        var group = complex ? Layer.Group("Group") : null;
        if (group != null) document.Layers.Add(group);
        for (var i = 1; i <= (complex ? 3 : 1); i++)
        {
            var bitmap = Surface(complex ? 2360 : 3200, complex ? 1550 : 1920, depth, profile, i);
            var layer = Layer.Raster($"Raster {i}", bitmap, 318 + 117 * i, 142 + 38 * i);
            if (complex)
            {
                layer.Mask = Pixels.NewMask(bitmap.Width, bitmap.Height, 213);
                if (i <= 2) layer.Effects = new LayerEffects
                {
                    Shadow = new() { Blur = 36, Distance = 24 }, Stroke = new() { Size = 6 }
                };
                if (i == 2)
                    layer.BlendIf = new BlendIfSettings
                    {
                        Gray = new() { ThisLayer = new(21, 79, 226, 251) }
                    };
            }
            (i < 3 && group != null ? group.Children : document.Layers).Add(layer);
            document.SetActive(layer.Id);
        }
        if (complex)
        {
            var adjustment = Layer.ForAdjustment(new InvertAdjustment());
            adjustment.Mask = Pixels.NewMask(Width, Height, 158);
            adjustment.Opacity = .62;
            document.Layers.Add(adjustment);
        }
        return new EditorSession(document);
    }

    private static SKBitmap Surface(int width, int height, ColorDepth depth, RgbProfile profile, int seed)
    {
        var result = Pixels.NewWorkingColor(width, height, depth, profile);
        using var canvas = new SKCanvas(result);
        canvas.Clear(new SKColor(183, (byte)(187 - seed * 33), 143, seed == 0 ? (byte)255 : (byte)190));
        using var paint = new SKPaint { Color = new SKColor(46, 149, (byte)(195 - seed * 22), 140), IsAntialias = true };
        for (int x = 43; x < width; x += 123) canvas.DrawOval(x, height * .44f, 47, height * .37f, paint);
        return result;
    }

    private static string Label(ColorDepth depth, RgbProfile profile) =>
        $"{(depth == ColorDepth.Eight ? "RGB8" : "RGB16")} {(profile.Equals(RgbProfile.Srgb) ? "sRGB" : "P3")}";

    private static string Hash(SKBitmap bitmap) => Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan()));

    private static void Measure(string name, Action action, Func<string>? check = null, Action? setup = null, int n = Repeats)
    {
        setup?.Invoke();
        action();
        var observations = new List<double>();
        var stages = new Dictionary<string, (int Calls, double Milliseconds)>();
        for (var i = 0; i < n; i++)
        {
            setup?.Invoke();
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.Reset();
#endif
            var started = Stopwatch.GetTimestamp();
            action();
            observations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
#if CANVAS_PERFORMANCE
            foreach (var stage in CanvasPerformanceTrace.Snapshot().Stages)
            {
                var old = stages.GetValueOrDefault(stage.Name);
                stages[stage.Name] = (old.Calls + stage.Calls, old.Milliseconds + stage.Milliseconds);
            }
#endif
        }
        observations.Sort();
        double Percentile(double fraction) => observations[(int)Math.Ceiling(n * fraction) - 1];
        Console.WriteLine(JsonSerializer.Serialize(new { name, n, p50 = Percentile(.5), p95 = Percentile(.95),
            check = check?.Invoke(), stages = stages.Select(x => new { name = x.Key, calls = x.Value.Calls, ms = x.Value.Milliseconds }) }));
    }
}
