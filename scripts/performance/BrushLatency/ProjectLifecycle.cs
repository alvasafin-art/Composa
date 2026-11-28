using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Composa.App;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

internal static class ProjectLifecycle
{
    internal static int MakeFixtures(string folder, string photoPath)
    {
        Directory.CreateDirectory(folder);
        using var photo = ImageFiles.Load(photoPath, preserveProfile: true);
        Save(768, 512, ColorDepth.Eight, RgbProfile.Srgb, "card");
        Save(3840, 2160, ColorDepth.Sixteen, RgbProfile.DisplayP3, "banner");
        return 0;

        void Save(int width, int height, ColorDepth depth, RgbProfile profile, string name)
        {
            var document = new Document(width, height) { Depth = depth, Profile = profile };
            document.Layers.Add(Layer.Raster("Background", Bitmap(width, height, false)));
            var layer = Layer.Raster("Feature", Bitmap(width * 3 / 4, height * 4 / 5, true), width / 8, height / 10);
            layer.Mask = Pixels.NewMask(layer.Pixels!.Width, layer.Pixels.Height, 190);
            using (var canvas = new SKCanvas(layer.Mask))
            using (var cut = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true })
                canvas.DrawCircle(layer.Mask.Width / 4f, layer.Mask.Height / 2f, layer.Mask.Height / 5f, cut);
            layer.Effects = new() { Shadow = new() { Blur = 26, Distance = 12 } };
            var group = Layer.Group("Art");
            group.Children.Add(layer);
            document.Layers.Add(group);
            var nested = new Document(width / 4, height / 4) { Depth = depth, Profile = profile };
            nested.Layers.Add(Layer.Raster("Embedded photograph", Bitmap(nested.Width, nested.Height, false)));
            var objectSource = SmartObjectSource.Create(nested);
            var embedded = Layer.Raster("Embedded", objectSource.Preview, width * 3 / 5, height * 2 / 3);
            embedded.SmartObject = objectSource;
            document.Layers.Add(embedded);
            document.SetActive(layer.Id);
            ProjectFile.Save(document, Path.Combine(folder, name + ".cmps"));

            SKBitmap Bitmap(int w, int h, bool flip)
            {
                var bitmap = Pixels.NewWorkingColor(w, h, depth, profile);
                using var canvas = new SKCanvas(bitmap);
                if (flip) { canvas.Translate(w, 0); canvas.Scale(-1, 1); }
                using var image = SKImage.FromBitmap(photo);
                canvas.DrawImage(image, SKRect.Create(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear));
                return bitmap;
            }
        }
    }

    internal static int Run(string folder, string output, string firstProject)
    {
        if (!OperatingSystem.IsWindows() || firstProject is not ("card" or "banner")) return 2;
        var started = Stopwatch.GetTimestamp();
        var outFolder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(outFolder);
        var profile = Path.Combine(outFolder, Path.GetFileNameWithoutExtension(output) + "-profile");
        Environment.SetEnvironmentVariable("COMPOSA_PROFILE_DIR", profile);
        Environment.SetEnvironmentVariable("COMPOSA_EXCHANGE_PIPE", "composa-bench-" + Environment.ProcessId);
        Settings.Persist = Environment.GetEnvironmentVariable("COMPOSA_PERF_RECENT") != "0";
        new Settings { EditorWelcomeSeen = true, LastUpdateCheck = DateTime.Now, ComfyServerUrl = "http://127.0.0.1:1" }
            .SaveTo(Path.Combine(profile, "config", "settings.json"));
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var memory = new MemorySampler();
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        GpuAcceleration.Configure(filters: false, editor: false);
        var window = new MainWindow { Width = 1280, Height = 840, Position = new PixelPoint(10, 10) };
        using var done = new CancellationTokenSource();
        Exception? failure = null;
        window.Show();
        var shown = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Replay(window, folder, output, profile, firstProject, started, shown, memory); }
            catch (Exception error) { failure = error; }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        if (failure == null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static async Task Replay(MainWindow window, string folder, string output, string profile, string firstProject,
        long started, double shown, MemorySampler memory)
    {
        var measurements = new List<object>();
        var gaps = new List<double>();
        var lastTick = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { var now = Stopwatch.GetTimestamp(); gaps.Add(Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds); lastTick = now; };
        timer.Start();
        var previewFolder = Path.Combine(profile, "cache", "recent-previews");
        var work = Path.Combine(profile, "copies");
        Directory.CreateDirectory(work);
        var paths = new[] { firstProject, firstProject == "card" ? "banner" : "card" }
            .Select(name => Path.GetFullPath(Path.Combine(folder, name + ".cmps"))).ToArray();
        var closed = new List<WeakReference<EditorSession>>();
        var frames = new List<object>();
        var shortRun = Environment.GetEnvironmentVariable("COMPOSA_PROJECT_SHORT") == "1";
        var ready = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long? startupDocument = null;

        foreach (var path in paths)
            await Open(path, "cold in process " + Path.GetFileName(path));
        for (var pass = 0; pass < (shortRun ? 3 : 7); pass++)
            foreach (var path in paths)
                await Open(path, "switch " + Path.GetFileName(path), waitForPreview: false);

        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var session = (await window.OpenPath(path))!;
            var recoveredFolder = Path.Combine(profile, "test-recovery-" + name);
            var recovery = new Recovery(recoveredFolder);
            var destination = Path.Combine(work, name + ".cmps");
            for (var pass = 0; pass < (shortRun ? 3 : 4); pass++)
            {
                session.Rename(session.ActiveLayer!, "Feature " + pass);
                await Measure($"{name} save {(pass == 0 ? "first" : "metadata")}", async () =>
                {
                    if (await window.SaveTo(session, destination) is { } error) throw error;
                }, () => new FileInfo(destination).Length);
                await WaitForPreview(destination);
            }
            // Change the revision each time while keeping the source rasters shared.
            for (var pass = 0; pass < (shortRun ? 1 : 3); pass++)
            {
                session.Rename(session.ActiveLayer!, "Recovery " + pass);
                await Measure(name + " autosave", () => recovery.Save([session]));
            }
            var recoveryPath = Directory.GetFiles(recoveredFolder, "*.cmps").Last();
            await Measure(name + " restore file", () => Task.Run(() =>
            {
                var document = ProjectFile.Load(recoveryPath);
                if (!document.Profile.Equals(session.Document.Profile))
                    throw new InvalidOperationException("Recovery changed the document profile.");
            }));
            var png = Path.Combine(work, name + ".png");
            await Measure(name + " export PNG", () => Task.Run(() =>
            {
                var render = Stopwatch.GetTimestamp();
                using var flattened = DocumentRenderer.Flatten(session.Document.Clone());
                CanvasPerformanceTrace.StageCompleted("Export flatten", render);
                var encode = Stopwatch.GetTimestamp();
                ImageFiles.Save(flattened, png, ExportFormat.Png, 100);
                CanvasPerformanceTrace.StageCompleted("Export encode and write", encode);
            }), () => new FileInfo(png).Length);
            session.MarkSaved(destination, session.History.CurrentId);
            await Close(session);
        }

        for (var pass = 0; pass < (shortRun ? 1 : 3); pass++)
        {
            foreach (var path in paths)
                await Open(path, "reopen " + Path.GetFileName(path));
            await Measure("close both tabs", async () =>
            {
                while (window.Sessions.Count > 0) await Close(window.Canvas.Session!);
                await Task.Delay(500);
            });
        }
        await Task.Delay(1500);
        timer.Stop();
        File.WriteAllText(output, JsonSerializer.Serialize(new { shown, ready, startupDocument, startupDocumentMs = startupDocument is { } at ?
            (double?)Stopwatch.GetElapsedTime(started, at).TotalMilliseconds : null, frames, measurements, afterClose = memory.Read(),
            closed = closed.Count, closedAlive = closed.Count(item => item.TryGetTarget(out _)) }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Open(string path, string label, bool waitForPreview = true)
        {
            var draw = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Rendered(long at)
            {
                if (window.Canvas.Session?.FilePath == path) draw.TrySetResult(at);
            }
            CanvasPerformanceTrace.CanvasDrawCompleted += Rendered;
            try
            {
                await Measure(label, async () =>
                {
                    var invoked = Stopwatch.GetTimestamp();
                    var load = window.OpenPath(path);
                    var uiReturn = Stopwatch.GetElapsedTime(invoked).TotalMilliseconds;
                    var session = await load ?? throw new InvalidOperationException("Open returned no document.");
                    var loadMs = Stopwatch.GetElapsedTime(invoked).TotalMilliseconds;
                    var frame = await draw.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    startupDocument ??= frame;
                    frames.Add(new { label, uiReturn, loadMs, canvasDrawMs = Stopwatch.GetElapsedTime(invoked, frame).TotalMilliseconds });
                    if (waitForPreview) await WaitForPreview(path);
                });
            }
            finally { CanvasPerformanceTrace.CanvasDrawCompleted -= Rendered; }
        }

        async Task WaitForPreview(string path)
        {
            if (!Settings.Persist) return;
            var at = Stopwatch.GetTimestamp();
            while (!RecentPreviewExists(path) && Stopwatch.GetElapsedTime(at) < TimeSpan.FromSeconds(12)) await Task.Delay(15);
        }

        bool RecentPreviewExists(string path)
        {
            var full = Path.GetFullPath(path).ToUpperInvariant();
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)));
            var cache = Path.Combine(previewFolder, key + ".png");
            return File.Exists(cache) && File.GetLastWriteTimeUtc(cache) == File.GetLastWriteTimeUtc(path);
        }

        async Task Close(EditorSession session)
        {
            var item = window.GetLogicalDescendants().OfType<MenuItem>().Single(menu => Equals(menu.Header, "Close Project"));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            for (var attempt = 0; window.Sessions.Contains(session) && attempt < 100; attempt++) await Task.Delay(10);
            if (window.Sessions.Contains(session)) throw new InvalidOperationException("Tab did not close.");
            closed.Add(new(session));
        }

        async Task Measure(string label, Func<Task> action, Func<long>? size = null)
        {
            CanvasPerformanceTrace.Reset();
            memory.Reset();
            gaps.Clear();
            var invoked = lastTick = Stopwatch.GetTimestamp();
            var allocatedAt = GC.GetTotalAllocatedBytes(precise: true);
            var current = action();
            var uiCall = Stopwatch.GetElapsedTime(invoked).TotalMilliseconds;
            await current;
            var elapsed = Stopwatch.GetElapsedTime(invoked).TotalMilliseconds;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedAt;
            var sampled = memory.Peak();
            var present = memory.Read();
            var sorted = gaps.Order().ToArray();
            var inputP95 = sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1];
            measurements.Add(new { label, elapsed, uiCall, allocated, sampled, present, inputN = sorted.Length, inputP95,
                inputMax = sorted.LastOrDefault(), fileBytes = size?.Invoke(), stages = CanvasPerformanceTrace.Snapshot().Stages });
        }
    }

    private sealed class MemorySampler : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Counters
        {
            public uint Size, Faults;
            public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
            public nuint Pagefile, PeakPagefile, PrivateCommit, PrivateWorkingSet;
            public ulong SharedCommit;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

        internal sealed record Sample(long? PrivateWorkingSet, long WorkingSet, long PrivateCommit, long ManagedLive);

        private readonly Process process = Process.GetCurrentProcess();
        private readonly Timer timer;
        private readonly object gate = new();
        private Sample peak = new(null, 0, 0, 0);

        internal MemorySampler() => timer = new Timer(_ => { lock (gate) Record(); }, null, 0, 15);

        private Sample Record()
        {
            var counters = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
            var success = GetProcessMemoryInfo(process.Handle, ref counters, counters.Size);
            if (!success) process.Refresh();
            var sample = new Sample(success ? (long)counters.PrivateWorkingSet : null, success ? (long)counters.WorkingSet : process.WorkingSet64,
                success ? (long)counters.PrivateCommit : process.PrivateMemorySize64, GC.GetTotalMemory(false));
            peak = new(sample.PrivateWorkingSet is { } pws ? Math.Max(peak.PrivateWorkingSet ?? 0, pws) : peak.PrivateWorkingSet,
                Math.Max(peak.WorkingSet, sample.WorkingSet), Math.Max(peak.PrivateCommit, sample.PrivateCommit),
                Math.Max(peak.ManagedLive, sample.ManagedLive));
            return sample;
        }

        internal void Reset() { lock (gate) peak = Record(); }
        internal Sample Read() { lock (gate) return Record(); }
        internal Sample Peak() { lock (gate) { Record(); return peak; } }
        public void Dispose() { using var finished = new ManualResetEvent(false); if (timer.Dispose(finished)) finished.WaitOne(); process.Dispose(); }
    }
}
