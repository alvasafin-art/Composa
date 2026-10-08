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
    public void Original_size_enlarges_the_API_context_image_and_generation_request_together()
    {
        var session=EditorSession.NewCanvas(320,240,SKColors.White); session.SelectRect(new SKRect(60,50,120,110));
        var request=new AiTaskRequest { Task=AiTaskKind.GenerativeFill,Settings=new() { Values=new() { ["imageOriginalSize"]=true } } };
        using var inputs=AiTaskInputPreparer.Prepare(session,request); using var api=new PartnerImageInputs(inputs,request);
        var source=api.Images["apiSource"]; Assert.True((long)source.Width*source.Height >= Composa.Model.DocumentLimits.MinimumGenerationPixels);
        var files=api.Images.ToDictionary(p=>p.Key,p=>p.Key+".png");
        var graph=api.Bind(Catalog().ReadWorkflow(Pack(),Pack().Workflows[0]),Pack(),files,17); var node=graph["gpt"]!["inputs"]!;
        Assert.True((long)node["model.custom_width"]!.GetValue<int>()*node["model.custom_height"]!.GetValue<int>() >= Composa.Model.DocumentLimits.MinimumGenerationPixels);
        using var result=api.Finish(GeneratedPatch(inputs,api,SKColors.CornflowerBlue));
        Assert.Equal((320,240),(result.Width,result.Height));
    }

    // A masked model preserves its source context. This fixture distinguishes local
    // insertion from the separate tests which deliberately corrupt that context.
    internal static SKBitmap GeneratedPatch(AiTaskInputs inputs, PartnerImageInputs api, SKColor color)
    {
        var bounds = api.SourceBounds;
        using var context = inputs.ExpansionBounds != null ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
        var result = Pixels.NewColor(bounds.Width, bounds.Height);
        using (var draw = new SKCanvas(result)) draw.DrawImage(Pixels.ImageOf(context), -bounds.Left, -bounds.Top);
        var core = inputs.OutputMask != null && inputs.PreprocessedImage?.Width != inputs.ContextImage.Width
            ? inputs.OutputMask : inputs.BackgroundMask ?? inputs.SelectionMask ?? inputs.OutputMask;
        for (var y = 0; y < result.Height; y++) for (var x = 0; x < result.Width; x++)
            if (core?.GetPixel(x + bounds.Left, y + bounds.Top).Alpha >= 128) result.SetPixel(x, y, color);
        Pixels.Invalidate(result); return result;
    }
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
        {"input":{"required":{"model":["COMFY_DYNAMICCOMBO_V3",{"options":[{"key":"gpt-image-2.5-sunburst","inputs":{"required":{"quality":["COMBO",{"options":["low","medium","high","xhigh","max"]}],"size":["COMBO",{"options":["auto","1024x1024","1024x1536","Custom"]}]}}}]}]}}}
        """)!.AsObject();
        definitions["price_badge"] = new JsonObject { ["expr"] = """
            ($ranges := {"gpt-image-2.5-sunburst":{"low":[0.0023,0.0283]}};
             $perImage := {"gpt-image-2.5-sunburst":[0.0117,0.0176]};
             $presets := {"gpt-image-2.5":{"low":{"1024x1024":0.0084}}};)
            """ };
        return new() { Version = "1.0", NodeTypes = Pack().RequiredNodeTypes.ToHashSet(), NodeDefinitions = new() { ["OpenAIGPTImageNodeV2"] = definitions } };
    }
    internal static AiTaskService Service(Connection connection, bool key = true) => new(() => "http://localhost:8188", Catalog().Root, _ => connection)
        { SelectedEngine = Pack(), ApiKey = () => key ? "test-only-comfy-key" : null };

    [Fact]
    public void Api_pack_has_one_official_node_no_local_weights_and_local_pack_stays_default()
    {
        var catalog = Catalog(); Assert.Equal(3, catalog.Profiles.Count); Assert.False(catalog.Profiles[0].PaidApi);
        var engine = Pack(); Assert.True(engine.PaidApi); Assert.Equal("gpt-image-2.5-sunburst", engine.ApiModel);
        Assert.Empty(engine.RequiredAssets); Assert.Empty(catalog.ModelSlots(engine)); Assert.False(engine.Lora.Supported);
        Assert.DoesNotContain("ImageToMask", engine.RequiredNodeTypes);
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
    public void Masked_edits_send_only_source_and_ordered_references_and_restore_odd_canvas(int references)
    {
        var session = EditorSession.NewCanvas(79, 61, SKColors.White); session.SelectRect(new SKRect(25, 20, 45, 40));
        using var reference = Pixels.NewColor(17, 13);
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeFill, ReferenceImages = Enumerable.Repeat(reference, references).ToArray(), ReferenceMegapixels = null,
            Prompt = "replace the object with a blue cup", AdditionalPrompt = "Preserve the framing.",
            Settings = new() { Values = new() { ["maskGrow"] = 0, ["maskBlend"] = 4, ["gptContextPadding"] = 10 } } };
        using var inputs = AiTaskInputPreparer.Prepare(session, request); using var api = new PartnerImageInputs(inputs, request);
        // Reverse uploads to prove image 1 cannot accidentally become a reference.
        var template = Catalog().ReadWorkflow(Pack(), Pack().Workflows[0]);
        template["gpt"]!["inputs"]!["model.mask"] = new JsonArray("stale_mask", 0);
        template["gpt"]!["inputs"]!["model.images.image_9"] = new JsonArray("stale_image", 0);
        var graph = api.Bind(template, Pack(), api.Images.Reverse().ToDictionary(pair => pair.Key, pair => pair.Key + ".png"), 42);
        var node = graph["gpt"]!["inputs"]!.AsObject();
        Assert.False(node.ContainsKey("model.mask"));
        Assert.DoesNotContain("apiMask", api.Images.Keys);
        Assert.DoesNotContain(graph, pair => pair.Value?["class_type"]?.GetValue<string>() == "ImageToMask");
        Assert.Equal(references + 1, node.Count(pair => pair.Key.StartsWith("model.images.image_")));
        Assert.Equal("apiSource.png", graph["composa_api_image_1"]!["inputs"]!["image"]!.GetValue<string>());
        for (var i = 1; i <= references; i++) Assert.Equal($"referenceImage{i}.png", graph[$"composa_api_image_{i + 1}"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal(request.Prompt + "\n\n" + request.AdditionalPrompt, node["prompt"]!.GetValue<string>());
        var generated = GeneratedPatch(inputs, api, SKColors.CornflowerBlue);
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
        var request = new AiTaskRequest { Task = AiTaskKind.RemoveObject, Settings = new() { Values = new() { ["maskGrow"] = 0, ["maskBlend"] = 0, ["gptContextPadding"] = 10 } } };
        using var inputs = AiTaskInputPreparer.Prepare(session, request); using var api = new PartnerImageInputs(inputs, request);
        Assert.Equal((90, 90), (api.Images["apiSource"].Width, api.Images["apiSource"].Height));
        Assert.Equal(SKColors.Black, api.Images["apiSource"].GetPixel(45, 45));
        Assert.Equal(SKColors.White, inputs.ContextImage.GetPixel(250, 190));
        Assert.Equal(SKColors.White, session.ActiveLayer!.Pixels!.GetPixel(250, 190));
    }

    [Fact]
    public void Fill_harmonize_and_relight_keep_original_crop_and_do_not_describe_an_absent_mask()
    {
        var s = EditorSession.NewCanvas(500, 400, SKColors.White); s.SelectRect(new SKRect(240, 180, 260, 200));
        foreach (var task in new[] { AiTaskKind.GenerativeFill, AiTaskKind.Harmonize, AiTaskKind.Relight })
        {
            var request = new AiTaskRequest { Task = task, Prompt = "make it blue", Settings = new() { Values = new() { ["maskGrow"] = 0, ["maskBlend"] = 0, ["gptContextPadding"] = 10 } } };
            using var inputs = AiTaskInputPreparer.Prepare(s, request); using var api = new PartnerImageInputs(inputs, request);
            Assert.Equal(SKColors.White, api.Images["apiSource"].GetPixel(20, 20));
            var graph = api.Bind(Catalog().ReadWorkflow(Pack(), Pack().Workflows[0]), Pack(), api.Images.ToDictionary(p => p.Key, p => p.Key + ".png"), 0);
            var prompt = graph["gpt"]!["inputs"]!["prompt"]!.GetValue<string>();
            Assert.Contains("make it blue", prompt); Assert.DoesNotContain("mask", prompt); Assert.DoesNotContain("x=", prompt);
            Assert.Equal(SKColors.White, inputs.ContextImage.GetPixel(250, 190));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 64)]
    [InlineData(32, 0)]
    [InlineData(32, 64)]
    public void Gpt_padding_is_independent_of_flux_context_blur_and_local_blend(int padding, int blend)
    {
        var s = EditorSession.NewCanvas(500, 400, SKColors.White); s.SelectRect(new SKRect(240, 180, 260, 200));
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeFill, Settings = new() { Values = new()
            { ["gptContextPadding"] = padding, ["maskBlend"] = blend, ["maskGrow"] = 64, ["maskBlur"] = 64, ["maskContext"] = 8 } } };
        using var inputs = AiTaskInputPreparer.Prepare(s, request); using var api = new PartnerImageInputs(inputs, request);
        Assert.Equal((90, 90), (api.Images["apiSource"].Width, api.Images["apiSource"].Height));
        Assert.Single(api.Images);
    }

    [Fact]
    public void Default_crop_restores_coordinates_at_canvas_edge_and_blends_soft_selection_once()
    {
        var s = EditorSession.NewCanvas(211, 173, SKColors.White); s.SelectRect(new SKRect(1, 50, 21, 70));
        var selection = Pixels.Clone(s.Selection!); selection.SetPixel(5, 60, new SKColor(0, 0, 0, 128)); Pixels.Invalidate(selection);
        s.ApplyAiSelection(AiTaskKind.ObjectSelection, selection);
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeFill, Settings = new() { Values = new() { ["maskBlend"] = 0, ["gptContextPadding"] = 32 } } };
        using var inputs = AiTaskInputPreparer.Prepare(s, request); using var api = new PartnerImageInputs(inputs, request);
        var source = api.Images["apiSource"]; Assert.Equal((90, 90), (source.Width, source.Height));
        var generated = GeneratedPatch(inputs, api, SKColors.CornflowerBlue);
        generated.SetPixel(10 - api.SourceBounds.Left, 60 - api.SourceBounds.Top, SKColors.Red); Pixels.Invalidate(generated);
        var result = api.Finish(generated);
        Assert.Equal(SKColors.Red, result.GetPixel(10, 60)); Assert.Equal(SKColors.White, result.GetPixel(10, 17));
        var edge = result.GetPixel(5, 60); Assert.Equal(SKColors.CornflowerBlue, edge); // selection opacity is not generation strength
        AiTaskService.Insert(new EditorCommandService(s), request.Task, AiOutputMode.NewLayerWithMask, [result], inputs.TargetBounds, inputs, true);
        using var displayed = s.Flatten(); Assert.Equal(edge, displayed.GetPixel(5, 60)); Assert.Equal(SKColors.Red, displayed.GetPixel(10, 60));
        s.Undo(); Assert.Single(s.Document.Layers); s.Redo(); using var redone = s.Flatten(); Assert.Equal(edge, redone.GetPixel(5, 60));
    }

    [Fact]
    public async Task Expand_with_selection_uses_fill_without_expanding_canvas_or_reusing_prompt()
    {
        var s = EditorSession.NewCanvas(300, 200, SKColors.White); s.SelectRect(new SKRect(100, 60, 160, 120));
        var connection = new Connection(); var service = Service(connection); var history = s.History.Count;
        await service.RunAsync(new EditorCommandService(s), new AiTaskRequest { Task = AiTaskKind.GenerativeExpand, Prompt = "stale prompt from Gen Fill", ExpansionMode = AiExpansionMode.WholeImage,
            Settings = new() { Width = 1024, Height = 1024, Values = new() { ["maskBlend"] = 4, ["maskBlur"] = 0, ["maskGrow"] = 0 } } }, TestContext.Current.CancellationToken);
        var prompt = connection.Graphs[0]["gpt"]!["inputs"]!["prompt"]!.GetValue<string>();
        Assert.Contains(AiPromptDefaults.Expand, prompt); Assert.DoesNotContain("stale prompt", prompt);
        Assert.Equal((300, 200), (s.Document.Width, s.Document.Height));
        using var image = s.Flatten(); Assert.Equal(SKColors.White, image.GetPixel(20, 20));
        Assert.True(image.GetPixel(130, 90).Blue > image.GetPixel(130, 90).Red + 60); // context-biased model output is corrected automatically
        Assert.Equal(history + 1, s.History.Count); s.Undo(); Assert.Single(s.Document.Layers);
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
            var generated = GeneratedPatch(inputs, api, SKColors.CornflowerBlue);
            using var result = api.Finish(generated); Assert.Equal((102, 76), (result.Width, result.Height));
            Assert.Equal(SKColors.CornflowerBlue, result.GetPixel(0, 0)); Assert.Equal(SKColors.White, result.GetPixel(30, 30));
        }
        session.SelectRect(new SKRect(25, 20, 45, 40));
        request = request with { Task = AiTaskKind.ChangeBackground, ExpansionBounds = null };
        using (var inputs = AiTaskInputPreparer.Prepare(session, request))
        using (var api = new PartnerImageInputs(inputs, request))
        {
            var generated = GeneratedPatch(inputs, api, SKColors.CornflowerBlue); var result = api.Finish(generated);
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
        Assert.Equal(new[] { "apiSource" }, connection.Uploads); Assert.Equal(connection.Uploads.Distinct(), connection.Uploads);
        Assert.Equal(history + 1, session.History.Count); Assert.Equal(count * 2, service.Operation!.CreditsUsed);
        if (count > 1) { Assert.Equal(count, session.AiVariantGroup!.Children.Count); Assert.Single(session.AiVariantGroup.Children, layer => layer.Visible); }
        session.Undo(); Assert.Single(session.Document.Layers);
    }

    [Theory]
    [InlineData(AiTaskKind.GenerativeFill)]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.Relight)]
    public async Task Cropped_edits_use_the_current_packs_additional_prompt_not_an_unprepared_request(AiTaskKind task)
    {
        var s = EditorSession.NewCanvas(200, 150, SKColors.White); s.SelectRect(new SKRect(50, 40, 90, 80));
        var connection = new Connection(); var service = Service(connection);
        service.AdditionalPromptForPack = id => id == Pack().Id ? "MY PACK INSTRUCTION" : "WRONG PACK";
        await service.RunAsync(new EditorCommandService(s), new() { Task = task, Prompt = "make it blue", AdditionalPrompt = "STALE INSTRUCTION" }, TestContext.Current.CancellationToken);
        var prompt = connection.Graphs[0]["gpt"]!["inputs"]!["prompt"]!.GetValue<string>();
        Assert.Contains("MY PACK INSTRUCTION", prompt); Assert.DoesNotContain("STALE INSTRUCTION", prompt);
        Assert.DoesNotContain("masked", prompt); Assert.Equal(new[] { "apiSource" }, connection.Uploads);
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

    [Theory]
    [InlineData(AiTaskKind.ImageEdit)]
    [InlineData(AiTaskKind.RemoveObject)]
    [InlineData(AiTaskKind.ChangeBackground)]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.Relight)]
    public async Task Full_image_operations_without_selection_run_a_real_edit_workflow(AiTaskKind task)
    {
        var session = EditorSession.NewCanvas(79, 61, SKColors.White); var connection = new Connection();
        var service = Service(connection); service.AdditionalPromptForPack = id => id == "chatgpt-image-2.5" ? "PACK INSTRUCTION" : "WRONG PACK";
        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest { Task = task, Prompt = "make the cup blue" }, TestContext.Current.CancellationToken);
        Assert.Single(connection.Graphs); Assert.Equal(new[] { "apiSource" }, connection.Uploads);
        var node = connection.Graphs[0]["gpt"]!["inputs"]!;
        Assert.Null(node["model.mask"]); Assert.Contains("PACK INSTRUCTION", node["prompt"]!.GetValue<string>());
        Assert.Equal(SKColors.CornflowerBlue, session.ActiveLayer!.Pixels!.GetPixel(0, 0));
        Assert.Equal(1, session.History.Count); Assert.Null(session.Selection);
        session.Undo(); Assert.Single(session.Document.Layers);
    }

    [Theory]
    [InlineData(AiExpansionMode.MaskedRegion, 3)]
    [InlineData(AiExpansionMode.WholeImage, 2)]
    public async Task Expansion_variants_use_actual_canvas_coordinates_and_undo_all_at_once(AiExpansionMode mode, int variants)
    {
        var session = EditorSession.NewCanvas(79, 61, SKColors.White); var connection = new Connection();
        await Service(connection).RunAsync(new EditorCommandService(session), new AiTaskRequest { Task = AiTaskKind.GenerativeExpand,
            ExpansionBounds = new(-11, -7, 91, 69), ExpansionMode = mode, ExpansionMinimumSide = mode == AiExpansionMode.WholeImage ? 0 : 1024,
            Settings = new() { Variants = variants, VariantMode = AiVariantMode.Batch } }, TestContext.Current.CancellationToken);
        Assert.Single(connection.Graphs); Assert.Equal((102, 76), (session.Document.Width, session.Document.Height));
        Assert.Equal(variants, session.AiVariantGroup!.Children.Count); Assert.Equal(1, session.History.Count);
        using var result = session.Flatten(); Assert.Equal(255, result.GetPixel(0, 0).Alpha);
        Assert.True(result.GetPixel(0, 0).Blue > result.GetPixel(0, 0).Red + 60);
        Assert.Equal(mode == AiExpansionMode.WholeImage ? SKColors.CornflowerBlue : SKColors.White, result.GetPixel(30, 30));
        session.Undo(); Assert.Single(session.Document.Layers); Assert.Equal((79, 61), (session.Document.Width, session.Document.Height));
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
    internal sealed class Connection : IComfyConnection
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
        { var node = Graphs[^1]["gpt"]!["inputs"]!; var bitmap = Pixels.NewColor(node["model.custom_width"]!.GetValue<int>(), node["model.custom_height"]!.GetValue<int>()); bitmap.Erase(SKColors.CornflowerBlue); return Task.FromResult(bitmap); }
        public void Dispose() { }
    }
}
