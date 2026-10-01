using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

public class PartnerImageTests
{
    [Fact]
    public async Task Live_partner_schema_can_be_checked_without_uploads_or_a_paid_request_when_requested()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_PARTNER_SCHEMA_TEST_URL");
        if (string.IsNullOrWhiteSpace(url)) return;
        using var client = new ComfyClient(url);
        var (_, capabilities) = await client.TestConnectionAsync(TestContext.Current.CancellationToken);
        Assert.True(PartnerPricing.SupportsModel(capabilities, Pack().ApiModel!));
        Assert.Contains("low", PartnerPricing.Choices(capabilities, Pack().ApiModel!, "quality"));
        Assert.NotNull(PartnerPricing.Estimate(capabilities, Pack().ApiModel!, "low", "auto", 8, 3));
        Assert.Empty(Catalog().ModelSlots(Pack()));
    }
    private static EngineCatalog Catalog() => new(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
    private static EngineProfile Pack() => Catalog().Find("chatgpt-image-2.5")!;
    internal static ComfyServerCapabilities Capabilities()
    {
        var definitions = JsonNode.Parse("""
        {"input":{"required":{"model":["COMFY_DYNAMICCOMBO_V3",{"options":[{"key":"gpt-image-2.5-sunburst","inputs":{"required":{"quality":["COMBO",{"options":["low","medium","high","xhigh","max"]}],"size":["COMBO",{"options":["auto","1024x1024","1024x1536"]}]}}}]}]}}}
        """)!.AsObject();
        definitions["price_badge"] = new JsonObject { ["expr"] = """
            ($ranges := {"gpt-image-2.5-sunburst":{"low":[0.0023,0.0283]}};
             $perImage := {"gpt-image-2.5-sunburst":[0.0117,0.0176]};
             $presets := {"gpt-image-2.5":{"low":{"1024x1024":0.0084}}};)
            """ };
        return new() { Version = "1.0", NodeTypes = Pack().RequiredNodeTypes.ToHashSet(), NodeDefinitions = new() { ["OpenAIGPTImageNodeV2"] = definitions } };
    }
    private static AiTaskService Service(Connection connection, bool key = true) => new(() => "http://localhost:8188", Catalog().Root, _ => connection)
        { SelectedEngine = Pack(), ApiKey = () => key ? "test-only-comfy-key" : null };

    [Fact]
    public void Api_pack_has_one_official_node_no_local_weights_and_local_pack_stays_default()
    {
        var catalog = Catalog(); Assert.Equal(2, catalog.Profiles.Count); Assert.False(catalog.Profiles[0].PaidApi);
        var engine = Pack(); Assert.True(engine.PaidApi); Assert.Equal("gpt-image-2.5-sunburst", engine.ApiModel);
        Assert.Empty(engine.RequiredAssets); Assert.Empty(catalog.ModelSlots(engine)); Assert.False(engine.Lora.Supported);
        Assert.True(EngineCompatibility.Check(engine, Capabilities()).IsCompatible);
        var graph = catalog.ReadWorkflow(engine, engine.Workflows[0]);
        Assert.Single(graph.Where(pair => pair.Value?["class_type"]?.GetValue<string>() == "OpenAIGPTImageNodeV2"));
        Assert.DoesNotContain(graph, pair => pair.Value?["class_type"]?.GetValue<string>() is "UNETLoader" or "CLIPLoader" or "VAELoader");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void Generation_connects_references_in_order_without_a_fake_canvas_source(int count)
    {
        var session = EditorSession.NewCanvas(79, 61);
        using var reference = Pixels.NewColor(23, 19);
        var request = new AiTaskRequest { Task = AiTaskKind.GenerateImage, ReferenceImages = Enumerable.Repeat(reference, count).ToArray(), ReferenceMegapixels = null };
        using var inputs = AiTaskInputPreparer.Prepare(session, request); using var api = new PartnerImageInputs(inputs, request);
        var files = api.Images.ToDictionary(pair => pair.Key, pair => pair.Key + ".png");
        var graph = api.Bind(Catalog().ReadWorkflow(Pack(), Pack().Workflows[0]), Pack(), files, long.MaxValue);
        var node = graph["gpt"]!["inputs"]!.AsObject();
        Assert.DoesNotContain("apiSource", files.Keys); Assert.DoesNotContain("model.mask", node.Select(pair => pair.Key));
        Assert.Equal(count, node.Count(pair => pair.Key.StartsWith("model.images.image_")));
        for (var i = 1; i <= count; i++) Assert.Equal($"referenceImage{i}.png", graph[$"composa_api_image_{i}"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.InRange(node["seed"]!.GetValue<long>(), 0, int.MaxValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void Masked_edits_respect_the_nodes_single_image_mask_limit_and_restore_odd_canvas(int references)
    {
        var session = EditorSession.NewCanvas(79, 61, SKColors.White); session.SelectRect(new SKRect(25, 20, 45, 40));
        using var reference = Pixels.NewColor(17, 13);
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeFill, ReferenceImages = Enumerable.Repeat(reference, references).ToArray(), ReferenceMegapixels = null,
            Settings = new() { Values = new() { ["maskGrow"] = 0, ["maskBlend"] = 4, ["maskBlur"] = 0, ["maskContext"] = 2 } } };
        using var inputs = AiTaskInputPreparer.Prepare(session, request); using var api = new PartnerImageInputs(inputs, request);
        var graph = api.Bind(Catalog().ReadWorkflow(Pack(), Pack().Workflows[0]), Pack(), api.Images.ToDictionary(pair => pair.Key, pair => pair.Key + ".png"), 42);
        var node = graph["gpt"]!["inputs"]!.AsObject();
        Assert.Equal(references == 0, node.ContainsKey("model.mask"));
        Assert.Equal(references > 0, api.MaskAsReference);
        Assert.Equal("apiSource.png", graph["composa_api_image_1"]!["inputs"]!["image"]!.GetValue<string>());
        if (references > 0)
        {
            Assert.Contains($"Image {references + 2} is ONLY a grayscale editing mask", node["prompt"]!.GetValue<string>());
            Assert.Equal(references + 2, node.Count(pair => pair.Key.StartsWith("model.images.image_")));
        }
        var generated = Pixels.NewColor(112, 96); generated.Erase(SKColors.CornflowerBlue);
        var result = api.Finish(generated); Assert.Equal((79, 61), (result.Width, result.Height));
        Assert.Equal(SKColors.White, result.GetPixel(0, 0)); Assert.Equal(SKColors.CornflowerBlue, result.GetPixel(35, 30));
        AiTaskService.Insert(new EditorCommandService(session), request.Task, AiOutputMode.NewLayerWithMask, [result], inputs.TargetBounds, inputs, outputIsComposited: true);
        using var flattened = session.Flatten(); Assert.Equal(SKColors.CornflowerBlue, flattened.GetPixel(35, 30));
        Assert.Equal(SKColors.White, flattened.GetPixel(0, 0)); session.Undo(); Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void Remove_sends_black_patch_with_local_context_and_does_not_modify_source()
    {
        var session = EditorSession.NewCanvas(500, 400, SKColors.White); session.SelectRect(new SKRect(240, 180, 260, 200));
        var request = new AiTaskRequest { Task = AiTaskKind.RemoveObject, Settings = new() { Values = new() { ["maskGrow"] = 0, ["maskBlend"] = 0, ["maskBlur"] = 0, ["maskContext"] = 2 } } };
        using var inputs = AiTaskInputPreparer.Prepare(session, request); using var api = new PartnerImageInputs(inputs, request);
        Assert.Equal((40, 40), (api.Images["apiSource"].Width, api.Images["apiSource"].Height));
        Assert.Equal(SKColors.Black, api.Images["apiSource"].GetPixel(20, 20));
        Assert.Equal(SKColors.White, inputs.ContextImage.GetPixel(250, 190));
        Assert.Equal(SKColors.White, session.ActiveLayer!.Pixels!.GetPixel(250, 190));
    }

    [Fact]
    public void Expansion_keeps_odd_geometry_and_background_replacement_keeps_original_subject_separate()
    {
        var session = EditorSession.NewCanvas(79, 61, SKColors.White);
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeExpand, ExpansionBounds = new(-11, -7, 91, 69),
            Settings = new() { Values = new() { ["maskGrow"] = 0, ["maskBlend"] = 0, ["maskBlur"] = 0 } } };
        using (var inputs = AiTaskInputPreparer.Prepare(session, request))
        using (var api = new PartnerImageInputs(inputs, request))
        {
            var generated = Pixels.NewColor(128, 96); generated.Erase(SKColors.CornflowerBlue);
            using var result = api.Finish(generated); Assert.Equal((102, 76), (result.Width, result.Height));
            Assert.Equal(SKColors.CornflowerBlue, result.GetPixel(0, 0)); Assert.Equal(SKColors.White, result.GetPixel(30, 30));
        }
        session.SelectRect(new SKRect(25, 20, 45, 40));
        request = request with { Task = AiTaskKind.ChangeBackground, ExpansionBounds = null };
        using (var inputs = AiTaskInputPreparer.Prepare(session, request))
        using (var api = new PartnerImageInputs(inputs, request))
        {
            var generated = Pixels.NewColor(112, 96); generated.Erase(SKColors.CornflowerBlue); var result = api.Finish(generated);
            AiTaskService.Insert(new EditorCommandService(session), request.Task, AiOutputMode.LayerGroup, [result], inputs.TargetBounds, inputs);
            var group = Assert.Single(session.Document.Layers.Where(layer => layer.IsGroup));
            Assert.Equal(new[] { "AI Background", "Original Subject" }, group.Children.Select(layer => layer.Name));
            using var rendered = session.Flatten(); Assert.Equal(SKColors.White, rendered.GetPixel(35, 30)); Assert.Equal(SKColors.CornflowerBlue, rendered.GetPixel(0, 0));
            session.Undo(); Assert.Single(session.Document.Layers);
        }
    }

    [Theory]
    [InlineData(AiVariantMode.List, 1)]
    [InlineData(AiVariantMode.List, 2)]
    [InlineData(AiVariantMode.List, 3)]
    [InlineData(AiVariantMode.Batch, 2)]
    [InlineData(AiVariantMode.Batch, 3)]
    public async Task Api_variants_upload_once_and_apply_in_one_undo(AiVariantMode mode, int count)
    {
        var session = EditorSession.NewCanvas(79, 61, SKColors.White); session.SelectRect(new SKRect(25, 20, 45, 40));
        var history = session.History.Count; var connection = new Connection(); var service = Service(connection);
        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest { Task = AiTaskKind.GenerativeFill,
            Settings = new() { Variants = count, VariantMode = mode } }, TestContext.Current.CancellationToken);
        Assert.Equal(mode == AiVariantMode.List ? count : 1, connection.Graphs.Count);
        Assert.Equal(2, connection.Uploads.Count); Assert.Equal(connection.Uploads.Distinct(), connection.Uploads);
        Assert.Equal(history + 1, session.History.Count); Assert.Equal(count * 2, service.Operation!.CreditsUsed);
        if (count > 1) { Assert.Equal(count, session.AiVariantGroup!.Children.Count); Assert.Single(session.AiVariantGroup.Children, layer => layer.Visible); }
        session.Undo(); Assert.Single(session.Document.Layers);
    }

    [Fact]
    public async Task Missing_key_unsupported_model_or_settings_fail_before_upload_or_billing()
    {
        foreach (var failure in new[] { "key", "model", "quality" })
        {
            var connection = new Connection { Server = failure == "model" ? Capabilities() with { NodeDefinitions = [] } : Capabilities() };
            var session = EditorSession.NewCanvas(79, 61); var service = Service(connection, key: failure != "key");
            var request = new AiTaskRequest { Task = AiTaskKind.GenerateImage, Settings = new() { Values = new() { ["apiQuality"] = failure == "quality" ? "invalid" : "low" } } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new EditorCommandService(session), request, TestContext.Current.CancellationToken));
            Assert.Empty(connection.Graphs); Assert.Empty(connection.Uploads); Assert.Equal(0, session.History.Count);
        }
    }

    [Fact]
    public async Task Failed_second_api_request_applies_no_partial_document_edits()
    {
        var connection = new Connection { FailAt = 2 }; var session = EditorSession.NewCanvas(79, 61);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(connection).RunAsync(new EditorCommandService(session),
            new AiTaskRequest { Task = AiTaskKind.GenerateImage, Settings = new() { Variants = 2 } }, TestContext.Current.CancellationToken));
        Assert.Equal(0, session.History.Count); Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void Price_estimate_uses_server_tables_includes_references_and_all_variants_and_can_be_unavailable()
    {
        var estimate = PartnerPricing.Estimate(Capabilities(), Pack().ApiModel!, "low", "1024x1024", 3, 2)!;
        Assert.Equal((0.0084 + 3 * 0.0117) * 2, estimate.MinimumUsd, 8);
        Assert.Equal((0.0084 + 3 * 0.0176) * 2, estimate.MaximumUsd, 8);
        Assert.Null(PartnerPricing.Estimate(null, Pack().ApiModel!, "low", "auto", 0, 1));
        Assert.Null(PartnerPricing.Estimate(Capabilities(), Pack().ApiModel!, "missing", "auto", 0, 1));
    }

    [Fact]
    public void Binary_node_progress_reads_reported_credits_and_ignores_previews_other_nodes_and_bad_frames()
    {
        var text = Encoding.UTF8.GetBytes("Status: Completed\nPrice: 1,234.5 credits"); var node = Encoding.UTF8.GetBytes("gpt");
        var frame = new byte[8 + node.Length + text.Length]; BinaryPrimitives.WriteUInt32BigEndian(frame, 3);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)node.Length); node.CopyTo(frame, 8); text.CopyTo(frame, 8 + node.Length);
        var state = new AiOperationState { Status = AiOperationStatus.Running, NodeId = "gpt" };
        Assert.Equal(1234.5, ComfyEventParser.ParseBinary(frame, state).CreditsUsed);
        Assert.Null(ComfyEventParser.ParseBinary(frame, state with { NodeId = "other" }).CreditsUsed);
        Assert.Equal(state, ComfyEventParser.ParseBinary([1, 2], state));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), uint.MaxValue); Assert.Equal(state, ComfyEventParser.ParseBinary(frame, state));
    }

    [Fact]
    public async Task Comfy_key_is_sent_only_in_sensitive_extra_data_for_api_nodes_and_redacted_on_error()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        using var client = new ComfyClient("http://localhost:8188", http, () => "test-private-key");
        await client.SubmitAsync(Catalog().ReadWorkflow(Pack(), Pack().Workflows[0]), Guid.NewGuid(), TestContext.Current.CancellationToken);
        var payload = JsonNode.Parse(handler.Body!)!; Assert.Equal("test-private-key", payload["extra_data"]!["api_key_comfy_org"]!.GetValue<string>());
        Assert.DoesNotContain("test-private-key", payload["prompt"]!.ToJsonString());
        await client.SubmitAsync(new JsonObject { ["save"] = new JsonObject { ["class_type"] = "SaveImage" } }, Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Null(JsonNode.Parse(handler.Body!)!["extra_data"]);
        handler.Fail = true;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SubmitAsync(Catalog().ReadWorkflow(Pack(), Pack().Workflows[0]), Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("test-private-key", error.Message); Assert.Contains("[redacted]", error.Message);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Body; public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(Fail ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = new StringContent(Fail ? "test-private-key invalid" : "{\"prompt_id\":\"job\"}") };
        }
    }
    private sealed class Connection : IComfyConnection
    {
        public ComfyServerAddress Address { get; } = ComfyServerAddress.Parse("http://localhost:8188");
        public TimeSpan ConnectionTimeout { get; set; }
        public ComfyServerCapabilities Server { get; init; } = Capabilities();
        public List<JsonObject> Graphs { get; } = []; public List<string> Uploads { get; } = [];
        public int FailAt { get; init; }
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((new ComfyServerInfo("1.0", "test", "test", []), Server));
        public Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken cancellationToken = default)
        { Uploads.Add(semantic); return Task.FromResult(semantic + ".png"); }
        public Task<ComfyExecutionResult> ExecuteAsync(JsonObject graph, IProgress<AiOperationState>? progress = null, CancellationToken cancellationToken = default)
        {
            Graphs.Add((JsonObject)graph.DeepClone()); if (Graphs.Count == FailAt) throw new InvalidOperationException("API unavailable");
            var count = graph["gpt"]!["inputs"]!["n"]!.GetValue<int>();
            return Task.FromResult(new ComfyExecutionResult("job", JsonDocument.Parse("{}"), Enumerable.Range(0, count).Select(i => new ComfyImageReference(i + ".png", "", "output", "save")).ToArray()) { CreditsUsed = count * 2 });
        }
        public Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken cancellationToken = default)
        { var bitmap = Pixels.NewColor(112, 96); bitmap.Erase(SKColors.CornflowerBlue); return Task.FromResult(bitmap); }
        public void Dispose() { }
    }
}
