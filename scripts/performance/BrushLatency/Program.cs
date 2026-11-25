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

internal static class BrushLatency
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
        if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] is not ("simple" or "layered"))
        {
            Console.Error.WriteLine("Run on a visible Windows desktop: BrushLatency <simple|layered> <output.json>");
            return 2;
        }
        Settings.Persist = false;
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        GpuAcceleration.Configure(filters: false, editor: false);
        var window = new MainWindow { Width = 1200, Height = 760, Position = new PixelPoint(20, 20), Topmost = true };
        using var done = new CancellationTokenSource();
        Exception? failure = null;
        window.Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Run(window, args[0], args[1]); }
            catch (Exception ex) { failure = ex; }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        if (failure == null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static async Task Run(MainWindow window, string scenario, string output)
    {
        var session = EditorSession.NewCanvas(4096, 3072, SKColors.White);
        if (scenario == "layered")
        {
            for (int i = 1; i <= 5; i++)
            {
                var pixels = EditorSession.NewCanvas(4096, 3072).ActiveLayer!.Pixels!;
                using (var canvas = new SKCanvas(pixels))
                using (var paint = new SKPaint { Color = new SKColor(180, 190, 210, 120) })
                    canvas.DrawRect(i * 180, i * 80, 3000 - i * 260, 1300 + i * 240, paint);
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
        session.Brush = new BrushSettings { Size = 150, Hardness = .42, Flow = .62, Opacity = .85 };
        window.Canvas.Fit();
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        var arrow = LoadCursor(0, 32512);
        window.AddHandler(InputElement.PointerMovedEvent, Enter, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerMovedEvent, Leave, RoutingStrategies.Bubble, true);
        window.AddHandler(InputElement.PointerPressedEvent, Enter, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerPressedEvent, Leave, RoutingStrategies.Bubble, true);
        await Task.Delay(900);
        window.Activate();
        if (!GetCursorPos(out var cursorBefore)) throw new InvalidOperationException("Cannot read the desktop pointer.");
        var (_, park) = Position(new SKPoint(2048, 2880));
        if (!SetCursorPos(park.X, park.Y)) throw new InvalidOperationException("Cannot park the desktop pointer inside the test window.");
        await Task.Delay(200);

        var samples = new List<Sample>();
        timeBeginPeriod(1);
        try
        {
            for (int i = 0; i < 32; i++)
                await Measure("hover", Move, new SKPoint(i % 2 == 0 ? 1410 : 2500, 1240));
            for (int i = 0; i < 8; i++)
            {
                var at = new SKPoint(1350 + i % 4 * 400, 1700 + i / 4 * 440);
                await Measure(i == 0 ? "cold" : "repeat", Down, at);
                var (xy, _) = Position(at);
                Send(Up, xy);
                await Task.Delay(160);
            }
        }
        finally
        {
            timeEndPeriod(1);
            SetCursorPos(cursorBefore.X, cursorBefore.Y);
        }

        if (session.Revision != 8) throw new InvalidOperationException($"Expected eight committed dabs, got {session.Revision}.");
        var result = new
        {
            scenario, remoteSession = GetSystemMetrics(0x1000) != 0, scaling = window.RenderScaling,
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
            var cursorRadius = (int)Math.Round(session.Brush.CursorRadius(1) * window.Canvas.Zoom);
            var probe = message == Move ? new PixelPoint(center.X + cursorRadius, center.Y) : center;
            if (message == Move && WindowFromPoint(new WinPoint { X = center.X, Y = center.Y }) != hwnd)
                throw new InvalidOperationException("The pointer target is obscured by another window.");
            var sample = new Sample(phase);
            CanvasPerformanceTrace.Reset();
            await Task.Run(() =>
            {
                var dc = GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) throw new InvalidOperationException("Cannot read the session desktop.");
                try
                {
                    var before = Read(dc, probe, message == Move);
                    sample.Posted = Stopwatch.GetTimestamp();
                    Volatile.Write(ref pending, sample);
                    if (message == Move)
                    {
                        if (!SetCursorPos(center.X, center.Y)) throw new InvalidOperationException("Cannot move the test pointer.");
                    }
                    else Send(message, xy);
                    while (Stopwatch.GetElapsedTime(sample.Posted).TotalMilliseconds < (message == Move ? 400 : 2000))
                    {
                        if (message == Move && NativeCursorAt(center, arrow))
                        {
                            sample.NativeCursor = true;
                            sample.Observed = Stopwatch.GetTimestamp();
                            break;
                        }
                        if (Read(dc, probe, message == Move) != before)
                        {
                            sample.Observed = Stopwatch.GetTimestamp();
                            break;
                        }
                        Thread.Sleep(1);
                    }
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
            });
            // Wait for the routed event even when Windows moved the native cursor first.
            await Task.Delay(20 + (samples.Count * 13) % 29);
            Volatile.Write(ref pending, null);
            var frame = CanvasPerformanceTrace.FrameAt;
            if (!sample.NativeCursor && frame >= sample.Posted && (sample.Observed == 0 || frame <= sample.Observed))
                sample.Frame = frame;
            sample.Diagnostics = CanvasPerformanceTrace.Snapshot();
            if (message != Move && (sample.Returned == 0 || sample.Observed == 0))
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
        void Send(uint message, nint xy)
        {
            if (!PostMessage(hwnd, message, message == Down ? 1 : 0, xy))
                throw new InvalidOperationException($"PostMessage failed for {message:X}.");
        }
    }

    private static ulong Read(nint dc, PixelPoint at, bool hover)
    {
        var a = GetPixel(dc, at.X, at.Y);
        if (!hover) return a;
        var b = GetPixel(dc, at.X + 1, at.Y);
        var c = GetPixel(dc, at.X - 1, at.Y);
        return ((ulong)a << 32) ^ ((ulong)b << 12) ^ c;
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
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
