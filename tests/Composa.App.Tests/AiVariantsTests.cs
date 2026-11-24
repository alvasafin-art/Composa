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
    [Fact]
    public async Task Factory_Klein_generation_preserves_steps_but_normalizes_sampler_on_the_wire()
    {
        var catalog = Service(new Connection()).Engines;
        var klein = catalog.Find("flux2-klein-intel-xpu")!;
        var temporary = Directory.CreateTempSubdirectory("composa-workflow-");
        try
        {
            var engineFolder = Path.Combine(temporary.FullName, klein.Id);
            foreach (var file in Directory.EnumerateFiles(catalog.DirectoryOf(klein), "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(engineFolder, Path.GetRelativePath(catalog.DirectoryOf(klein), file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            var workflow = klein.Workflow("generate");
            var filePath = Path.Combine(engineFolder, workflow.File);
            var graph = catalog.ReadWorkflow(klein, workflow);
            graph["sampler"]!["inputs"]!["steps"] = 9;
            graph["sampler"]!["inputs"]!["sampler_name"] = "heun";
            File.WriteAllText(filePath, graph.ToJsonString());

            using var transport = new PromptCapture();
            var connection = new Connection { Transport = transport };
            var service = new AiTaskService(() => "http://localhost:8188", temporary.FullName, _ => connection);
            var session = EditorSession.NewCanvas(48, 32);
            await service.RunAsync(new EditorCommandService(session), new() { Task = AiTaskKind.GenerateImage },
                TestContext.Current.CancellationToken);

            var sent = Assert.Single(transport.Prompts);
            Assert.Equal(9, sent["sampler"]!["inputs"]!["steps"]!.GetValue<int>());
            Assert.Equal("euler", sent["sampler"]!["inputs"]!["sampler_name"]!.GetValue<string>());
            Assert.False(sent.ContainsKey("sampling"));
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal sealed class PromptCapture : HttpMessageHandler
    {
        public List<JsonObject> Prompts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/prompt", request.RequestUri!.AbsolutePath);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Prompts.Add(body["prompt"]!.AsObject());
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"prompt_id":"job"}""") };
        }
    }

    [Fact]
    public async Task Assigned_fill_checks_required_mask_and_posts_only_bound_inputs()
    {
        using var transport = new PromptCapture();
        var connection = new Connection { Transport = transport };
        var service = Service(connection);
        var prefs = new WorkflowPreferences();
        const AiTaskKind task = AiTaskKind.GenerativeFill;
        service.WorkflowSettings = () => prefs;
        prefs.Assignments[task] = "fill";
        var saved = EditableWorkflows.Factory(service.Engines, service.SelectedEngine!, task);
        var mask = saved.Bindings["selectionMask"];
        saved.Bindings.Remove("selectionMask");
        prefs.Workflows["fill"] = saved;
        var session = EditorSession.NewCanvas(48, 32, SKColors.White);
        session.SelectRect(new(10, 10, 25, 25));
        var editor = new EditorCommandService(session);
        var request = new AiTaskRequest { Task = task, Prompt = "A red bowl" };
        var token = TestContext.Current.CancellationToken;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.RunAsync(editor, request, token));
        Assert.Contains("Bind Selection mask", error.Message);
        Assert.Empty(connection.Uploads);
        Assert.Empty(transport.Prompts);

        saved.Bindings["selectionMask"] = mask;
        await service.RunAsync(editor, request, token);
        Assert.Equal(new[] { "sourceImage", "selectionMask" }, connection.Uploads);
        var expected = (JsonObject)saved.Graph.DeepClone();
        var actual = Assert.Single(transport.Prompts);
        var prompt = saved.Bindings["prompt"];
        var source = saved.Bindings["sourceImage"];
        expected[prompt.NodeId]!["inputs"]![prompt.Input] = actual[prompt.NodeId]!["inputs"]![prompt.Input]!.GetValue<string>();
        expected[source.NodeId]!["inputs"]![source.Input] = "sourceImage.png";
        expected[mask.NodeId]!["inputs"]![mask.Input] = "selectionMask.png";
        Assert.True(JsonNode.DeepEquals(expected, actual));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(640)]
    public async Task Selected_expansion_uses_its_own_original_or_explicit_size_mode(int side)
    {
        var session=EditorSession.NewCanvas(320,240,SKColors.White); session.SelectRect(new SKRect(120,80,170,130));
        var fake=new Connection();
        await Service(fake).RunAsync(new EditorCommandService(session),new() { Task=AiTaskKind.GenerativeExpand,ExpansionMinimumSide=side,
            Settings=new() { Width=640,Height=640,Values=new() { ["imageOriginalSize"]=side!=0 } } },TestContext.Current.CancellationToken);
        var size=fake.Graphs[0]["composa_edit_empty_mask"]!["inputs"]!;
        var pixels=(long)size["width"]!.GetValue<int>()*size["height"]!.GetValue<int>();
        if(side==0) Assert.True(pixels >= DocumentLimits.MinimumGenerationPixels);
        else Assert.True(pixels < DocumentLimits.MinimumGenerationPixels);
        Assert.Equal((320,240),(session.Document.Width,session.Document.Height));
    }

    [Theory]
    [InlineData(true,320,240)]
    [InlineData(true,2048,1536)]
    [InlineData(false,320,240)]
    public async Task Generation_original_size_reaches_one_MP_without_affecting_explicit_dimensions(bool original,int width,int height)
    {
        var session=EditorSession.NewCanvas(width,height); var fake=new Connection { UseGenerationDimensions=true };
        await Service(fake).RunAsync(new EditorCommandService(session),new() { Task=AiTaskKind.GenerateImage,
            Settings=new() { Width=width,Height=height,Values=new() { ["imageOriginalSize"]=original } } },TestContext.Current.CancellationToken);
        var dimensions=fake.Graphs[0]["latent"]!["inputs"]!;
        var w=dimensions["width"]!.GetValue<int>(); var h=dimensions["height"]!.GetValue<int>();
        if(original) Assert.True((long)w*h >= DocumentLimits.MinimumGenerationPixels);
        if(!original || (long)width*height >= DocumentLimits.MinimumGenerationPixels) Assert.Equal((width,height),(w,h));
        Assert.Equal((w,h),(session.ActiveLayer!.Pixels!.Width,session.ActiveLayer.Pixels.Height));
    }

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
            Task = AiTaskKind.ImageEdit, ReferenceImages = [reference],
            Settings = new AiGenerationSettings { Width = 48, Height = 32, Seed = 17, Variants = 3, VariantMode = mode }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(runs, fake.Graphs.Count);
        Assert.Equal(fake.Uploads.Distinct(), fake.Uploads); // source and reference uploaded once, not once per list item
        Assert.Contains("sourceImage", fake.Uploads); Assert.Contains("referenceImage1", fake.Uploads);
        Assert.Equal(3, session.AiVariantGroup!.Children.Count);
        Assert.Single(session.AiVariantGroup.Children, layer => layer.Visible);
        Assert.Equal(history + 1, session.History.Count);
        if (mode == AiVariantMode.List) Assert.Equal(new long[] { 17, 18, 19 }, fake.Graphs.Select(graph => graph["sampler"]!["inputs"]!["seed"]!.GetValue<long>()));
        else Assert.Equal(3, fake.Graphs[0]["latent"]!["inputs"]!["batch_size"]!.GetValue<int>());
        using var preview = Pixels.NewColor(48, 32);
        session.RenderView(preview, session.Document.Bounds, new RenderView(1, SKPoint.Empty));
        var generated = preview.GetPixel(20, 15);
        var revision = session.History.CurrentId;
        session.CompareWithoutVariants(session.AiVariantGroup.Id);
        session.RenderView(preview, session.Document.Bounds, new RenderView(1, SKPoint.Empty));
        Assert.Equal(SKColors.White, preview.GetPixel(20, 15));
        using var export = session.Flatten();
        Assert.Equal(generated, export.GetPixel(20, 15));
        session.CompareWithoutVariants(null);
        session.RenderView(preview, session.Document.Bounds, new RenderView(1, SKPoint.Empty));
        Assert.Equal(generated, preview.GetPixel(20, 15));
        Assert.Equal(revision, session.History.CurrentId);
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
        const string temporary = "alternate/upscale.pth";
        var session = EditorSession.NewCanvas(57, 43, SKColors.White);
        var fake = new Connection();
        var service = Service(fake);
        var slot = Assert.Single(UpscaleModels.Slots(service.Engines, service.SelectedEngine!));
        fake.ModelChoices[slot.LoaderKey] = [slot.Default, temporary];
        var saved = new Dictionary<string, string> { [slot.Key] = slot.Default };
        service.ModelSelections = _ => saved;
        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest
        {
            Task = AiTaskKind.Upscale, ModelFileOverrides = new Dictionary<string, string> { [slot.Key] = temporary },
            Settings = new AiGenerationSettings { UpscaleFactor = factor }
        }, TestContext.Current.CancellationToken);
        Assert.Equal((57 * factor, 43 * factor), (session.Document.Width, session.Document.Height));
        var graph = Assert.Single(fake.Graphs);
        Assert.Equal(temporary, graph[slot.NodeId]!["inputs"]![slot.Input]!.GetValue<string>());
        Assert.Equal(slot.Default, saved[slot.Key]);
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

    internal sealed class Connection : IComfyConnection
    {
        public ComfyServerAddress Address { get; } = ComfyServerAddress.Parse("http://localhost:8188");
        public TimeSpan ConnectionTimeout { get; set; }
        public List<JsonObject> Graphs { get; } = [];
        public List<string> Uploads { get; } = [];
        public int FailAt { get; init; }
        public bool Cancel { get; init; }
        public bool UseGenerationDimensions { get; init; }
        public Action? DuringExecute { get; init; }
        public PromptCapture? Transport { get; init; }
        public Dictionary<string, HashSet<string>> ModelChoices { get; init; } = [];
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
            var nodes = catalog.Profiles.Single(profile => profile.Id == "flux2-klein-intel-xpu").RequiredNodeTypes.Concat(new[] { "EmptyImage", "ImageCompositeMasked", "ImageFromBatch", "ImageBatch", "InpaintModelConditioning", "SetLatentNoiseMask", "ConditioningZeroOut", "RepeatLatentBatch", "GrowMask", "ThresholdMask", "ImageCrop", "ImageScale", "CropMask", "MaskToImage", "LoadImageMask", "ImageBlur", "ImagePadForOutpaint", "SolidMask", "MaskComposite" }).ToHashSet();
            return Task.FromResult((new ComfyServerInfo("1.0", "test", "test", []), new ComfyServerCapabilities
                { Version = "1.0", NodeTypes = nodes, ModelChoices = ModelChoices }));
        }
        public Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken cancellationToken = default)
        { Uploads.Add(semantic); return Task.FromResult(semantic + ".png"); }
        public async Task<ComfyExecutionResult> ExecuteAsync(JsonObject workflow, IProgress<AiOperationState>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Transport != null)
            {
                using var http = new HttpClient(Transport, disposeHandler: false);
                using var client = new ComfyClient(Address.ToString(), http);
                await client.SubmitAsync(workflow, Guid.NewGuid(), cancellationToken);
            }
            Graphs.Add((JsonObject)workflow.DeepClone()); DuringExecute?.Invoke();
            if (Graphs.Count == FailAt)
            {
                if (Cancel) throw new OperationCanceledException();
                throw new InvalidOperationException("Mock failure");
            }
            var count = workflow["latent"]?["inputs"]?["batch_size"]?.GetValue<int>() ?? workflow["latent"]?["inputs"]?["amount"]?.GetValue<int>() ?? 1;
            return new ComfyExecutionResult("job", JsonDocument.Parse("{}"),
                Enumerable.Range(0, count).Select(index => new ComfyImageReference(index + ".png", "", "output", "save")).ToArray());
        }
        public Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken cancellationToken = default)
        {
            var resize = Graphs[^1]["composa_upscale_size"]?["inputs"];
            var masked = Graphs[^1]["composa_edit_empty_mask"]?["inputs"];
            var generated = UseGenerationDimensions ? Graphs[^1]["latent"]?["inputs"] : null;
            var bitmap = Pixels.NewColor(resize?["width"]?.GetValue<int>() ?? masked?["width"]?.GetValue<int>() ?? generated?["width"]?.GetValue<int>() ?? 48,
                resize?["height"]?.GetValue<int>() ?? masked?["height"]?.GetValue<int>() ?? generated?["height"]?.GetValue<int>() ?? 32);
            bitmap.Erase(SKColors.CornflowerBlue); return Task.FromResult(bitmap);
        }
        public void Dispose() { }
    }
}
