using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class AiVariantsTests
{
    [Theory]
    [InlineData(AiVariantMode.List)]
    [InlineData(AiVariantMode.Batch)]
    public async Task Two_local_variants_also_work_in_list_and_native_batch(AiVariantMode mode)
    {
        var session = EditorSession.NewCanvas(48, 32); var fake = new Connection();
        await Service(fake).RunAsync(new EditorCommandService(session), new AiTaskRequest { Task = AiTaskKind.GenerateImage,
            Settings = new() { Variants = 2, VariantMode = mode } }, TestContext.Current.CancellationToken);
        Assert.Equal(mode == AiVariantMode.List ? 2 : 1, fake.Graphs.Count);
        Assert.Equal(2, session.AiVariantGroup!.Children.Count); Assert.Single(session.AiVariantGroup.Children, layer => layer.Visible);
        session.Undo(); Assert.Single(session.Document.Layers);
    }
    [Theory]
    [InlineData(AiVariantMode.List, 3)]
    [InlineData(AiVariantMode.Batch, 1)]
    public async Task Three_variants_reuse_inputs_use_one_undo_and_show_only_one_result(AiVariantMode mode, int runs)
    {
        var session = EditorSession.NewCanvas(48, 32, SKColors.White);
        session.SelectRect(new SKRect(12, 8, 32, 24)); var history = session.History.Count;
        var fake = new Connection(); var service = Service(fake);
        using var reference = Pixels.NewColor(8, 8);
        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest
        {
            Task = AiTaskKind.Harmonize, ReferenceImages = [reference],
            Settings = new AiGenerationSettings { Width = 48, Height = 32, Seed = 17, Variants = 3, VariantMode = mode }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(runs, fake.Graphs.Count);
        Assert.Equal(fake.Uploads.Distinct(), fake.Uploads); // source and reference uploaded once, not once per list item
        Assert.Contains("sourceImage", fake.Uploads); Assert.Contains("referenceImage1", fake.Uploads);
        Assert.Equal(3, session.AiVariantGroup!.Children.Count);
        Assert.Single(session.AiVariantGroup.Children, layer => layer.Visible);
        Assert.Equal(history + 1, session.History.Count);
        if (mode == AiVariantMode.List) Assert.Equal(new long[] { 17, 18, 19 }, fake.Graphs.Select(graph => graph["sampler"]!["inputs"]!["seed"]!.GetValue<long>()));
        else Assert.Equal(3, fake.Graphs[0]["latent"]!["inputs"]!["amount"]!.GetValue<int>());
        session.SelectAiVariant(session.AiVariantGroup, 2);
        Assert.True(session.AiVariantGroup!.Children[2].Visible); session.Undo(); Assert.True(session.AiVariantGroup!.Children[0].Visible);
        session.Undo(); Assert.Single(session.Document.Layers);
        Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
    }

    [Fact]
    public async Task Failed_second_list_item_and_cancellation_apply_no_partial_results()
    {
        foreach (var cancel in new[] { false, true })
        {
            var session = EditorSession.NewCanvas(48, 32, SKColors.White);
            var fake = new Connection { FailAt = 2, Cancel = cancel }; var service = Service(fake);
            var request = new AiTaskRequest { Task = AiTaskKind.GenerateImage, Settings = new AiGenerationSettings { Variants = 3 } };
            if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunAsync(new EditorCommandService(session), request, TestContext.Current.CancellationToken));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new EditorCommandService(session), request, TestContext.Current.CancellationToken));
            Assert.Single(session.Document.Layers); Assert.Equal(0, session.History.Count);
            Assert.Equal(cancel ? AiOperationStatus.Cancelled : AiOperationStatus.Failed, service.Operation!.Status);
        }
    }

    [Fact]
    public async Task Editing_during_generation_does_not_apply_a_result_to_another_history_state()
    {
        var session = EditorSession.NewCanvas(48, 32); var fake = new Connection { DuringExecute = () => session.AddBlankLayer() };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fake).RunAsync(new EditorCommandService(session),
            new AiTaskRequest { Task = AiTaskKind.GenerateImage }, TestContext.Current.CancellationToken));
        Assert.Contains("document changed", error.Message); Assert.Equal(2, session.Document.Layers.Count); Assert.DoesNotContain(session.Document.Layers, layer => layer.Tags.Contains("ai-generated"));
    }

    [Fact]
    public async Task Switching_active_layer_during_generation_does_not_insert_above_a_different_target()
    {
        var session = EditorSession.NewCanvas(48, 32); var first = session.ActiveLayer!.Id; session.AddBlankLayer();
        var history = session.History.CurrentId;
        var fake = new Connection { DuringExecute = () => session.SelectLayer(first) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fake).RunAsync(new EditorCommandService(session),
            new AiTaskRequest { Task = AiTaskKind.GenerateImage }, TestContext.Current.CancellationToken));
        Assert.Equal(history, session.History.CurrentId); Assert.Equal(2, session.Document.Layers.Count);
    }

    [Theory]
    [InlineData(AiTaskKind.GenerativeFill)]
    [InlineData(AiTaskKind.RemoveObject)]
    public void Native_batch_splits_images_before_stitch_and_reuses_original_crop_metadata(AiTaskKind task)
    {
        var service = Service(new Connection()); var engine = service.SelectedEngine!;
        var binding = engine.Binding(task)!; var graph = service.Engines.ReadWorkflow(engine, engine.Workflow(binding.Workflow));
        var original = graph["stitch"]!["inputs"]!["stitcher"]!.DeepClone().ToJsonString();
        WorkflowExecution.Batch(graph, 3);
        Assert.False(graph.ContainsKey("stitch")); Assert.Equal(3, graph["latent"]!["inputs"]!["batch_size"]!.GetValue<int>());
        var stitches = graph.Where(pair => pair.Value?["class_type"]?.GetValue<string>() == "InpaintStitchImproved").ToArray();
        Assert.Equal(3, stitches.Length);
        foreach (var (_, node) in stitches) Assert.Equal(original, node!["inputs"]!["stitcher"]!.ToJsonString());
        Assert.Equal("composa_join_1", graph["save"]!["inputs"]!["images"]![0]!.GetValue<string>());
        Assert.Equal(3, graph.Count(pair => pair.Value?["class_type"]?.GetValue<string>() == "ImageFromBatch"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Upscale_requested_factor_preserves_odd_size_and_one_undo(int factor)
    {
        var session = EditorSession.NewCanvas(57, 43, SKColors.White); var fake = new Connection();
        await Service(fake).RunAsync(new EditorCommandService(session), new AiTaskRequest
        { Task = AiTaskKind.Upscale, Settings = new AiGenerationSettings { UpscaleFactor = factor } }, TestContext.Current.CancellationToken);
        Assert.Equal((57 * factor, 43 * factor), (session.Document.Width, session.Document.Height));
        var graph = Assert.Single(fake.Graphs);
        Assert.Equal("source", graph["upscale"]!["inputs"]!["image"]![0]!.GetValue<string>()); // no pre-downscale before model
        session.Undo(); Assert.Equal((57, 43), (session.Document.Width, session.Document.Height));
    }

    [Fact]
    public void Unsupported_batch_and_oversized_upscale_fail_before_execution()
    {
        var graph = JsonNode.Parse("""{"source":{"class_type":"VAEEncode","inputs":{}}}""")!.AsObject();
        Assert.Contains("List", Assert.Throws<InvalidOperationException>(() => WorkflowExecution.Batch(graph, 3)).Message);
        Assert.Throws<ArgumentException>(() => WorkflowExecution.Upscale(graph, 3, 100, 100));
        Assert.Throws<InvalidOperationException>(() => WorkflowExecution.Upscale(graph, 4, DocumentLimits.MaxSide, 100));
    }

    private static AiTaskService Service(Connection fake) => new(() => "http://localhost:8188", Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => fake);

    private sealed class Connection : IComfyConnection
    {
        public ComfyServerAddress Address { get; } = ComfyServerAddress.Parse("http://localhost:8188");
        public TimeSpan ConnectionTimeout { get; set; }
        public List<JsonObject> Graphs { get; } = [];
        public List<string> Uploads { get; } = [];
        public int FailAt { get; init; }
        public bool Cancel { get; init; }
        public Action? DuringExecute { get; init; }
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
            var nodes = catalog.Profiles.Single(profile => profile.Id == "flux2-klein-intel-xpu").RequiredNodeTypes.Concat(new[] { "EmptyImage", "ImageCompositeMasked", "ImageFromBatch", "ImageBatch", "InpaintModelConditioning", "RepeatLatentBatch", "GrowMask", "ThresholdMask", "ImageCrop", "ImageScale", "CropMask", "MaskToImage", "ImageBlur" }).ToHashSet();
            return Task.FromResult((new ComfyServerInfo("1.0", "test", "test", []), new ComfyServerCapabilities { Version = "1.0", NodeTypes = nodes }));
        }
        public Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken cancellationToken = default)
        { Uploads.Add(semantic); return Task.FromResult(semantic + ".png"); }
        public Task<ComfyExecutionResult> ExecuteAsync(JsonObject workflow, IProgress<AiOperationState>? progress = null, CancellationToken cancellationToken = default)
        {
            Graphs.Add((JsonObject)workflow.DeepClone()); DuringExecute?.Invoke();
            if (Graphs.Count == FailAt)
            {
                if (Cancel) throw new OperationCanceledException();
                throw new InvalidOperationException("Mock failure");
            }
            var count = workflow["latent"]?["inputs"]?["batch_size"]?.GetValue<int>() ?? workflow["latent"]?["inputs"]?["amount"]?.GetValue<int>() ?? 1;
            return Task.FromResult(new ComfyExecutionResult("job", JsonDocument.Parse("{}"),
                Enumerable.Range(0, count).Select(index => new ComfyImageReference(index + ".png", "", "output", "save")).ToArray()));
        }
        public Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken cancellationToken = default)
        {
            var resize = Graphs[^1]["composa_upscale_size"]?["inputs"];
            var bitmap = Pixels.NewColor(resize?["width"]?.GetValue<int>() ?? 48, resize?["height"]?.GetValue<int>() ?? 32);
            bitmap.Erase(SKColors.CornflowerBlue); return Task.FromResult(bitmap);
        }
        public void Dispose() { }
    }
}
