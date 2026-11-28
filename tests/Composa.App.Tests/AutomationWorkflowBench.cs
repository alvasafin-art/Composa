#if CANVAS_PERFORMANCE
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Assistant;
using Composa.App.Automation;
using Composa.App.Mcp;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Deterministic card production using the same native tools and live-layer checks as agent acceptance.</summary>
public sealed class AutomationWorkflowBench
{
    private const int AccentCount = 20;
    private const int Width = 3200, Height = 2000;
    private sealed record Variant(string Id, string Title, string Color, string Image);
    private sealed record Phase(string Name, double Ms, long Allocated, CanvasPerformanceTrace.Stage[] Stages);
    private sealed record Sample(string Mode, string Id, double Total, int Calls, int Draws, int Invalidations, Phase[] Phases);

    [AvaloniaFact]
    public async Task Editable_card_series()
    {
        if (Environment.GetEnvironmentVariable("COMPOSA_AUTOMATION_REPLAY") is not { } target) return;
        var root = Path.GetFullPath(target);
        if (Directory.Exists(root)) throw new IOException("Use a new replay directory.");
        Directory.CreateDirectory(root);
        var cancellation = TestContext.Current.CancellationToken;
        GpuAcceleration.Configure(filters: false, editor: false);
        var runtime = new JavaScriptRuntime();
        var window = new MainWindow { Width = 1200, Height = 800 };
        var settings = window.Settings;
        settings.AssistantReadFolders = [root];
        settings.AssistantVision = true;
        var samples = new List<Sample>();
        var reference = new Dictionary<string, (string Export, string Layers)>();
        var variants = new[] { new Variant("spring", "Spring Studio", "#2D9BA4", Path.Combine(root, "spring-image.png")),
            new Variant("autumn", "Autumn Studio", "#CF8958", Path.Combine(root, "autumn-image.png")) };
        var templateStart = Stopwatch.GetTimestamp();
        string template = Path.Combine(root, "template.cmps");
        ProjectFile.Save(CreateTemplate(), template);
        for (var i = 0; i < variants.Length; i++)
        {
            using var image = Texture(1000, 720, 40 + i * 13);
            ImageFiles.Save(image, variants[i].Image, ExportFormat.Png);
        }
        var prepared = Ms(templateStart);
        var load = Stopwatch.GetTimestamp();
        var document = ProjectFile.Load(template);
        var loaded = Ms(load);
        var before = Structure(document);
        window.Show();
        Pump();
        try
        {
            var modes = new[] { "pieces", "native-batch", "script" };
            if (Environment.GetEnvironmentVariable("COMPOSA_AUTOMATION_REVERSE") == "1") Array.Reverse(modes);
            for (var repeat = 0; repeat < 3; repeat++)
                foreach (var mode in modes)
                    foreach (var variant in variants)
                        await Run(mode, variant, repeat);
            var probes = new List<Phase>();
            var previewSession = new EditorSession(document.Clone());
            await Measure(probes, "Assistant initial vision cold", () =>
            {
                var composite = Stopwatch.GetTimestamp();
                var bitmap = previewSession.Composite();
                CanvasPerformanceTrace.StageCompleted("Preview full Composite", composite);
                Assert.StartsWith("data:image/png;base64,", AssistantFile.Preview(bitmap));
                return Task.CompletedTask;
            });
            await CheckRollback();
            await Measure(probes, "AI fake, one image edit", TraceAi);
            foreach (var variant in variants)
            {
                await Measure(probes, "Detached script and publish " + variant.Id, async () => Assert.True(await RunDetached(variant)));
                await Measure(probes, "Detached idempotent retry " + variant.Id, async () => Assert.False(await RunDetached(variant)));
            }
            var equivalent = reference.Select(pair => new { id = pair.Key, png = pair.Value.Export,
                layers = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pair.Value.Layers))) });
            File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { prepared, loaded, samples, probes, equivalent },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var remaining in window.Sessions) remaining.MarkEmbeddedSaved();
            window.Close();
        }

        async Task Run(string mode, Variant variant, int repeat)
        {
            var total = Stopwatch.GetTimestamp();
            var phases = new List<Phase>();
            var prep = Stopwatch.GetTimestamp();
            var session = new EditorSession(document.Clone());
            window.AddSession(session);
            var native = new ComposaTools(window, session);
            var tools = new AssistantEditorTools(window, session, runtime, NoAi.Instance, settings, [], allowExport: true);
            var final = Path.Combine(root, $"{repeat}-{mode}-{variant.Id}");
            var stage = final + ".pending";
            Directory.CreateDirectory(stage);
            var png = Path.Combine(stage, "card.png");
            var cmps = Path.Combine(stage, "card.cmps");
            var calls = 0;
            var draws = 0;
            var invalidations = 0;
            Pump();
            phases.Add(new("Prepare session and tools", Ms(prep), 0, []));
            void Frame(long _) => draws++;
            void Invalidated(SKRectI? _) => invalidations++;
            CanvasPerformanceTrace.CanvasDrawCompleted += Frame;
            session.CanvasChanged += Invalidated;
            try
            {
                await Measure(phases, "MCP preview cold", () => Preview(native));
                await Measure(phases, "MCP preview warm", () => Preview(native));
                if (mode == "script")
                {
                    await Measure(phases, "Apply via one script, including PNG export", () => Ask("execute_script", new { code = Script(variant, png) }, redraw: true));
                }
                else
                {
                    await Measure(phases, "Apply via native tools", () => session.RunTransactionAsync("Make card", async _ =>
                    {
                        await Ask("set_text", new { layer = "Title", text = variant.Title }, true);
                        await Ask("delete_layer", new { layer = "Hero" }, true);
                        await Ask("import_file", new { path = variant.Image }, true);
                        var imported = Path.GetFileNameWithoutExtension(variant.Image);
                        await Ask("set_layer", new { layer = imported, name = "Hero" }, true);
                        await Ask("transform_layer", new { layer = "Hero", x = 1450, y = 400, width = 1320, height = 950 }, true);
                        if (mode == "native-batch")
                            await Ask("batch_set_layers", new { query = new { kind = "shape", tag = "accent" }, shapeColor = variant.Color, opacity = .86 }, true);
                        else
                            for (var i = 0; i < AccentCount; i++)
                            {
                                await Ask("set_shape", new { layer = $"Accent {i}", color = variant.Color }, true);
                                await Ask("set_layer", new { layer = $"Accent {i}", opacity = .86 }, true);
                            }
                    }));
                    await Measure(phases, "Native export PNG", async () => { calls++; await native.ExportImage(png); });
                }
                await Measure(phases, "Verify MCP preview", () => Preview(native));
                await Measure(phases, "Save editable CMPS and publish item", async () =>
                {
                    var revision = session.History.CurrentId;
                    var snapshot = session.Document.Clone();
                    await Task.Run(() => ProjectFile.Save(snapshot, cmps), cancellation);
                    Assert.Equal(revision, session.History.CurrentId);
                    cancellation.ThrowIfCancellationRequested();
                    Directory.Move(stage, final); // The two outputs become visible together. Existing variants are refused.
                });
                samples.Add(new(mode, variant.Id, Ms(total), calls, draws, invalidations, phases.ToArray()));
            }
            finally
            {
                session.CanvasChanged -= Invalidated;
                CanvasPerformanceTrace.CanvasDrawCompleted -= Frame;
            }
            Assert.Equal(1, session.History.Count);
            var after = Structure(session.Document);
            session.Undo(); Assert.Equal(before, Structure(session.Document));
            session.Redo(); Assert.Equal(after, Structure(session.Document));
            var saved = ProjectFile.Load(Path.Combine(final, "card.cmps"));
            Assert.Equal(after, Structure(saved));
            Assert.Equal(variant.Title, saved.AllLayers().Single(layer => layer.Name == "Title").Text!.Text);
            Assert.Equal(AccentCount, saved.AllLayers().Count(layer => layer.Tags.Contains("accent") && layer.Shape?.Fill == (uint)SKColor.Parse(variant.Color)));
            Assert.NotNull(saved.Layers.Single(layer => layer.Name == "Hero").Pixels);
            var result = (Export: Digest(Path.Combine(final, "card.png")), Layers: after);
            if (reference.TryGetValue(variant.Id, out var previous)) Assert.Equal(previous, result);
            else reference.Add(variant.Id, result);
            session.MarkEmbeddedSaved();
            await CloseTab(window, session);

            async Task Ask(string name, object args, bool redraw)
            {
                calls++;
                await tools.ExecuteAsync(new(calls.ToString(), name, JsonSerializer.SerializeToElement(args)), cancellation);
                if (redraw) Pump();
            }

            async Task Preview(ComposaTools api)
            {
                calls++;
                var response = await api.Render(384);
                Assert.False(response.IsError ?? false);
            }
        }

        async Task CheckRollback()
        {
            var scratch = new EditorSession(document.Clone());
            var rollback = Path.Combine(root, "rolled-back.png");
            var code = "doc.findLayersByName('Title')[0].text='Discard';doc.export(" + JsonSerializer.Serialize(rollback) + ");";
            var fault = await Assert.ThrowsAnyAsync<Exception>(() => runtime.ExecuteAsync(scratch, code + "throw new Error('rollback');", NoAi.Instance, settings, cancellationToken: cancellation));
            Assert.Contains("rollback", fault.Message);
            Assert.Equal(before, Structure(scratch.Document));
            Assert.Equal(0, scratch.History.Count);
            Assert.False(File.Exists(rollback));
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ExecuteAsync(scratch, code + "ai.imageEdit('fixture');", new CancelAi(cancel), settings, cancellationToken: cancel.Token));
            Assert.Equal(before, Structure(scratch.Document));
            Assert.Equal(0, scratch.History.Count);
            Assert.False(File.Exists(rollback));
        }

        async Task<bool> RunDetached(Variant variant)
        {
            // Each item owns a fresh transaction, an independent output pair and a stable destination.
            var final = Path.Combine(root, "detached-" + variant.Id);
            if (Directory.Exists(final))
            {
                Assert.Equal(reference[variant.Id].Export, Digest(Path.Combine(final, "card.png")));
                Assert.Equal(reference[variant.Id].Layers, Structure(ProjectFile.Load(Path.Combine(final, "card.cmps"))));
                return false;
            }
            var staging = Directory.CreateDirectory(Path.Combine(root, "pending-" + Guid.NewGuid().ToString("N"))).FullName;
            var png = Path.Combine(staging, "card.png");
            var scratch = new EditorSession(document.Clone());
            await runtime.ExecuteAsync(scratch, Script(variant, png), NoAi.Instance, new Settings { AssistantReadFolders = [root] },
                cancellationToken: cancellation);
            Assert.Equal(1, scratch.History.Count);
            var revision = scratch.History.CurrentId;
            var snapshot = scratch.Document.Clone();
            await Task.Run(() => ProjectFile.Save(snapshot, Path.Combine(staging, "card.cmps")), cancellation);
            Assert.Equal(revision, scratch.History.CurrentId);
            Assert.Equal(reference[variant.Id], (Digest(png), Structure(snapshot)));
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(staging, final);
            return true;
        }

        async Task TraceAi()
        {
            using var fixture = Texture(512, 320, 19);
            var encoded = ImageFiles.Encode(fixture, ExportFormat.Png);
            var serviceFake = new AiVariantsTests.Connection
            {
                InspectUpload = (semantic, bitmap) => { _ = ImageFiles.Encode(bitmap, ExportFormat.Png); },
                DownloadImage = () => { using var stream = new MemoryStream(encoded, writable: false); return ImageFiles.Load(stream); }
            };
            var service = new AiTaskService(() => serviceFake.Address.ToString(), Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => serviceFake);
            var scratch = EditorSession.NewCanvas(512, 320, SKColors.White);
            var call = Stopwatch.GetTimestamp();
            await runtime.ExecuteAsync(scratch, "ai.imageEdit('fixture', {width:512,height:320,seed:17});", service, settings, cancellationToken: cancellation);
            CanvasPerformanceTrace.StageCompleted("AI script call", call);
            Assert.Single(serviceFake.Graphs);
            Assert.NotEmpty(serviceFake.Uploads);
            Assert.Equal(1, scratch.History.Count);
        }
    }

    private static async Task Measure(List<Phase> phases, string name, Func<Task> work)
    {
        CanvasPerformanceTrace.Reset();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        await work();
        phases.Add(new(name, Ms(start), GC.GetTotalAllocatedBytes(precise: true) - allocated, CanvasPerformanceTrace.Snapshot().Stages));
    }

    private static double Ms(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    private static string Digest(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    private static string Script(Variant variant, string png) => $$"""
        const card = app.activeDocument;
        card.findLayersByName('Title')[0].text = {{JsonSerializer.Serialize(variant.Title)}};
        card.findLayersByName('Hero')[0].remove();
        const hero = card.addImageFile({{JsonSerializer.Serialize(variant.Image)}});
        hero.name = 'Hero'; hero.transform = {x:1450, y:400, width:1320, height:950};
        for (const accent of card.findLayersByTag('accent')) {
            accent.setShapeColor({{JsonSerializer.Serialize(variant.Color)}}); accent.opacity = 0.86;
        }
        card.export({{JsonSerializer.Serialize(png)}});
        """;

    private static Document CreateTemplate()
    {
        var session = EditorSession.NewCanvas(Width, Height);
        session.ActiveLayer!.Pixels = Texture(Width, Height, 19);
        for (var i = 0; i < AccentCount; i++)
        {
            var shape = session.AddShape(new(ShapeKind.Rectangle, (uint)SKColors.DarkSlateGray, 0),
                SKRect.Create(140 + i * 112, 1450 + i % 3 * 64, 90, 85))!;
            session.Rename(shape, $"Accent {i}");
            session.SetLayerTags(shape, ["accent"]);
        }
        var title = session.AddText(new(130, 160), new() { Text = "Template title", Size = 120, Bold = true, Color = (uint)SKColors.White });
        session.Rename(title, "Title");
        session.Rename(session.AddImageLayer("Hero", Texture(1000, 720, 59)), "Hero");
        return session.Document;
    }

    private static SKBitmap Texture(int width, int height, int seed)
    {
        var bitmap = Pixels.NewColor(width, height);
        var bytes = new byte[width * height * 4];
        new Random(seed).NextBytes(bytes);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = (byte)(70 + (bytes[i] >> 2));
            bytes[i + 1] = (byte)(46 + seed + (bytes[i + 1] >> 2));
            bytes[i + 2] = (byte)(88 + (bytes[i + 2] >> 2));
            bytes[i + 3] = 255;
        }
        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    private static string Structure(Document document) => JsonSerializer.Serialize(document.AllLayers().Select(layer => new
    {
        layer.Name, layer.Kind, layer.Opacity, layer.Visible, layer.Transform, layer.Tags, layer.Shape, layer.Text,
        pixels = layer.Kind == LayerKind.Raster && layer.Pixels is { } bitmap ? Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan())) : null
    }));

    private static async Task CloseTab(MainWindow window, EditorSession session)
    {
        var item = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Avalonia.Controls.MenuItem>()
            .Single(menu => Equals(menu.Header, "Close Project"));
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        for (var i = 0; i < 100 && window.Sessions.Contains(session); i++) { Pump(); await Task.Yield(); }
        Assert.DoesNotContain(session, window.Sessions);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class NoAi : IAiTaskRunner
    {
        internal static NoAi Instance { get; } = new();
        public Task RunAsync(IEditorCommandService editor, AiTaskRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The card replay does not request AI.");
    }

    private sealed class CancelAi(CancellationTokenSource source) : IAiTaskRunner
    {
        public Task RunAsync(IEditorCommandService editor, AiTaskRequest request, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }
}
#endif
