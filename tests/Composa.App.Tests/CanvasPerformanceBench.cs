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
using Composa.App.Controls;
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
    private readonly List<WeakReference<EditorSession>> closedTabs = [];
    private readonly List<(string Name, WeakReference<Layer> Layer, WeakReference<SKBitmap> Pixels)> closedRasters = [];
    private MainWindow window = null!;
    private ProcessMemory memory = null!;
    private long phaseStart;

    [AvaloniaFact]
    public async Task Replay()
    {
        var scenario = Environment.GetEnvironmentVariable("COMPOSA_PERF_CASE");
        if (scenario == null) return;
        var folder = Environment.GetEnvironmentVariable("COMPOSA_PERF_FIXTURES")!;
        if (scenario == "photoshop-photo")
        {
            var document = ProjectFile.Load(PathCombine(folder, "photo.cmps"));
            Composa.IO.Psd.PsdExport.Save(document, Environment.GetEnvironmentVariable("COMPOSA_PERF_OUTPUT")!);
            return;
        }
        if (scenario is "fixtures" or "fixture-photo")
        {
            MakeFixtures(folder, Environment.GetEnvironmentVariable("COMPOSA_PERF_PHOTO")!, scenario == "fixture-photo");
            return;
        }
        using var sampler = new ProcessMemory();
        memory = sampler;
        var editorGpu = Environment.GetEnvironmentVariable("COMPOSA_PERF_EDITOR_GPU") == "1";
        GpuAcceleration.Configure(filters: false, editor: editorGpu);
        if (editorGpu)
        {
            GpuAcceleration.ProbeOpenCl();
            for (var i = 0; i < 200 && !GpuAcceleration.OpenCl.Probed; i++)
                await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        window = new MainWindow { Width = WindowWidth, Height = WindowHeight };
        window.Show();
        Pump();
        Checkpoint("empty");
        try
        {
            if (scenario == "tabs") await TabCycles(folder);
            else if (scenario == "interaction") ExerciseInteraction();
            else if (scenario == "brush-preview") CheckBrushPreviews();
            else if (scenario == "tools") ExerciseTools();
            else if (scenario is "brush8" or "brush16")
            {
                var depth = scenario == "brush16" ? ColorDepth.Sixteen : ColorDepth.Eight;
                if (Environment.GetEnvironmentVariable("COMPOSA_PERF_LONG_BRUSH") == "1") ExerciseLongBrush(depth);
                else ExerciseBrush(depth);
            }
            else if (scenario == "photo-open")
            {
                var opened = await Open(folder, "photo");
                Checkpoint("open", opened);
                await CloseTab(opened);
            }
            else if (scenario == "photo-cycles")
            {
                for (var pass = 1; pass <= 3; pass++)
                {
                    await Exercise(folder, "photo");
                    await Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                    Checkpoint($"closed photo {pass} +30s");
                }
            }
            else await Exercise(folder, scenario);
            Checkpoint("closed");
            if (scenario is not ("brush8" or "brush16" or "interaction" or "brush-preview" or "tools"))
            {
                await Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Checkpoint("closed +30s");
            }
            if (Environment.GetEnvironmentVariable("COMPOSA_PERF_DIAG_GC") == "1")
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Checkpoint("diagnostic GC 1");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Checkpoint("diagnostic GC 2");
                SKGraphics.PurgeAllCaches();
                Checkpoint("diagnostic Skia purge");
            }
            var output = Environment.GetEnvironmentVariable("COMPOSA_PERF_OUTPUT")!;
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                scenario, window = new { width = WindowWidth, height = WindowHeight },
                backend = "Avalonia Headless / Skia raster", editorGpu, openCl = GpuAcceleration.OpenCl, phases, checkpoints, checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (Environment.GetEnvironmentVariable("COMPOSA_PERF_INSPECT") == "1")
            {
                Console.WriteLine($"Inspect test process {Environment.ProcessId}");
                await Task.Delay(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            window.Close();
            GpuAcceleration.Configure(filters: false, editor: false);
        }
    }

    private void ExerciseInteraction()
    {
        var recent = Directory.CreateTempSubdirectory("composa-menu-replay-");
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var path = PathCombine(recent.FullName, $"document-{i}.cmps");
                File.WriteAllBytes(path, []);
                window.Settings.RecentFiles.Add(path);
            }
            var menus = window.GetLogicalDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().ToArray();
            foreach (var menu in menus) menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            var menuTimes = new List<InputFrame>();
            StartPhase();
            for (var i = 0; i < 6; i++)
            foreach (var menu in menus)
                Measure("submenu opened", () => menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent)), menuTimes, measureInput: true);
            EndPhase("menu state with 12 recent paths", menuTimes);
        }
        finally
        {
            window.Settings.RecentFiles.Clear();
            foreach (var file in recent.EnumerateFiles()) file.Delete();
            recent.Delete();
        }

        var session = EditorSession.NewCanvas(3072, 2048, SKColors.White);
        for (int i = 0; i < 16; i++)
        {
            var pixels = Pixels.NewColor(2000, 1400);
            using (var canvas = new SKCanvas(pixels))
            using (var paint = new SKPaint { Color = new SKColor((byte)(i * 13), 120, 165, 90) })
            {
                canvas.DrawRect(0, 0, pixels.Width, pixels.Height, paint);
                paint.Color = new SKColor(66, (byte)(i * 11), 85, 154);
                canvas.DrawOval(548, 406, 500, 380, paint);
            }
            var layer = Layer.Raster($"Overlay {i}", pixels, 320 + i * 9, 200 + i * 6);
            if (i % 4 == 0) layer.Blend = BlendMode.Multiply;
            session.Document.Layers.Add(layer);
        }
        var moving = session.Document.Layers[^1];
        session.Document.SetActive(moving.Id);
        session.View = session.View with { Snap = false };
        window.AddSession(session);
        window.Canvas.AutoSelect = false;
        window.Canvas.ZoomTo(.625);
        window.Canvas.CenterOn(new(1510, 1000));
        Pump();

        var times = new List<InputFrame>();
        StartPhase();
        for (var i = 1; i <= 42; i++)
        {
            var offset = i <= 21 ? i : 42 - i;
            Measure("pan", () => window.Canvas.CenterOn(new(1510 + 8 * offset, 1000)), times);
        }
        EndPhase("pan 62.5%", times);

        times = [];
        StartPhase();
        for (var i = 1; i <= 36; i++)
        {
            var offset = i <= 18 ? i : 36 - i;
            Measure("pan", () => window.Canvas.CenterOn(new(1020 + 50 * offset, 710 + 20 * offset)), times, measureInput: true);
        }
        EndPhase("pan across viewport padding", times);
        window.Canvas.CenterOn(new(1510, 1000));
        Pump();

        var zoomAnchor = window.Canvas.TranslatePoint(new Rect(window.Canvas.Bounds.Size).Center, window)!.Value;
        times = [];
        StartPhase();
        for (var i = 0; i < 18; i++)
        {
            var direction = i % 6 < 3 ? 1 : -1;
            Measure("zoom", () => window.MouseWheel(zoomAnchor, new Vector(0, direction), RawInputModifiers.Control), times, measureInput: true);
        }
        EndPhase("wheel zoom", times);
        window.Canvas.ZoomTo(.625);
        window.Canvas.CenterOn(new(1510, 1000));
        Pump();

        window.SelectTool(Tool.Move);
        Pump();
        var pointer = window.Canvas.TranslatePoint(window.Canvas.ToScreen(new(1350, 1050)), window)!.Value;
        window.MouseDown(pointer, MouseButton.Left);
        Assert.NotNull(session.Transform);
        times = [];
        StartPhase();
        for (var i = 1; i <= 32; i++)
        {
            var destination = pointer + new Vector(4 * i, i);
            Measure("move", () => window.MouseMove(destination, RawInputModifiers.LeftMouseButton), times);
        }
        EndPhase("move layer 62.5%", times);
        window.MouseUp(pointer + new Vector(128, 32), MouseButton.Left);
        Pump();
        Assert.Null(session.Transform);
        checks["layer position"] = new[] { moving.Transform.X, moving.Transform.Y };

        window.Canvas.ZoomTo(1);
        session.SelectRect(new SKRect(1250, 840, 1380, 990));
        Pump();
        Assert.True(session.BeginMovePixels(duplicate: false));
        times = [];
        StartPhase();
        for (var i = 1; i <= 24; i++)
        {
            var offset = i * 7;
            Measure("move pixels", () => session.MovePixelsBy(offset, 12), times, measureInput: true);
        }
        EndPhase("move selected pixels 100%", times);
        session.EndMovePixels(keep: true);
        checks["moved pixels"] = Hash(moving.Pixels!);
        checks["moved selection"] = Hash(session.Selection!);
        session.MarkSaved("interaction-benchmark.cmps", session.History.CurrentId);
        Pump();
    }

    private void CheckBrushPreviews()
    {
        foreach (var depth in new[] { ColorDepth.Eight, ColorDepth.Sixteen })
        foreach (var mode in new[] { BrushMode.Paint, BrushMode.Erase })
        {
            var session = EditorSession.NewCanvas(1500, 1000, SKColors.Beige, depth);
            var pixels = Pixels.NewColor(1000, 780, depth);
            pixels.Erase(new SKColor(88, 154, 122, 226));
            var layer = Layer.Raster("Paint on this layer", pixels, 170, 119);
            layer.Effects = new LayerEffects
            {
                Stroke = new StrokeEffect { Size = 5 },
                Shadow = new ShadowEffect { Blur = 28, Distance = 18 }
            };
            session.Document.Layers.Add(layer);
            session.Document.SetActive(layer.Id);
            session.Foreground = new SKColor(197, 41, 68);
            session.Brush = new BrushSettings { Size = 500, Hardness = .42, Flow = .62, Opacity = .85 };
            session.ViewZoom = .5;
            session.SelectRect(SKRect.Create(250, 215, 920, 650));
            Assert.True(session.BeginStroke(new SKPoint(510, 335), out _, mode: mode));
            var phase = $"{depth} {mode}";
            using var viewport = Pixels.NewColor(750, 500);
            var view = new RenderView(.5f, SKPoint.Empty);
            session.RenderView(viewport, viewport.Info.Rect, view);
            checks[$"{phase} first dab"] = Hash(viewport);
            session.ContinueStrokeSamples([(new SKPoint(591, 372), .79f), (new SKPoint(711, 430), .63f), (new SKPoint(802, 483), 1)]);
            session.RenderView(viewport, viewport.Info.Rect, view);
            checks[$"{phase} live"] = Hash(viewport);
            checks[$"{phase} working layer"] = Hash(layer.Pixels!);
            session.EndStroke();
            session.RenderView(viewport, viewport.Info.Rect, view);
            checks[$"{phase} last frame"] = Hash(viewport);
            checks[$"{phase} committed layer"] = Hash(layer.Pixels!);
        }
    }

    private void ExerciseTools()
    {
        var session = EditorSession.NewCanvas(4608, 3072, SKColors.Beige);
        var layer = session.ActiveLayer!;
        using (var canvas = new SKCanvas(layer.Pixels!))
        using (var paint = new SKPaint { Color = SKColors.CadetBlue })
            for (var x = 0; x < 4608; x += 53) canvas.DrawRect(x, 0, 11, 3072, paint);
        Pixels.Invalidate(layer.Pixels!);
        window.AddSession(session);
        window.Canvas.Fit();
        Pump();
        var buttons = window.GetLogicalDescendants().OfType<ToolButton>().Where(button => button.IsEffectivelyVisible && button.IsEnabled)
            .Select(button => (Button: button, Position: button.TranslatePoint(new Rect(button.Bounds.Size).Center, window)))
            .Where(button => button.Position != null).Take(8).ToArray();
        Assert.True(buttons.Length >= 2);
        var platform = window.PlatformImpl!;
        // The headless MouseMove helper pumps before and after input. Dispatch the raw
        // event so InputMs measures routing and control updates without that extra work.
        var headless = platform.GetType().GetInterface("Avalonia.Headless.IHeadlessWindow");
        Assert.NotNull(headless);
        var movePointer = headless.GetMethod("MouseMove")!.CreateDelegate<Action<Point, RawInputModifiers>>(platform);
        var pressPointer = headless.GetMethod("MouseDown")!.CreateDelegate<Action<Point, MouseButton, RawInputModifiers>>(platform);
        void MovePointer(Point position) => movePointer(position, RawInputModifiers.None);
        window.SelectTool(Tool.Move);
        var center = window.Canvas.TranslatePoint(new Rect(window.Canvas.Bounds.Size).Center, window)!.Value;
        Pump();
        StartPhase();
        var hoverTimes = new List<InputFrame>();
        for (var i = 0; i < 4; i++)
        foreach (var (button, position) in buttons)
        {
            Measure("canvas hover", () => MovePointer(center + new Vector(i + button.Bounds.Y % 5, i)), hoverTimes, measureInput: true);
            Measure("panel hover", () => MovePointer(position!.Value), hoverTimes, measureInput: true);
            Assert.True(button.IsPointerOver);
        }
        EndPhase("Canvas to tool panel hover", hoverTimes);
        hoverTimes = [];
        StartPhase();
        for (var i = 0; i < 4; i++)
        foreach (var (button, position) in buttons)
        {
            Measure("panel hover", () => MovePointer(position!.Value), hoverTimes, measureInput: true);
            Assert.True(button.IsPointerOver);
        }
        EndPhase("Tool panel hover", hoverTimes);

        if (Environment.GetEnvironmentVariable("COMPOSA_PERF_TOOL_ZOOM") == "200")
        {
            window.Canvas.ZoomTo(2);
            window.Canvas.CenterOn(new(2310, 1489));
            Pump();
        }
        var path = Path(new SKPoint(2160, 1430), 300, 118, 18);
        Point StrokeStart() => window.Canvas.TranslatePoint(window.Canvas.ToScreen(path[0]), window)!.Value;
        session.Brush = new BrushSettings { Size = 500, Hardness = .995, Flow = .65, Opacity = .82 };
        var samples = path.Skip(1).Select(p => (p, 1f)).ToArray();
        session.SetCloneSource(new SKPoint(2115, 1300));
        foreach (var mode in new[] { BrushMode.Paint, BrushMode.Erase, BrushMode.Heal, BrushMode.Healing, BrushMode.Clone })
        {
            window.SelectTool(mode switch
            {
                BrushMode.Erase => Tool.Eraser,
                BrushMode.Heal => Tool.SpotHealing,
                BrushMode.Healing => Tool.HealingBrush,
                BrushMode.Clone => Tool.CloneStamp,
                _ => Tool.Brush
            });
            session.Brush = new BrushSettings { Size = 500, Hardness = .995, Flow = .65, Opacity = .82 };
            session.ViewZoom = window.Canvas.Zoom / window.RenderScaling;
            var start = StrokeStart();
            MovePointer(start);
            Pump();
            StartPhase();
            var times = new List<InputFrame>();
            Measure("press", () => pressPointer(start, MouseButton.Left, RawInputModifiers.None), times, measureInput: true, awaitFrame: true);
            Assert.True(session.IsStroking);
            for (var i = 0; i < samples.Length; i += 3)
            {
                var from = i;
                Measure("move", () => session.ContinueStrokeSamples(samples.AsSpan(from, 3)), times, measureInput: true, awaitFrame: true);
            }
            EndPhase($"{mode} batched samples", times);
            checks[$"{mode} live"] = Hash(layer.Pixels!);
            window.Canvas.CancelInteraction();
            window.MouseUp(start, MouseButton.Left);
            layer = session.ActiveLayer!;
            Pump();
        }

        window.SelectTool(Tool.SpotHealing);
        session.SampleAllLayers = true;
        session.Brush = new BrushSettings { Size = 170, Hardness = .7 };
        for (var i = 0; i < 5; i++)
        {
            var raster = Pixels.NewColor(2750, 2100);
            raster.Erase(new SKColor((byte)(50 + 20 * i), 120, 183, 75));
            session.Document.Layers.Insert(0, Layer.Raster($"Underlay {i}", raster, 480 + i * 27, 360 + i * 21));
        }
        layer.Effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 5 },
            Shadow = new ShadowEffect { Blur = 22, Distance = 12 }
        };
        session.InvalidateAll();
        Pump();
        StartPhase();
        var retouch = new List<InputFrame>();
        Measure("press", () => Assert.True(session.BeginStroke(path[0], out _, mode: BrushMode.Heal)),
            retouch, measureInput: true, awaitFrame: true);
        foreach (var point in path.Skip(1).Take(9))
            Measure("move", () => session.ContinueStroke(point), retouch, measureInput: true, awaitFrame: true);
        EndPhase("Spot Healing with all layers and effects", retouch);
        checks["styled Spot Healing live"] = Hash(layer.Pixels!);
        session.CancelStroke();
        layer = session.ActiveLayer!;
        layer.Effects = null;
        session.Document.Layers.RemoveRange(0, 5);
        session.SampleAllLayers = false;
        session.InvalidateAll();
        Pump();

        window.Canvas.Fit();
        Pump();
        session.SelectRect(new SKRect(500, 420, 2500, 1820));
        session.SelectionBrushSize = 90;
        session.SelectionBrushFeather = 6;
        window.SelectTool(Tool.SelectionBrush);
        Pump();
        StartPhase();
        var selectionTimes = new List<InputFrame>();
        Measure("press", () => session.BeginSelectionBrush(path[0]), selectionTimes, measureInput: true);
        foreach (var point in path.Skip(1))
            Measure("move", () => session.ContinueSelectionBrush(point), selectionTimes, measureInput: true);
        session.EndSelectionBrush();
        EndPhase("Selection brush", selectionTimes);
        checks["selection"] = Hash(session.Selection!);
        session.Deselect();

        var source = layer.Pixels!;
        var field = new LiquifyField(source.Width, source.Height);
        StartPhase();
        var fieldTimes = new List<InputFrame>();
        for (var i = 1; i < path.Length; i++)
        {
            var to = i;
            Measure("move", () => field = field.Stroke(path[to - 1], path[to], LiquifyTool.Push, 590), fieldTimes, measureInput: true);
        }
        EndPhase("Liquify field", fieldTimes);
        StartPhase();
        var renderTimes = new List<InputFrame>();
        SKBitmap? rendered = null;
        for (var i = 0; i < 6; i++)
        {
            rendered?.Dispose();
            Measure("apply", () => rendered = field.Render(source), renderTimes, measureInput: true);
        }
        EndPhase("Liquify full resolution", renderTimes);
        checks["Liquify"] = Hash(rendered!);
        rendered!.Dispose();

        window.Canvas.ZoomTo(1);
        window.Canvas.StartWarp();
        window.Canvas.ChangeWarpGrid(4, 4);
        var warp = session.WarpEdit!;
        const int index = 12;
        Point At(SKPoint p) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(p), window)!.Value;
        var node = warp.Layer.WarpNodePosition(warp.Mesh.Points[index]);
        Pump();
        StartPhase();
        var warpTimes = new List<InputFrame>();
        window.MouseDown(At(node), MouseButton.Left, RawInputModifiers.None);
        foreach (var point in Path(node, 140, 60, 14).Skip(1))
            Measure("move", () => window.MouseMove(At(point), RawInputModifiers.LeftMouseButton), warpTimes, measureInput: true);
        Measure("release", () => window.MouseUp(At(node + new SKPoint(140, 60)), MouseButton.Left), warpTimes, measureInput: true);
        Assert.False(warp.Mesh.IsIdentity);
        EndPhase("Warp at 100%", warpTimes);
        using var viewport = Pixels.NewColor(900, 600);
        session.RenderView(viewport, viewport.Info.Rect, new RenderView(1, new SKPoint(1200, 900)));
        checks["Warp released"] = Hash(viewport);
        window.Canvas.FinishWarp(keep: false);
        session.MarkSaved("tool-replay.cmps", session.History.CurrentId);
    }

    private void ExerciseBrush(ColorDepth depth)
    {
        var session = EditorSession.NewCanvas(6000, 4000, SKColors.White, depth);
        window.AddSession(session);
        window.SelectTool(Tool.Brush);
        session.Foreground = new SKColor(56, 97, 161);
        var fast = Environment.GetEnvironmentVariable("COMPOSA_PERF_FAST_BRUSH") == "1";
        var start = new SKPoint(2660, 1850);
        foreach (var zoom in new[] { "fit", "100%" })
        {
            if (zoom == "100%") window.Canvas.ZoomTo(1); else window.Canvas.Fit();
            foreach (var size in new[] { 84, 500 })
            {
                session.Brush = new BrushSettings { Size = size, Hardness = .28, Flow = .62, Opacity = .85 };
                Pump();
                StartPhase();
                var events = new List<InputFrame>();
                for (int i = 0; i < 5; i++)
                {
                    var offset = new SKPoint(i * 17, i * 31);
                    ReplayStroke(fast ? FastBrushPath(zoom, offset) : Path(start + offset, 520, 145, 20), events);
                }
                EndPhase($"brush {size} px {zoom}", events);
            }
        }
        Assert.Equal(20, session.Revision);
        checks["painted"] = Hash(session.ActiveLayer!.Pixels!);
        session.MarkSaved("brush-replay.cmps", session.History.CurrentId);
    }

    private void ExerciseLongBrush(ColorDepth depth)
    {
        var session = EditorSession.NewCanvas(6000, 4000, SKColors.White, depth);
        window.AddSession(session);
        window.SelectTool(Tool.Brush);
        window.Canvas.Fit();
        session.Foreground = new SKColor(56, 97, 161);
        var burst = Environment.GetEnvironmentVariable("COMPOSA_PERF_BURST_BRUSH") == "1";
        foreach (var size in new[] { 84, 500 })
        {
            session.Brush = new BrushSettings { Size = size, Hardness = .76, Flow = .65, Opacity = .82, Spacing = .045 };
            Pump();
            StartPhase();
            var events = new List<InputFrame>();
            var path = LongBrushPath();
            ReplayLong(path);
            ReplayLong(path.Reverse().Select(p => p + new SKPoint(133, 87)).ToArray());
            void ReplayLong(SKPoint[] points)
            {
                if (burst) ReplayBurst(points, events);
                else ReplayStroke(points, events);
            }
            EndPhase($"long brush {size} px fit", events);
        }
        Assert.Equal(4, session.Revision);
        checks["painted"] = Hash(session.ActiveLayer!.Pixels!);
        session.MarkSaved("brush-replay.cmps", session.History.CurrentId);
    }

    private static SKPoint[] LongBrushPath()
    {
        var turns = FastBrushPath("fit", SKPoint.Empty);
        var path = new List<SKPoint> { turns[0] };
        for (var pass = 0; pass < 4; pass++)
        for (var i = 0; i < turns.Length; i++)
        {
            var start = path[^1];
            var end = turns[(i + pass * 3 + 1) % turns.Length] + new SKPoint(pass * 28, pass * 47);
            path.AddRange(Path(start, end.X - start.X, end.Y - start.Y, i % 4 == 3 ? 1 : 9).Skip(1));
        }
        return path.ToArray();
    }

    private static SKPoint[] FastBrushPath(string zoom, SKPoint offset)
    {
        SKPoint[] positions =
        [
            new(-1, -.35f), new(-.55f, -.9f), new(-.17f, .2f), new(.17f, -.35f), new(.6f, .7f), new(1, .1f),
            new(.3f, .65f), new(-.07f, -.15f), new(-.5f, .8f), new(-.88f, .2f), new(-.27f, -.3f), new(.33f, .25f)
        ];
        var spread = zoom == "100%" ? new SKPoint(350, 155) : new SKPoint(1650, 1150);
        return positions.Select(p => new SKPoint(2950 + p.X * spread.X, 1950 + p.Y * spread.Y) + offset).ToArray();
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
            var revision = session.Revision;
            var events = new List<InputFrame>();
            for (int i = 0; i < 30; i++)
                ReplayStroke(Path(new(1440 + i % 6 * 570, 740 + i / 6 * 560), 46, 21, 3), events);
            EndPhase("30 strokes fit", events);
            Assert.Equal(30, session.Revision - revision);
            Assert.True(session.History.Count >= 10);
            checks["retainedUndo"] = session.History.Count;
            var painted = Hash(retouch.Pixels!);
            for (int i = 0; i < 10; i++) session.Undo();
            Pump();
            checks["undo"] = Hash(NamedLayer(session, "Retouch").Pixels!);
            for (int i = 0; i < 10; i++) session.Redo();
            Pump();
            Assert.Equal(painted, Hash(NamedLayer(session, "Retouch").Pixels!));
            Assert.False(session.History.CanRedo);
            checks["redo"] = painted;
            Checkpoint("after editing", session);
            var copy = PathCombine(folder, $"reopen-{Environment.ProcessId}.cmps");
            try
            {
                StartPhase();
                Assert.Null(await window.SaveTo(session, copy));
                Checkpoint("after save", session);
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
        if (session.Document.Depth == ColorDepth.Sixteen)
            foreach (var layer in session.Document.AllLayers())
                if (layer.Pixels is { } pixels)
                    closedRasters.Add(($"{closedTabs.Count + 1}: {layer.Name}", new(layer), new(pixels)));
        session.MarkSaved(session.FilePath!, session.History.CurrentId);
        var close = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, "Close Project"));
        close.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (int i = 0; window.Sessions.Contains(session) && i < 100; i++)
        {
            Pump();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.DoesNotContain(session, window.Sessions);
        closedTabs.Add(new(session));
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

    private void ReplayBurst(SKPoint[] points, List<InputFrame> times)
    {
        var session = window.Canvas.Session!;
        var individual = Environment.GetEnvironmentVariable("COMPOSA_PERF_INDIVIDUAL_SAMPLES") == "1";
        Point At(SKPoint p) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(p), window)!.Value;
        Measure("press", () => window.MouseDown(At(points[0]), MouseButton.Left), times);
        foreach (var batch in points.Skip(1).Chunk(8))
        {
            // The same render loop as the pointer replay. Send samples together
            // to the session because Headless MouseMove sends a single position.
            var samples = batch.Select(p => (window.Canvas.ToDocument(window.Canvas.ToScreen(p)), 1f)).ToArray();
            Measure("move", () =>
            {
                if (individual)
                    foreach (var (point, pressure) in samples) session.ContinueStroke(point, pressure);
                else session.ContinueStrokeSamples(samples);
                window.MouseMove(At(batch[^1]), RawInputModifiers.LeftMouseButton);
            }, times);
        }
        Measure("release", () => window.MouseUp(At(points[^1]), MouseButton.Left), times);
    }

    private static void Measure(string input, Action action, List<InputFrame> times, bool measureInput = false, bool awaitFrame = false)
    {
        var traceStages = input == "press" && Environment.GetEnvironmentVariable("COMPOSA_PERF_STAGE") == "1";
        var before = traceStages ? CanvasPerformanceTrace.Snapshot().Stages : null;
        var begin = Stopwatch.GetTimestamp();
        action();
        var afterInput = Stopwatch.GetTimestamp();
        Pump();
        if (awaitFrame)
            for (var i = 0; i < 3 && CanvasPerformanceTrace.FrameAt < begin; i++) Pump();
        var frame = CanvasPerformanceTrace.FrameAt;
        var stages = before == null ? null : CanvasPerformanceTrace.Snapshot().Stages.Select(stage =>
        {
            var old = before.FirstOrDefault(s => s.Name == stage.Name);
            return new CanvasPerformanceTrace.Stage(stage.Name, stage.Calls - (old?.Calls ?? 0), stage.Milliseconds - (old?.Milliseconds ?? 0));
        }).Where(stage => stage.Calls != 0).ToArray();
        times.Add(new(input, frame >= begin ? Stopwatch.GetElapsedTime(begin, frame).TotalMilliseconds : null,
            traceStages || measureInput ? Stopwatch.GetElapsedTime(begin, afterInput).TotalMilliseconds : null, stages));
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
            CanvasPerformanceTrace.Snapshot().CachedBytes, CanvasPerformanceTrace.Mapped.Views, CanvasPerformanceTrace.Mapped.Bytes,
            closedTabs.Count(tab => tab.TryGetTarget(out _)), GC.CollectionCount(2), GC.GetGCMemoryInfo().TotalCommittedBytes,
            SKGraphics.GetResourceCacheTotalBytesUsed(),
            closedRasters.Where(raster => raster.Layer.TryGetTarget(out _) || raster.Pixels.TryGetTarget(out _))
                .Select(raster => new ClosedRaster(raster.Name, raster.Layer.TryGetTarget(out _), raster.Pixels.TryGetTarget(out _))).ToArray(),
            Environment.GetEnvironmentVariable("COMPOSA_PERF_NATIVE_MEMORY") == "1" ? CanvasNativeMemory.Read() : null));

    private static SKPoint[] Path(SKPoint start, float dx, float dy, int moves = 13) =>
        Enumerable.Range(0, moves + 1).Select(i => new SKPoint(start.X + dx * i / moves, start.Y + dy * i / moves)).ToArray();

    private static Layer NamedLayer(EditorSession session, string name) =>
        session.Document.AllLayers().Single(layer => layer.Name == name);

    private static string Hash(SKBitmap bitmap) => Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan()));
    private static string PathCombine(string root, string name) => System.IO.Path.Combine(root, name);

    private static void MakeFixtures(string folder, string source, bool photoOnly)
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
        if (photoOnly)
        {
            SavePhoto();
            return;
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

        SavePhoto();

        void SavePhoto()
        {
            var high = EditorSession.NewCanvas(6000, 4000, depth: ColorDepth.Sixteen, background: null);
            var profile = RgbProfile.Rgb1998;
            high.Document.Profile = profile;
            high.Document.ActiveLayer!.Pixels = Photo(6000, 4000, ColorDepth.Sixteen, profile);
            var retouch = Pixels.NewWorkingColor(3600, 3000, ColorDepth.Sixteen, profile);
            using (var canvas = new SKCanvas(retouch)) canvas.DrawBitmap(high.Document.ActiveLayer.Pixels, -1200, -500);
            var layer = Layer.Raster("Retouch", retouch, 1200, 500);
            // Keep history within the stock 3 GiB budget without changing its policy.
            layer.Locks = LayerLocks.Transparency;
            high.Document.Layers.Add(layer);
            high.Document.SetActive(layer.Id);
            ProjectFile.Save(high.Document, PathCombine(folder, "photo.cmps"));
        }
    }

    private sealed record InputFrame(string Input, double? CompleteMs, double? InputMs, CanvasPerformanceTrace.Stage[]? Stages);
    private sealed record Phase(string Name, double ElapsedMs, List<InputFrame> Events, Mem Peak, CanvasPerformanceTrace.Sample Diagnostics);
    private sealed record Snapshot(string Name, Mem Memory, long Managed, long DocumentRaster, long EffectCache,
        int MappedViews, long MappedBytes, int ClosedTabsAlive, int Gen2Collections, long ManagedCommitted, long SkiaResourceCache,
        ClosedRaster[] ClosedRasters, CanvasNativeMemory.Sample? NativeMemory);
    private sealed record ClosedRaster(string Name, bool LayerAlive, bool PixelsAlive);
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
