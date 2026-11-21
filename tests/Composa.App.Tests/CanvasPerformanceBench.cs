#if CANVAS_PERFORMANCE
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class CanvasPerformanceBench
{
    private const int WindowWidth = 1440, WindowHeight = 900;
    private readonly List<Phase> phases = [];
    private readonly List<Snapshot> checkpoints = [];
    private readonly Dictionary<string, object> checks = [];
    private MainWindow window = null!;
    private ProcessMemory memory = null!;
    private long phaseStart;

    [AvaloniaFact]
    public async Task Replay()
    {
        var scenario = Environment.GetEnvironmentVariable("COMPOSA_PERF_CASE");
        if (scenario == null) return;
        var folder = Environment.GetEnvironmentVariable("COMPOSA_PERF_FIXTURES")!;
        if (scenario == "fixtures")
        {
            MakeFixtures(folder, Environment.GetEnvironmentVariable("COMPOSA_PERF_PHOTO")!);
            return;
        }
        using var sampler = new ProcessMemory();
        memory = sampler;
        window = new MainWindow { Width = WindowWidth, Height = WindowHeight };
        window.Show();
        Pump();
        Checkpoint("empty");
        try
        {
            if (scenario == "tabs") await TabCycles(folder);
            else await Exercise(folder, scenario);
            Checkpoint("closed");
            await Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Checkpoint("closed +30s");
            var output = Environment.GetEnvironmentVariable("COMPOSA_PERF_OUTPUT")!;
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                scenario, window = new { width = WindowWidth, height = WindowHeight },
                backend = "Avalonia Headless / Skia raster", phases, checkpoints, checks
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private async Task Exercise(string folder, string scenario)
    {
        StartPhase();
        var session = await Open(folder, scenario);
        EndPhase("cold open / effect", []);
        Checkpoint("open", session);
        session.View = session.View with { Snap = false };
        window.Canvas.AutoSelect = true;
        if (scenario == "card")
        {
            var title = NamedLayer(session, "Headline");
            var start = title.ControlBounds;
            var from = new SKPoint(start.Left + 18, start.Top + 24);
            session.SelectLayer(title.Id);
            window.SelectTool(Tool.Move);
            Pump();
            Gesture("move fit", Path(from, 60, 95), omitLastMove: true);
            Assert.InRange(title.ControlBounds.Left - start.Left, 58, 62);
            Assert.InRange(title.ControlBounds.Top - start.Top, 93, 97);
            window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.Control);
            Pump();
            var before = title.ControlBounds;
            var transparent = new SKPoint(before.Right - 45, before.Bottom - 30);
            Assert.Equal((byte)0, title.Pixels!.GetPixel(title.Pixels.Width - 45, title.Pixels.Height - 30).Alpha);
            Gesture("Ctrl+T transparent fit", Path(transparent, -45, 65), omitLastMove: true);
            Assert.InRange(title.ControlBounds.Left - before.Left, -47, -43);
            Assert.InRange(title.ControlBounds.Top - before.Top, 63, 67);
            checks["end"] = new[] { title.ControlBounds.Left, title.ControlBounds.Top };
        }
        else if (scenario == "banner")
        {
            var feature = NamedLayer(session, "Feature");
            foreach (var zoom in new[] { "fit", "100%" })
            {
                if (zoom == "100%") window.Canvas.ZoomTo(1); else window.Canvas.Fit();
                session.EditingMask = false;
                session.SelectLayer(feature.Id);
                window.SelectTool(Tool.Move);
                Pump();
                Gesture("move " + zoom, Path(new(1920, 1080), 56, 45), omitLastMove: true);
                window.SelectTool(Tool.Brush);
                session.Foreground = new SKColor(245, 92, 43);
                session.Brush = new BrushSettings { Size = 84, Hardness = .72, Opacity = .86 };
                Pump();
                Gesture("paint " + zoom, Path(new(1910, 1070), 360, 155));
                session.EditingMask = true;
                session.Foreground = SKColors.Black;
                Pump();
                Gesture("mask " + zoom, Path(new(1730, 1060), 345, -116));
            }
            checks["mask"] = Hash(feature.Mask!);
        }
        else
        {
            var retouch = NamedLayer(session, "Retouch");
            session.SelectLayer(retouch.Id);
            window.SelectTool(Tool.Brush);
            session.Foreground = new SKColor(172, 104, 82);
            session.Brush = new BrushSettings { Size = 52, Opacity = .27 };
            Pump();
            StartPhase();
            var events = new List<InputFrame>();
            for (int i = 0; i < 30; i++)
                ReplayStroke(Path(new(440 + i % 6 * 975, 470 + i / 6 * 725), 46, 21, 3), events);
            EndPhase("30 strokes fit", events);
            Assert.Equal(30, session.History.Count);
            var painted = Hash(retouch.Pixels!);
            for (int i = 0; i < 10; i++) session.Undo();
            Pump();
            checks["undo"] = Hash(NamedLayer(session, "Retouch").Pixels!);
            for (int i = 0; i < 10; i++) session.Redo();
            Pump();
            Assert.Equal(painted, Hash(NamedLayer(session, "Retouch").Pixels!));
            checks["redo"] = painted;
            var copy = PathCombine(folder, $"reopen-{Environment.ProcessId}.cmps");
            try
            {
                StartPhase();
                Assert.Null(await window.SaveTo(session, copy));
                await CloseTab(session);
                var reopened = Assert.IsType<EditorSession>(await window.OpenPath(copy));
                Pump();
                Assert.Equal(ColorDepth.Sixteen, reopened.Document.Depth);
                Assert.Equal(RgbProfile.Rgb1998, reopened.Document.Profile);
                checks["icc"] = Convert.ToHexString(SHA256.HashData(reopened.Document.Profile.IccBytes()));
                checks["reopened"] = Hash(NamedLayer(reopened, "Retouch").Pixels!);
                EndPhase("save, close, reopen", []);
                await CloseTab(reopened);
            }
            finally { File.Delete(copy); }
            Checkpoint("end");
            return;
        }
        Checkpoint("end", session);
        using (var composite = session.Flatten()) checks["composite"] = Hash(composite);
        await CloseTab(session);
    }

    private async Task TabCycles(string folder)
    {
        for (var i = 1; i <= 5; i++)
        {
            StartPhase();
            await Cycle(folder);
            EndPhase($"tab {i}", []);
            Checkpoint($"tab {i} closed");
        }
    }

    private async Task Cycle(string folder)
    {
        var session = await Open(folder, "banner");
        session.SelectLayer(NamedLayer(session, "Feature").Id);
        window.SelectTool(Tool.Brush);
        session.Foreground = SKColors.OrangeRed;
        ReplayStroke(Path(new(1800, 1000), 90, 50, 4), []);
        await CloseTab(session);
    }

    private async Task<EditorSession> Open(string folder, string scenario)
    {
        var session = Assert.IsType<EditorSession>(await window.OpenPath(PathCombine(folder, scenario + ".cmps")));
        window.Canvas.Fit();
        Pump();
        return session;
    }

    private async Task CloseTab(EditorSession session)
    {
        session.MarkSaved(session.FilePath!, session.History.CurrentId);
        var close = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, "Close Project"));
        close.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (int i = 0; window.Sessions.Contains(session) && i < 100; i++)
        {
            Pump();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.DoesNotContain(session, window.Sessions);
        Pump();
    }

    private void Gesture(string name, SKPoint[] points, bool omitLastMove = false)
    {
        StartPhase();
        var times = new List<InputFrame>();
        ReplayStroke(points, times, omitLastMove);
        EndPhase(name, times);
    }

    private void ReplayStroke(SKPoint[] points, List<InputFrame> times, bool omitLastMove = false)
    {
        Point At(SKPoint p) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(p), window)!.Value;
        var first = At(points[0]);
        Measure("press", () => window.MouseDown(first, MouseButton.Left), times);
        foreach (var p in points.Skip(1).Take(points.Length - (omitLastMove ? 2 : 1)))
        {
            var move = At(p);
            Measure("move", () => window.MouseMove(move, RawInputModifiers.LeftMouseButton), times);
        }
        var last = At(points[^1]);
        Measure("release", () => window.MouseUp(last, MouseButton.Left), times);
    }

    private static void Measure(string input, Action action, List<InputFrame> times)
    {
        var begin = Stopwatch.GetTimestamp();
        action();
        Pump();
        var frame = CanvasPerformanceTrace.FrameAt;
        times.Add(new(input, frame >= begin ? Stopwatch.GetElapsedTime(begin, frame).TotalMilliseconds : null));
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private void StartPhase()
    {
        CanvasPerformanceTrace.Reset();
        memory.ResetPeak();
        phaseStart = Stopwatch.GetTimestamp();
    }

    private void EndPhase(string name, List<InputFrame> events) =>
        phases.Add(new(name, Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds, events, memory.Peak(), CanvasPerformanceTrace.Snapshot()));

    private void Checkpoint(string name, EditorSession? session = null) =>
        checkpoints.Add(new(name, memory.Read(), GC.GetTotalMemory(false), session?.Document.RasterPixels() * 4 ?? 0,
            CanvasPerformanceTrace.Snapshot().CachedBytes));

    private static SKPoint[] Path(SKPoint start, float dx, float dy, int moves = 13) =>
        Enumerable.Range(0, moves + 1).Select(i => new SKPoint(start.X + dx * i / moves, start.Y + dy * i / moves)).ToArray();

    private static Layer NamedLayer(EditorSession session, string name) =>
        session.Document.AllLayers().Single(layer => layer.Name == name);

    private static string Hash(SKBitmap bitmap) => Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan()));
    private static string PathCombine(string root, string name) => System.IO.Path.Combine(root, name);

    private static void MakeFixtures(string folder, string source)
    {
        Directory.CreateDirectory(folder);
        using var photo = SKBitmap.Decode(source) ?? throw new IOException("Provide the repository's studio-workspace.png.");
        var crop = new SKRect(photo.Width * .18f, photo.Height * .145f, photo.Width * .835f, photo.Height * .92f);
        SKBitmap Photo(int w, int h, ColorDepth depth, RgbProfile profile)
        {
            var result = Pixels.NewWorkingColor(w, h, depth, profile);
            using var canvas = new SKCanvas(result);
            using var image = SKImage.FromBitmap(photo);
            canvas.DrawImage(image, crop, SKRect.Create(w, h), new SKSamplingOptions(SKFilterMode.Linear));
            return result;
        }
        var card = EditorSession.NewCanvas(1080, 1350);
        card.Document.ActiveLayer!.Pixels = Photo(1080, 1350, ColorDepth.Eight, RgbProfile.Srgb);
        var titles = new[] { ("Headline", 144, 320, 104), ("Subheading", 148, 650, 54), ("Composa Studio", 148, 1160, 42) };
        foreach (var (text, x, y, size) in titles)
        {
            var item = card.AddText(new(x, y), new TextStyle
            { Text = text, Bold = true, Size = size, Color = 0xFFFFFFFF, BoxWidth = 770, BoxHeight = 225 });
            item.Name = text;
            item.Effects = new LayerEffects { Shadow = new() { Blur = 35 }, Stroke = new() { Size = 5 } };
        }
        ProjectFile.Save(card.Document, PathCombine(folder, "card.cmps"));

        var banner = EditorSession.NewCanvas(3840, 2160);
        var background = banner.Document.ActiveLayer!;
        background.Pixels = Photo(3840, 2160, ColorDepth.Eight, RgbProfile.Srgb);
        var group = Layer.Group("Design");
        banner.Document.Layers.Add(group);
        var feature = Layer.Raster("Feature", Photo(3650, 1980, ColorDepth.Eight, RgbProfile.Srgb), 90, 85);
        feature.Mask = Pixels.NewMask(3650, 1980, 255);
        using (var maskCanvas = new SKCanvas(feature.Mask))
        using (var cut = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear })
        {
            maskCanvas.DrawCircle(280, 360, 230, cut);
            maskCanvas.DrawCircle(3180, 1550, 400, cut);
        }
        feature.Effects = new LayerEffects { Shadow = new() { Blur = 82, Distance = 43 }, OuterGlow = new() { Size = 28 } };
        group.Children.Add(feature);
        banner.Document.SetActive(feature.Id);
        ProjectFile.Save(banner.Document, PathCombine(folder, "banner.cmps"));

        var high = EditorSession.NewCanvas(6000, 4000, depth: ColorDepth.Sixteen, background: null);
        high.Document.Profile = RgbProfile.Rgb1998;
        high.Document.ActiveLayer!.Pixels = Photo(6000, 4000, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        high.AddBlankLayer().Name = "Retouch";
        ProjectFile.Save(high.Document, PathCombine(folder, "photo.cmps"));
    }

    private sealed record InputFrame(string Input, double? CompleteMs);
    private sealed record Phase(string Name, double ElapsedMs, List<InputFrame> Events, Mem Peak, CanvasPerformanceTrace.Sample Diagnostics);
    private sealed record Snapshot(string Name, Mem Memory, long Managed, long DocumentRaster, long EffectCache);
    private sealed record Mem(long Private, long WorkingSet);

    private sealed class ProcessMemory : IDisposable
    {
        private readonly object sync = new();
        private readonly Process process = Process.GetCurrentProcess();
        private readonly Timer timer;
        private Mem peak = new(0, 0);

        public ProcessMemory() => timer = new Timer(_ => { lock (sync) Sample(); }, null, 0, 10);

        private Mem Sample()
        {
            process.Refresh();
            var current = new Mem(process.PrivateMemorySize64, process.WorkingSet64);
            peak = new(Math.Max(peak.Private, current.Private), Math.Max(peak.WorkingSet, current.WorkingSet));
            return current;
        }

        public Mem Read() { lock (sync) return Sample(); }
        public void ResetPeak() { lock (sync) peak = Sample(); }
        public Mem Peak() { lock (sync) { Sample(); return peak; } }

        public void Dispose()
        {
            using var stopped = new ManualResetEvent(false);
            if (timer.Dispose(stopped)) stopped.WaitOne();
            process.Dispose();
        }
    }
}
#endif
