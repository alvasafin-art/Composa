using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

internal static partial class BrushLatency
{
    private const uint Wheel = 0x020A;
    private static InteractionSample? interaction;

    private sealed class InteractionSample(string phase)
    {
        public string Phase { get; } = phase;
        public long Posted, Entered, Returned, FirstDraw, DesktopChanged;
        public int Events, Draws;
        public CanvasPerformanceTrace.Sample? Diagnostics;
    }

    private static void ConfigurePresentation(AppBuilder builder)
    {
        var software = Environment.GetEnvironmentVariable("COMPOSA_PERF_SOFTWARE_PRESENTATION") == "1";
        var lowLatency = Environment.GetEnvironmentVariable("COMPOSA_PERF_LOW_LATENCY_COMPOSITION") == "1";
        if (!software && !lowLatency) return;
        var options = new Win32PlatformOptions();
        if (software) options.RenderingMode = [Win32RenderingMode.Software];
        if (lowLatency) options.CompositionMode = [Win32CompositionMode.LowLatencyDxgiSwapChain, Win32CompositionMode.WinUIComposition,
            Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface];
        builder.With(options);
    }

    private static int RunInteraction(string output)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        Settings.Persist = false;
        var builder = Program.BuildAvaloniaApp();
        ConfigurePresentation(builder);
        builder.SetupWithoutStarting();
        var editor = Environment.GetEnvironmentVariable("COMPOSA_PERF_EDITOR_GPU") == "1";
        var software = Environment.GetEnvironmentVariable("COMPOSA_PERF_SOFTWARE_PRESENTATION") == "1";
        GpuAcceleration.Configure(filters: false, editor: editor);
        var window = new MainWindow { Width = 1280, Height = 840, Position = new PixelPoint(10, 10), Topmost = true };
        using var done = new CancellationTokenSource();
        Exception? failure = null;
        window.Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await ReplayInteraction(window, output, editor, software); }
            catch (Exception error) { failure = error; }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        if (failure == null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static async Task ReplayInteraction(MainWindow window, string output, bool editor, bool software)
    {
        var canvas = window.Canvas;
        var session = Scene(complex: true);
        var moving = session.ActiveLayer!;
        var initialPlacement = moving.Transform;
        window.AddSession(session);
        window.SelectTool(Tool.Move);
        canvas.AutoSelect = false;
        canvas.ZoomTo(.625);
        canvas.CenterOn(new(1536, 1024));
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        var samples = new List<InteractionSample>();
        Track(InputElement.PointerMovedEvent);
        Track(InputElement.PointerPressedEvent);
        Track(InputElement.PointerWheelChangedEvent);
        void Track<T>(RoutedEvent<T> e) where T : PointerEventArgs
        {
            window.AddHandler(e, RoutedEnter, RoutingStrategies.Tunnel, true);
            window.AddHandler(e, RoutedLeave, RoutingStrategies.Bubble, true);
        }
        CanvasPerformanceTrace.CanvasDrawCompleted += Recorded;
        await Task.Delay(300);

        // Refuse to attribute pixels from another window or a disconnected input desktop.
        var center = canvas.PointToScreen(new Rect(canvas.Bounds.Size).Center);
        var desktopVisible = WindowFromPoint(new WinPoint { X = center.X, Y = center.Y }) == hwnd;
        var probeDesktop = Environment.GetEnvironmentVariable("COMPOSA_PERF_SKIP_DESKTOP_PROBE") != "1";
        string? desktopError = null;
        if (desktopVisible && probeDesktop)
        {
            try { using var read = new DesktopProbe(); _ = read.Read(center, hover: true); }
            catch (InvalidOperationException error) { desktopVisible = false; desktopError = error.Message; }
        }
        using var process = Process.GetCurrentProcess();
        object? nativeExtent = null;
        var control = new Dictionary<string, object>();
        using var mouse = new Pointer(1, PointerType.Mouse, true);
        timeBeginPeriod(1);
        try
        {
            PixelPoint At(double x, double y) => canvas.PointToScreen(new Point(canvas.Bounds.Width * x, canvas.Bounds.Height * y));
            var anchor = At(.55, .48);
            var probe = At(.35, .43);
            var oldZoom = canvas.Zoom;
            for (var i = 0; i < 19; i++)
            {
                var direction = (i & 1) == 0 ? 1 : -1;
                await Measure(i == 0 ? "zoom cold" : "zoom warm", probe, () =>
                    Dispatcher.UIThread.Post(() => canvas.RaiseEvent(new PointerWheelEventArgs(canvas, mouse, canvas,
                        canvas.PointToClient(anchor), (ulong)Environment.TickCount64, new PointerPointProperties(),
                        KeyModifiers.Control, new Vector(0, direction))), DispatcherPriority.Input));
            }
            control["wheelZoomApplied"] = canvas.Zoom != oldZoom;
            if (!Equals(control["wheelZoomApplied"], true)) throw new InvalidOperationException("Avalonia wheel input did not change zoom.");
            await Task.Delay(160);
            canvas.ZoomTo(.625);
            canvas.CenterOn(new(1536, 1024));
            await Task.Delay(170);
            for (var i = 0; i < 24; i++)
                await Measure("pan", probe, () => Send(Wheel, anchor, (i % 8 < 4 ? 120 : -120) << 16));
            for (var i = 0; i < 8; i++)
            {
                var delta = ((i & 1) == 0 ? 120 : -120) << 16;
                await Measure("pan burst", probe, () =>
                {
                    for (var tick = 0; tick < 3; tick++) Send(Wheel, anchor, delta);
                });
            }

            canvas.CenterOn(new(1536, 1024));
            await Task.Delay(170);
            var press = At(.50, .50);
            Send(Down, press, flags: 1);
            await Task.Delay(75);
            if (session.Transform == null) throw new InvalidOperationException("Native pointer did not start the layer transform.");
            for (var i = 1; i <= 20; i++)
            {
                var next = new PixelPoint(press.X + i * 4, press.Y + i % 5 * 3);
                await Measure("move complex layer", probe, () => Send(Move, next, flags: 1));
            }
            Send(Up, new PixelPoint(press.X + 80, press.Y), flags: 0);
            await Task.Delay(160);
            control["layer moved"] = moving.Transform.X != initialPlacement.X || moving.Transform.Y != initialPlacement.Y;
            var finalPlacement = moving.Transform;
            session.Undo();
            control["undo placement"] = session.Document.Find(moving.Id)?.Transform == initialPlacement;
            session.Redo();
            control["redo placement"] = session.Document.Find(moving.Id)?.Transform == finalPlacement;

            var tools = window.GetVisualDescendants().OfType<ToolButton>()
                .Where(x => x.IsEffectivelyVisible && x.IsEnabled && x.Bounds.Width > 16).Take(4).ToArray();
            for (var i = 0; i < 20 && tools.Length > 1; i++)
            {
                var point = tools[i % tools.Length].PointToScreen(new Point(4, 4));
                await Measure("panel hover", point, () => Send(Move, point, flags: 0));
            }

            window.SelectTool(Tool.Brush);
            session.Brush = new BrushSettings { Size = 150, Hardness = .45 };
            var start = At(.33, .55);
            Send(Down, start, flags: 1);
            await Task.Delay(100);
            for (var i = 1; i <= 12; i++)
            {
                var next = new PixelPoint(start.X + i * 13, start.Y + (i % 3) * 4);
                await Measure("brush control", next, () => Send(Move, next, flags: 1));
            }
            Send(Up, new PixelPoint(start.X + 156, start.Y), flags: 0);
            await Task.Delay(140);
            control["brush completed"] = !session.IsStroking && session.CanUndo;
            control["complex final pixels"] = HashView(session);

            var compatible = Scene(complex: false);
            window.AddSession(compatible);
            window.SelectTool(Tool.Move);
            var compatibleLayer = compatible.ActiveLayer!;
            var beforeMove = compatibleLayer.Transform;
            canvas.ZoomTo(.625);
            canvas.CenterOn(new(1536, 1024));
            await Task.Delay(170);
            var lightPress = At(.5, .5);
            Send(Down, lightPress, flags: 1);
            await Task.Delay(85);
            for (var i = 1; i <= 12; i++)
            {
                var next = new PixelPoint(lightPress.X + i * 5, lightPress.Y);
                await Measure("move simple layer", probe, () => Send(Move, next, flags: 1));
            }
            Send(Up, new PixelPoint(lightPress.X + 60, lightPress.Y), flags: 0);
            await Task.Delay(170);
            control["simple layer moved"] = compatibleLayer.Transform != beforeMove;
            compatible.Undo();
            control["simple layer restored"] = compatible.Document.Find(compatibleLayer.Id)?.Transform == beforeMove;
            control["simple restored pixels"] = HashView(compatible);
            if (editor) GpuAcceleration.ProbeOpenCl();
            canvas.ZoomTo(1);
            canvas.CenterOn(new(1536, 1024));
            nativeExtent = new { width = canvas.Bounds.Width * window.RenderScaling, height = canvas.Bounds.Height * window.RenderScaling };
            await Measure("native stack cold", probe, compatible.InvalidateAll, direct: true);
            for (var i = 0; editor && i < 20 && !GpuAcceleration.OpenCl.Probed; i++) await Task.Delay(100);
            await Task.Delay(200);
            for (var i = 0; i < 12; i++)
                await Measure("native stack warm", probe, compatible.InvalidateAll, direct: true, pixelChangeExpected: false);
            compatible.MarkEmbeddedSaved();
            session.MarkEmbeddedSaved();
        }
        finally
        {
            timeEndPeriod(1);
            CanvasPerformanceTrace.CanvasDrawCompleted -= Recorded;
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                processId = process.Id, sessionId = process.SessionId, remote = GetSystemMetrics(0x1000) != 0,
                desktopVisible, desktopError, desktopProbe = probeDesktop && desktopVisible, softwareRequested = software,
                lowLatencyRequested = Environment.GetEnvironmentVariable("COMPOSA_PERF_LOW_LATENCY_COMPOSITION") == "1",
                zoomInput = "Avalonia routed wheel, queued at Input priority", otherInput = "Win32 PostMessage",
                hardwareSkia = canvas.HardwareViewportAvailable, editorRequested = editor, openCl = GpuAcceleration.OpenCl,
                scaling = window.RenderScaling, window = window.ClientSize.ToString(), nativeExtent, control,
                samples = samples.Select(s => new { phase = s.Phase, s.Events, s.Draws, queueMs = Elapsed(s.Posted, s.Entered),
                    inputMs = Elapsed(s.Entered, s.Returned), canvasDrawMs = Elapsed(s.Posted, s.FirstDraw),
                    desktopPixelMs = Elapsed(s.Posted, s.DesktopChanged), diagnostics = s.Diagnostics })
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        window.Close();

        async Task Measure(string phase, PixelPoint point, Action emit, bool direct = false, bool pixelChangeExpected = true)
        {
            var sample = new InteractionSample(phase);
            CanvasPerformanceTrace.Reset();
            using var desktop = probeDesktop && desktopVisible && pixelChangeExpected ? new DesktopProbe() : null;
            var before = desktop?.Read(point, hover: true);
            sample.Posted = Stopwatch.GetTimestamp();
            Volatile.Write(ref interaction, sample);
            if (direct) { sample.Entered = sample.Posted; sample.Events = 1; }
            emit();
            if (direct) sample.Returned = Stopwatch.GetTimestamp();
            if (desktop != null)
                await Task.Run(() =>
                {
                    while (Stopwatch.GetElapsedTime(sample.Posted).TotalMilliseconds < 70)
                    {
                        if (desktop.Read(point, hover: true) != before)
                        { sample.DesktopChanged = Stopwatch.GetTimestamp(); break; }
                        Thread.Sleep(2);
                    }
                });
            var remaining = 85 - Stopwatch.GetElapsedTime(sample.Posted).TotalMilliseconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
            Volatile.Write(ref interaction, null);
            sample.Diagnostics = CanvasPerformanceTrace.Snapshot();
            samples.Add(sample);
        }

        void Send(uint message, PixelPoint point, int flags)
        {
            var root = window.PointToScreen(default);
            var x = message == Wheel ? point.X : point.X - root.X;
            var y = message == Wheel ? point.Y : point.Y - root.Y;
            if (!PostMessage(hwnd, message, flags, (y << 16) | (x & 0xffff)))
                throw new InvalidOperationException($"Can't route Win32 message {message:X} to the benchmark window.");
        }
    }

    private static string HashView(EditorSession session)
    {
        using var bitmap = Pixels.NewColor(800, 600);
        session.RenderView(bitmap, bitmap.Info.Rect, new RenderView(.5f, new SKPoint(736, 424)));
        return Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan()));
    }

    private static EditorSession Scene(bool complex)
    {
        var session = EditorSession.NewCanvas(3072, 2048, SKColors.Beige);
        session.View = session.View with { Snap = false };
        for (var index = 0; index < (complex ? 10 : 2); index++)
        {
            var pixels = Pixels.NewColor(2060, 1520);
            using var canvas = new SKCanvas(pixels);
            using var paint = new SKPaint { Color = new SKColor((byte)(39 + index * 14), 145, 183, 146) };
            for (var x = index * 5; x < 2060; x += 57)
                canvas.DrawRect(x, 0, 21, 1520, paint);
            paint.Color = paint.Color.WithAlpha(105);
            for (var y = index * 11; y < 1520; y += 73)
                canvas.DrawRect(0, y, 2060, 16, paint);
            var layer = Layer.Raster($"Overlay {index}", pixels, 370 + index * 11, 220 + index * 9);
            if (complex && index % 3 == 0) layer.Blend = BlendMode.Multiply;
            session.Document.Layers.Add(layer);
            session.Document.SetActive(layer.Id);
        }
        return session;
    }


    private static void RoutedEnter(object? sender, PointerEventArgs args)
    {
        if (Volatile.Read(ref interaction) is not { } s) return;
        s.Events++;
        if (s.Entered == 0) Volatile.Write(ref s.Entered, Stopwatch.GetTimestamp());
    }

    private static void RoutedLeave(object? sender, PointerEventArgs args)
    {
        if (Volatile.Read(ref interaction) is { Returned: 0 } s) s.Returned = Stopwatch.GetTimestamp();
    }

    private static void Recorded(long timestamp)
    {
        if (Volatile.Read(ref interaction) is not { } s || Volatile.Read(ref s.Entered) == 0) return;
        Interlocked.CompareExchange(ref s.FirstDraw, timestamp, 0);
        Interlocked.Increment(ref s.Draws);
    }
}
