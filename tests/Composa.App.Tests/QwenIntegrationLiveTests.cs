using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

public class QwenIntegrationLiveTests
{
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_QWEN_INTEGRATION_URL"));

    [Fact(Skip = "Set COMPOSA_QWEN_INTEGRATION_URL, INPUT and OUTPUT for real Qwen integration runs.", SkipUnless = nameof(Enabled))]
    public async Task Real_service_runs_the_lighting_matrix_and_all_other_generative_operations()
    {
        var token = TestContext.Current.CancellationToken;
        var inputRoot = Environment.GetEnvironmentVariable("COMPOSA_QWEN_INTEGRATION_INPUT")!;
        var outputRoot = Environment.GetEnvironmentVariable("COMPOSA_QWEN_INTEGRATION_OUTPUT")!; Directory.CreateDirectory(outputRoot);
        var cases = new[] { "indoor-sun", "indoor-diffuse", "outdoor-sun", "outdoor-overcast", "generate", "edit", "fill", "remove", "expand", "relight", "background", "fill-edge-wide", "edit-reference" };
        if (Environment.GetEnvironmentVariable("COMPOSA_QWEN_INTEGRATION_CASES") is { Length: > 0 } selected) cases = selected.Split(',');
        var metrics = new List<object>();
        foreach (var name in cases)
        {
            var task = name switch { "generate" => AiTaskKind.GenerateImage, "edit" or "edit-reference" => AiTaskKind.ImageEdit, "fill" or "fill-edge-wide" => AiTaskKind.GenerativeFill,
                "remove" => AiTaskKind.RemoveObject, "expand-selection" or "expand" => AiTaskKind.GenerativeExpand, "relight" => AiTaskKind.Relight,
                "background" => AiTaskKind.ChangeBackground, _ => AiTaskKind.Harmonize };
            var width = name == "fill-edge-wide" ? 1536 : 768;
            var document = new Document(width, 576);
            using var photo = ImageFiles.Load(Path.Combine(inputRoot, task == AiTaskKind.Harmonize ? name + ".png" : "indoor-diffuse.png"));
            var backdrop = Pixels.NewColor(width, 576);
            using (var draw = new SKCanvas(backdrop)) draw.DrawImage(Pixels.ImageOf(photo), SKRect.Create(width, width * photo.Height / (float)photo.Width), new SKSamplingOptions(SKCubicResampler.Mitchell));
            Pixels.Invalidate(backdrop); document.Layers.Add(Layer.Raster("Background", backdrop));
            var png = ImageFiles.Load(Path.Combine(inputRoot, "subject.png")); var subject = Layer.Raster("Original PNG", png);
            using (var alpha = SceneEditInput.Alpha(png))
            {
                var bounds = SelectionMask.Bounds(alpha, 16); var scale = 210d / bounds.Height;
                subject.Transform = new() { X = 384 - (bounds.Left + bounds.Width / 2d) * scale, Y = 440 - bounds.Bottom * scale, Width = png.Width * scale, Height = png.Height * scale };
            }
            document.Layers.Add(subject); document.SetActive(subject.Id);
            var session = new EditorSession(document);
            if (name == "fill-edge-wide") session.SelectRect(new(0, 330, 115, 455));
            else if (task == AiTaskKind.GenerativeFill) session.SelectRect(new(150, 345, 240, 440));
            if (task == AiTaskKind.RemoveObject) session.SelectRect(new(340, 225, 425, 446));
            var prompt = task switch
            {
                AiTaskKind.GenerateImage => "Photograph of a blue ceramic cup on a wooden table near a sunlit window, realistic soft shadows, natural daylight",
                AiTaskKind.ImageEdit => "Change the bottle cap to blue. Keep the bottle, its label, placement and background unchanged.",
                AiTaskKind.GenerativeFill => "a small wooden cube resting on the table",
                AiTaskKind.ChangeBackground => "a sunlit wooden kitchen counter beside a window, photographic interior",
                _ => ""
            };
            var request = new AiTaskRequest { Task = task, Prompt = prompt, Scene = new() { Preset = task == AiTaskKind.Relight ? RelightPreset.GoldenHour : RelightPreset.MatchScene },
                ReferenceImages = name == "edit-reference" ? [png] : [],
                Settings = new() { Seed = 73, Width = width, Height = 576, Values = new() { ["imageOriginalSize"] = false } } };
            if (name == "expand-selection") session.SelectRect(new(340, 225, 425, 446));
            else if (task == AiTaskKind.GenerativeExpand) request = request with { ExpansionBounds = new(-64, -32, 832, 608), ExpansionMinimumSide = 1024 };
            var folder = Path.Combine(outputRoot, name); Directory.CreateDirectory(folder);
            var connection = new RecordingConnection(Environment.GetEnvironmentVariable("COMPOSA_QWEN_INTEGRATION_URL")!, folder);
            var clients=0;
            if(task==AiTaskKind.ChangeBackground) SceneBackgroundSubject.EnsureAvailable(token);
            var service = new AiTaskService(() => connection.Address.ToString(), Path.Combine(AppContext.BaseDirectory, "ai/engines"), url => ++clients==1 ? connection : new ComfyClient(url),
                (input,layout,cancellation)=>{var mask=SceneBackgroundSubject.Locate(input,layout,cancellation);ImageFiles.Save(mask,Path.Combine(folder,"temporary-subject.png"),ExportFormat.Png);return mask;});
            service.SelectedEngine = service.Engines.Find(QwenImageWorkflow.EngineId);
            using (var before = DocumentRenderer.Flatten(document)) Save(before, "before");
            using var scene = task is AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground ? SceneEditInput.Prepare(document.Clone(), request, token) : null;
            var state = session.History.CurrentId; var watch = Stopwatch.StartNew();
            await service.RunAsync(new EditorCommandService(session), request, token);
            Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
            using (var after = DocumentRenderer.Flatten(session.Document))
            {
                Save(after, "after");
                if (name == "fill-edge-wide")
                {
                    using var before = ImageFiles.Load(Path.Combine(folder, "before.png"));
                    Assert.Equal(before.Info.Size, after.Info.Size);
                    for (var y = 0; y < after.Height; y++)
                    for (var x = 140; x < after.Width; x++)
                        Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
                }
                if (name == "expand-selection")
                {
                    using var before = ImageFiles.Load(Path.Combine(folder, "before.png"));
                    Assert.Equal(before.Info.Size, after.Info.Size);
                    for (var y = 0; y < after.Height; y++)
                    for (var x = 0; x < after.Width; x++)
                        if (x < 320 || x >= 445 || y < 205 || y >= 466)
                            Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
                }
            }
            using (var file = File.Create(Path.Combine(folder, "result.cmps"))) ProjectFile.Write(session.Document, file);
            int contacts = 0, gaps = 0, strong = 0, farStrong = 0; double mass = 0, mx = 0, my = 0;
            if (scene != null)
            {
                var layer = Assert.Single(session.Document.AllLayers(), layer => layer.Visible && layer.Tags.Contains("scene-object"));
                for (var y = 0; y < 576; y++) for (var x = 0; x < 768; x++) Assert.Equal(scene.ObjectAlpha.GetPixel(x, y).Alpha, layer.Pixels!.GetPixel(x, y).Alpha);
                var shadow = session.Document.AllLayers().SingleOrDefault(layer => layer.Visible && layer.Tags.Contains("scene-shadow"))?.Pixels;
                Assert.NotNull(shadow); Save(shadow, "shadow");
                var center = scene.SupportBounds.Left + scene.SupportBounds.Width / 2d; var radius = Math.Max(16, scene.SupportBounds.Width * 1.5);
                for (var y = 0; y < 576; y++) for (var x = 0; x < 768; x++)
                {
                    var a = shadow.GetPixel(x, y).Alpha * (1 - scene.ObjectAlpha.GetPixel(x, y).Alpha / 255d);
                    mass += a; mx += a * x; my += a * y;
                    if (a >= 32) { strong++; if ((x - center) * (x - center) + (y - scene.SupportBounds.Bottom) * (y - scene.SupportBounds.Bottom) > radius * radius) farStrong++; }
                }
                for (var x = scene.SupportBounds.Left; x < scene.SupportBounds.Right; x++)
                {
                    var foot = scene.SupportY[x] + 1; if (foot <= 0 || foot + 8 >= 576) continue;
                    if (Enumerable.Range(foot, 8).Any(y => shadow.GetPixel(x, y).Alpha >= 8)) { contacts++; if (shadow.GetPixel(x, foot).Alpha == 0) gaps++; }
                }
                var dx = mx / Math.Max(1, mass) - center;
                Assert.Equal(0, gaps); Assert.True(contacts > 0);
                if (name == "indoor-sun") Assert.True(dx > 0);
                if (name == "outdoor-sun") Assert.True(dx < 0);
                if (name is "indoor-diffuse" or "outdoor-overcast") Assert.True(farStrong / (double)Math.Max(1, strong) < .5);
            }
            metrics.Add(new { name, task, seconds = watch.Elapsed.TotalSeconds, executions = connection.Executions, contacts, gaps, strong,
                farStrongFraction = farStrong / (double)Math.Max(1, strong), dx = mx / Math.Max(1, mass) - (scene == null ? 0 : scene.SupportBounds.Left + scene.SupportBounds.Width / 2d), dy = my / Math.Max(1, mass) - (scene?.SupportBounds.Bottom ?? 0) });
            File.WriteAllText(Path.Combine(outputRoot, "metrics.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
            session.Undo(); Assert.Equal(state, session.History.CurrentId);
            void Save(SKBitmap image, string label) => ImageFiles.Save(image, Path.Combine(folder, label + ".png"), ExportFormat.Png);
        }
    }

    private sealed class RecordingConnection(string url, string folder) : IComfyConnection
    {
        private readonly ComfyClient client = new(url);
        internal int Executions;
        public ComfyServerAddress Address => client.Address;
        public TimeSpan ConnectionTimeout { get => client.ConnectionTimeout; set => client.ConnectionTimeout = value; }
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken token = default) => client.TestConnectionAsync(token);
        public Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken token = default)
        { ImageFiles.Save(image, Path.Combine(folder, $"{Executions}-{semantic}.png"), ExportFormat.Png); return client.UploadPngAsync(semantic, image, token); }
        public Task<ComfyExecutionResult> ExecuteAsync(JsonObject graph, IProgress<AiOperationState>? progress = null, CancellationToken token = default)
        { File.WriteAllText(Path.Combine(folder, $"{++Executions}-workflow.json"), graph.ToJsonString(new() { WriteIndented = true })); return client.ExecuteAsync(graph, progress, token); }
        public async Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken token = default)
        { var bitmap = await client.DownloadAsync(image, token); ImageFiles.Save(bitmap, Path.Combine(folder, $"{Executions}-{image.NodeId}.png"), ExportFormat.Png); return bitmap; }
        public void Dispose() => client.Dispose();
    }
}
