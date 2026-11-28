using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Composa.App;
using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

internal static partial class BrushLatency
{
    private const uint Move = 0x0200, Down = 0x0201, Up = 0x0202;
    private static Sample? pending;
    private sealed class Sample(string phase)
    {
        internal readonly string Phase = phase;
        internal long Posted, Entered, Returned, Observed;
        internal bool NativeCursor;
        internal CanvasPerformanceTrace.Sample? Diagnostics;
        internal long Frame;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["project-fixtures", var fixtureFolder, var photo])
            return ProjectLifecycle.MakeFixtures(fixtureFolder, photo);
        if (args is ["projects", var projectsFolder, var projectsOutput, var firstProject])
            return ProjectLifecycle.Run(projectsFolder, projectsOutput, firstProject);
        if (args is ["interaction", var interactionOutput])
            return RunInteraction(interactionOutput);
        var size = 150;
        if (!OperatingSystem.IsWindows() || args.Length is < 2 or > 4 || args[0] is not ("simple" or "layered")
            || (args.Length >= 3 && (!int.TryParse(args[2], out size) || size is < 1 or > 1500))
            || (args.Length == 4 && args[3] is not ("fit" or "100")))
        {
            Console.Error.WriteLine("Run on a visible Windows desktop: BrushLatency <simple|layered> <output.json> [size] [fit|100]");
            return 2;
        }
        Settings.Persist = false;
        var builder = Program.BuildAvaloniaApp();
        ConfigurePresentation(builder);
        builder.SetupWithoutStarting();
        var editorGpu = Environment.GetEnvironmentVariable("COMPOSA_PERF_EDITOR_GPU") == "1";
        GpuAcceleration.Configure(filters: false, editor: editorGpu);
        var window = new MainWindow { Width = 1200, Height = 760, Position = new PixelPoint(20, 20), Topmost = true };
        using var done = new CancellationTokenSource();
        Exception? failure = null;
        window.Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Run(window, args[0], args[1], size, args.Length == 4 && args[3] == "100", editorGpu); }
            catch (Exception ex) { failure = ex; }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        if (failure == null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static async Task Run(MainWindow window, string scenario, string output, int size, bool actualSize, bool editorGpu)
    {
        var session = EditorSession.NewCanvas(4096, 3072, SKColors.White);
        var opaqueLayer = Environment.GetEnvironmentVariable("COMPOSA_PERF_OPAQUE_LAYER") == "1";
        if (scenario == "layered")
        {
            for (int i = 1; i <= 5; i++)
            {
                var pixels = EditorSession.NewCanvas(4096, 3072).ActiveLayer!.Pixels!;
                using (var canvas = new SKCanvas(pixels))
                using (var paint = new SKPaint { Color = new SKColor(180, 190, 210, (byte)(opaqueLayer && i == 5 ? 255 : 120)) })
                    if (opaqueLayer && i == 5) canvas.DrawPaint(paint);
                    else canvas.DrawRect(i * 180, i * 80, 3000 - i * 260, 1300 + i * 240, paint);
                var layer = Layer.Raster($"Layer {i}", pixels);
                session.Document.Layers.Add(layer);
                if (i != 5) continue;
                layer.Effects = new LayerEffects { Shadow = new() { Blur = 45, Distance = 19 }, Stroke = new() { Size = 5 } };
                session.Document.SetActive(layer.Id);
            }
        }

        window.AddSession(session);
        window.SelectTool(Tool.Brush);
        session.Foreground = new SKColor(165, 32, 44);
        var fullOutline = Environment.GetEnvironmentVariable("COMPOSA_PERF_FULL_OUTLINE") == "1";
        session.Brush = new BrushSettings { Size = size, Hardness = .42, Flow = .62, Opacity = .85, FullCursor = fullOutline };
        window.Canvas.Fit();
        if (actualSize) window.Canvas.ZoomTo(1);
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        var arrow = LoadCursor(0, 32512);
        window.AddHandler(InputElement.PointerMovedEvent, Enter, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerMovedEvent, Leave, RoutingStrategies.Bubble, true);
        window.AddHandler(InputElement.PointerPressedEvent, Enter, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerPressedEvent, Leave, RoutingStrategies.Bubble, true);
        await Task.Delay(900);
        window.Activate();
        if (!GetCursorPos(out var cursorBefore)) throw new InvalidOperationException("Cannot read the desktop pointer.");
        var viewCenter = window.Canvas.ViewportCenter;
        var (_, park) = Position(actualSize ? viewCenter + new SKPoint(0, 130) : new SKPoint(2048, 2880));
        if (!SetCursorPos(park.X, park.Y)) throw new InvalidOperationException("Cannot park the desktop pointer inside the test window.");
        await Task.Delay(200);

        var samples = new List<Sample>();
        timeBeginPeriod(1);
        try
        {
            for (int i = 0; i < 32; i++)
                await Measure("hover", Move, actualSize
                    ? viewCenter + new SKPoint(i % 2 == 0 ? -130 : 0, -100)
                    : new SKPoint(i % 2 == 0 ? 1410 : 2500, 1240));
            for (int i = 0; i < 8; i++)
            {
                var at = actualSize
                    ? viewCenter + new SKPoint(-240 + i % 4 * 150, -60 + i / 4 * 160)
                    : new SKPoint(1350 + i % 4 * 400, 1700 + i / 4 * 440);
                await Measure(i == 0 ? "cold" : "repeat", Down, at);
                var (xy, _) = Position(at);
                Send(Up, xy);
                await Task.Delay(160);
            }
            var strokeStart = actualSize ? viewCenter + new SKPoint(-270, -225) : new SKPoint(650, 580);
            var (start, _) = Position(strokeStart);
            Send(Down, start);
            await Task.Delay(150);
            for (int i = 1; i <= 12; i++)
                await Measure("drag", Move, strokeStart + new SKPoint(i * (actualSize ? 40 : 95), i % 4 * 18));
            var (end, _) = Position(strokeStart + new SKPoint(12 * (actualSize ? 40 : 95), 0));
            Send(Up, end);
            await Task.Delay(200);
        }
        finally
        {
            timeEndPeriod(1);
            SetCursorPos(cursorBefore.X, cursorBefore.Y);
        }

        if (session.Revision != 9) throw new InvalidOperationException($"Expected nine committed strokes, got {session.Revision}.");
        var result = new
        {
            scenario, size, fullOutline, opaqueLayer, editorGpu, editorDevice = GpuAcceleration.EditorDevice, remoteSession = GetSystemMetrics(0x1000) != 0,
            scaling = window.RenderScaling,
            screen = window.Screens.Primary!.Bounds.ToString(), window = window.ClientSize.ToString(), zoom = window.Canvas.Zoom,
            samples = samples.Select(s => new
            {
                phase = s.Phase, queueMs = Elapsed(s.Posted, s.Entered), inputMs = Elapsed(s.Entered, s.Returned),
                frameMs = Elapsed(s.Posted, s.Frame), observedMs = Elapsed(s.Posted, s.Observed),
                frameToDesktopMs = Elapsed(s.Frame, s.Observed), nativeCursor = s.NativeCursor, diagnostics = s.Diagnostics
            })
        };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        session.MarkSaved("brush-latency-probe.cmps", session.History.CurrentId);
        window.Close();
        return;

        async Task Measure(string phase, uint message, SKPoint at)
        {
            var (xy, center) = Position(at);
            var hover = phase == "hover";
            var cursorRadius = (int)Math.Round(session.Brush.CursorRadius(1) * window.Canvas.Zoom);
            var probe = hover ? new PixelPoint(center.X + cursorRadius, center.Y) : center;
            if (hover && WindowFromPoint(new WinPoint { X = center.X, Y = center.Y }) != hwnd)
                throw new InvalidOperationException("The pointer target is obscured by another window.");
            var sample = new Sample(phase);
            CanvasPerformanceTrace.Reset();
            await Task.Run(() =>
            {
                using var desktop = new DesktopProbe();
                var before = desktop.Read(probe, hover);
                sample.Posted = Stopwatch.GetTimestamp();
                Volatile.Write(ref pending, sample);
                if (hover)
                {
                    if (!SetCursorPos(center.X, center.Y)) throw new InvalidOperationException("Cannot move the test pointer.");
                }
                else Send(message, xy, drag: phase == "drag");
                while (Stopwatch.GetElapsedTime(sample.Posted).TotalMilliseconds < (message == Down ? 4000 : 400))
                {
                    if (hover && NativeCursorAt(center, arrow))
                    {
                        sample.NativeCursor = true;
                        sample.Observed = Stopwatch.GetTimestamp();
                        break;
                    }
                    if (desktop.Read(probe, hover) != before)
                    {
                        sample.Observed = Stopwatch.GetTimestamp();
                        break;
                    }
                    Thread.Sleep(1);
                }
            });
            // Wait for the routed event even when Windows moved the native cursor first.
            await Task.Delay(20 + (samples.Count * 13) % 29);
            Volatile.Write(ref pending, null);
            var frame = CanvasPerformanceTrace.FrameAt;
            if (!sample.NativeCursor && frame >= sample.Posted && (sample.Observed == 0 || frame <= sample.Observed))
                sample.Frame = frame;
            sample.Diagnostics = CanvasPerformanceTrace.Snapshot();
            if (message == Down && (sample.Returned == 0 || sample.Observed == 0))
                throw new InvalidOperationException($"{phase}: entered={sample.Entered != 0}, returned={sample.Returned != 0}, "
                    + $"changed={sample.Observed != 0}, point={probe}. Keep the window visible.");
            samples.Add(sample);
        }

        (nint Packed, PixelPoint Screen) Position(SKPoint point)
        {
            var screen = window.Canvas.PointToScreen(window.Canvas.ToScreen(point));
            var root = window.PointToScreen(default);
            return ((nint)(((screen.Y - root.Y) << 16) | ((screen.X - root.X) & 0xffff)), screen);
        }
        void Send(uint message, nint xy, bool drag = false)
        {
            if (!PostMessage(hwnd, message, message == Down || drag ? 1 : 0, xy))
                throw new InvalidOperationException($"PostMessage failed for {message:X}.");
        }
    }

    private sealed class DesktopProbe : IDisposable
    {
        private readonly nint desktop, buffer, bitmap, previous, pixels;

        public DesktopProbe()
        {
            desktop = GetDC(0);
            if (desktop == 0) throw new InvalidOperationException("Cannot read the session desktop.");
            buffer = CreateCompatibleDC(desktop);
            var header = new BitmapHeader { Size = 40, Width = 3, Height = -1, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(desktop, ref header, 0, out pixels, 0, 0);
            if (buffer != 0 && bitmap != 0) previous = SelectObject(buffer, bitmap);
            if (previous != 0 && previous != -1) return;
            Dispose();
            throw new InvalidOperationException("Cannot create the desktop probe bitmap.");
        }

        public (int, int, int) Read(PixelPoint at, bool hover)
        {
            // Capture the whole sample in one desktop read. Three separate GetPixel
            // calls can each block on the RDP graphics surface.
            if (!BitBlt(buffer, 0, 0, hover ? 3 : 1, 1, desktop, at.X - (hover ? 1 : 0), at.Y, 0x00CC0020))
                throw new InvalidOperationException($"Cannot read desktop pixels at {at}.");
            GdiFlush();
            int ColorAt(int offset) => Marshal.ReadInt32(pixels, offset) & 0xffffff;
            return (ColorAt(0), hover ? ColorAt(4) : 0, hover ? ColorAt(8) : 0);
        }

        public void Dispose()
        {
            if (previous != 0 && previous != -1) SelectObject(buffer, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (buffer != 0) DeleteDC(buffer);
            if (desktop != 0) ReleaseDC(0, desktop);
        }
    }

    private static double? Elapsed(long start, long end) =>
        start > 0 && end >= start ? Stopwatch.GetElapsedTime(start, end).TotalMilliseconds : null;
    private static bool NativeCursorAt(PixelPoint position, nint arrow)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref cursor) && cursor.Flags == 1 && cursor.Handle != 0 && cursor.Handle != arrow
            && cursor.Position.X == position.X && cursor.Position.Y == position.Y;
    }
    private static void Enter(object? sender, PointerEventArgs args)
    {
        if (Volatile.Read(ref pending) is { Entered: 0 } state) state.Entered = Stopwatch.GetTimestamp();
    }
    private static void Leave(object? sender, PointerEventArgs args)
    {
        if (Volatile.Read(ref pending) is { Entered: > 0, Returned: 0 } state) state.Returned = Stopwatch.GetTimestamp();
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct WinPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo
    {
        public int Size, Flags;
        public nint Handle;
        public WinPoint Position;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XResolution, YResolution;
        public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(WinPoint point);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint module, nint name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WinPoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint source);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint op);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
