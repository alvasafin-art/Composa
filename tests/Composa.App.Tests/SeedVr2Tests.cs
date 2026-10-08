using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class SeedVr2Tests
{
    [Fact]
    public void Official_defaults_preserve_previously_selected_native_model_files_per_server()
    {
        var preferences=new Settings();
        var oldDit=new WorkflowModelSlot("seedvr2-native","model","UNETLoader","unet_name","seedvr2_3b_fp16.safetensors",EngineAssetKind.DiffusionModel);
        var oldVae=new WorkflowModelSlot("seedvr2-native","vae","VAELoader","vae_name","ema_vae_fp16.safetensors",EngineAssetKind.Vae);
        preferences.SetComfyModels("http://localhost:8188",new Dictionary<string,string> { [oldDit.Key]="folder/seedvr2_3b_fp16.safetensors",[oldVae.Key]="folder/ema_vae_fp16.safetensors" });
        var restored=Settings.FromJson(JsonSerializer.Serialize(preferences));
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai","engines"));
        var slots=catalog.ModelSlots(catalog.Find("seedvr2-native")!);
        foreach(var slot in slots)
            Assert.Equal(slot.Kind==EngineAssetKind.Vae ? "folder/ema_vae_fp16.safetensors" : "folder/seedvr2_3b_fp16.safetensors",restored.ComfyModelsFor("http://localhost:8188")[slot.Key]);
    }

    [Theory]
    [InlineData("seedvr2-native")]
    [InlineData("seedvr2")]
    public async Task Official_workflow_uploads_the_whole_image_executes_once_and_commits_once(string engine)
    {
        var s = EditorSession.NewCanvas(185, 143, SKColors.Coral);
        s.AddShape(new(Composa.Model.ShapeKind.Rectangle, (uint)SKColors.Blue, 0), new SKRect(31, 28, 104, 116));
        using var original = s.Flatten(); var state = s.History.CurrentId;
        using var connection = new Connection();
        await Service(connection).RunAsync(new EditorCommandService(s), Request(engine), TestContext.Current.CancellationToken);
        Assert.Equal((740, 572), (s.Document.Width, s.Document.Height)); Assert.Equal(1, connection.Executions);
        Assert.Equal(new SKSizeI(185, 143), Assert.Single(connection.Uploads));
        using var actual = s.Flatten();
        for (var y = 0; y < actual.Height; y += 7) for (var x = 0; x < actual.Width; x += 7)
            Assert.Equal(original.GetPixel(x / 4, y / 4), actual.GetPixel(x, y));
        var completed = s.History.CurrentId; Assert.NotEqual(state, completed);
        s.Undo(); Assert.Equal(state, s.History.CurrentId);
        using var restored = s.Flatten(); Assert.Equal(original.GetPixelSpan().ToArray(), restored.GetPixelSpan().ToArray());
        s.Redo(); Assert.Equal(completed, s.History.CurrentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_leaves_document_and_history_untouched(bool cancel)
    {
        var s = EditorSession.NewCanvas(185, 143, SKColors.Coral); var state = s.History.CurrentId;
        using var connection = new Connection { Cancel = cancel, Fail = !cancel };
        var task = Service(connection).RunAsync(new EditorCommandService(s), Request("seedvr2-native"), TestContext.Current.CancellationToken);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal(1, connection.Executions);
        Assert.Equal(state, s.History.CurrentId); Assert.Equal((185, 143), (s.Document.Width, s.Document.Height)); Assert.Single(s.Document.Layers);
    }

    private static AiTaskRequest Request(string engine) => new() { Task = AiTaskKind.Upscale, EngineId = engine, Settings = new() { UpscaleFactor = 4, Seed = 17 } };
    private static AiTaskService Service(Connection connection) => new(() => "http://localhost:8188", Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => connection);

    internal sealed class Connection : IComfyConnection
    {
        public ComfyServerAddress Address => ComfyServerAddress.Parse("http://localhost:8188");
        public TimeSpan ConnectionTimeout { get; set; }
        public bool Fail, Cancel;
        public int Executions;
        public List<SKSizeI> Uploads = [];
        private SKBitmap? source;
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
            return Task.FromResult((new ComfyServerInfo("0.37.0", "Windows", "3.12", ["GPU"]) { Memory = [new("GPU", "cuda", 0, 8L << 30, 6L << 30)] },
                new ComfyServerCapabilities { NodeTypes = catalog.Find("seedvr2-native")!.RequiredNodeTypes.ToHashSet() }));
        }
        public Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken cancellationToken = default)
        {
            Assert.Equal("sourceImage", semantic); source?.Dispose(); source = Pixels.Clone(image);
            Uploads.Add(new(image.Width, image.Height)); return Task.FromResult("source.png");
        }
        public Task<ComfyExecutionResult> ExecuteAsync(JsonObject graph, IProgress<AiOperationState>? progress = null, CancellationToken cancellationToken = default)
        {
            Executions++;
            var sampler = graph["sampler"]!["inputs"]!;
            Assert.Equal(1, sampler["steps"]!.GetValue<int>()); Assert.Equal(1, sampler["cfg"]!.GetValue<int>());
            Assert.Equal("euler", sampler["sampler_name"]!.GetValue<string>()); Assert.Equal("simple", sampler["scheduler"]!.GetValue<string>());
            Assert.Equal("ResizeImageMaskNode", graph["upscale"]!["class_type"]!.GetValue<string>());
            Assert.Equal(4, graph["upscale"]!["inputs"]!["resize_type.multiplier"]!.GetValue<int>());
            foreach (var id in new[] { "encode", "decode" })
            {
                var inputs = graph[id]!["inputs"]!;
                Assert.Equal(512, inputs["tile_size"]!.GetValue<int>()); Assert.Equal(128, inputs["overlap"]!.GetValue<int>());
                Assert.Equal(4096, inputs["temporal_size"]!.GetValue<int>());
            }
            Assert.Equal("none", graph["postprocess"]!["inputs"]!["color_correction_method"]!.GetValue<string>());
            Assert.Equal("source", graph["rgba"]!["inputs"]!["alpha"]![0]!.GetValue<string>());
            Assert.Equal(1, graph["rgba"]!["inputs"]!["alpha"]![1]!.GetValue<int>());
            Assert.DoesNotContain(graph, n => n.Value?["class_type"]?.GetValue<string>() == "InvertMask");
            if (Cancel) throw new OperationCanceledException();
            if (Fail) throw new InvalidOperationException("CUDA out of memory");
            return Task.FromResult(new ComfyExecutionResult("test", JsonDocument.Parse("{}"), [new("upscaled.png", "", "output", "save")]));
        }
        public Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken cancellationToken = default)
        {
            var output = Pixels.NewColor(source!.Width * 4, source.Height * 4);
            using var canvas = new SKCanvas(output);
            canvas.DrawImage(Pixels.ImageOf(source), new SKRect(0, 0, output.Width, output.Height), new SKSamplingOptions(SKFilterMode.Nearest));
            return Task.FromResult(output);
        }
        public void Dispose() { source?.Dispose(); source = null; }
    }
}
