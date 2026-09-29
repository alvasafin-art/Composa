using System.Net;
using System.Text;
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

public class AiInfrastructureTests
{
    [Theory]
    [InlineData(AiTaskKind.GenerativeFill, "Replace the selected owl with a realistic black cat while preserving the surrounding forest.")]
    [InlineData(AiTaskKind.RemoveObject, "")]
    public async Task Bundled_inpaint_changes_a_real_selected_region_when_live_files_are_requested(AiTaskKind task, string prompt)
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_URL");
        var sourcePath = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_SOURCE");
        var maskPath = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_MASK");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(maskPath)
            || !File.Exists(sourcePath) || !File.Exists(maskPath)) return;

        var source = ImageFiles.Load(sourcePath);
        using var encodedMask = ImageFiles.Load(maskPath);
        var selection = Pixels.NewMask(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                selection.GetPixelSpan()[y * selection.RowBytes + x] = encodedMask.GetPixel(x, y).Alpha;
        var document = new Document(source.Width, source.Height);
        var background = Layer.Raster("Source", source);
        document.Layers.Add(background);
        document.SetActive(background.Id);
        var session = new EditorSession(document);
        session.PreviewSelection(selection);
        using var before = session.Flatten();
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest
        {
            Task = task, Prompt = prompt,
            Settings = new AiGenerationSettings
            {
                Width = 1024, Height = 1024, Seed = 7,
                Values = new() { ["maskGrow"] = 8, ["maskBlend"] = 32, ["maskContext"] = 2.0 }
            }
        }, TestContext.Current.CancellationToken);

        var result = session.ActiveLayer!.Pixels!;
        var changed = 0;
        var reconstructed = 0;
        var selected = 0;
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                if (selection.GetPixelSpan()[y * selection.RowBytes + x] < 128) continue;
                selected++;
                var a = before.GetPixel(x, y);
                var b = result.GetPixel(x, y);
                if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 24) changed++;
                if (b.Red + b.Green + b.Blue > 30) reconstructed++;
            }
        Assert.True(changed > selected / 20, $"Only {changed} of {selected} selected pixels changed.");
        if (task == AiTaskKind.RemoveObject)
            Assert.True(reconstructed > selected / 20, $"Only {reconstructed} of {selected} selected pixels were reconstructed from black.");
        Directory.CreateDirectory(Screenshots.Folder);
        using var displayed = session.Flatten();
        ImageFiles.Save(displayed, Path.Combine(Screenshots.Folder, "live-" + task + ".png"), ExportFormat.Png);
    }

    [Fact]
    public void Alpha_masks_are_uploaded_as_opaque_grayscale_for_Comfy_image_nodes()
    {
        using var mask = Pixels.NewMask(4, 3);
        mask.GetPixelSpan()[1 * mask.RowBytes + 2] = 255;

        var bytes = ComfyClient.EncodeUploadPng(mask);
        using var stream = new MemoryStream(bytes);
        using var decoded = ImageFiles.Load(stream, "mask.png");

        Assert.Equal(SKColors.Black, decoded.GetPixel(0, 0));
        Assert.Equal(SKColors.White, decoded.GetPixel(2, 1));
    }

    [Fact]
    public void Upscale_selection_keeps_canvas_size_and_inserts_a_fitted_masked_patch()
    {
        var session = EditorSession.NewCanvas(20, 16, SKColors.White);
        session.SelectRect(new SKRect(4, 3, 10, 9));
        var upscaled = Pixels.NewColor(24, 24);
        upscaled.Erase(SKColors.CornflowerBlue);

        AiTaskService.Insert(new EditorCommandService(session), AiTaskKind.Upscale, AiOutputMode.NewLayer, [upscaled], new SKRectI(4, 3, 10, 9));

        Assert.Equal((20, 16), (session.Document.Width, session.Document.Height));
        Assert.Equal((6, 6), (session.ActiveLayer!.Pixels!.Width, session.ActiveLayer.Pixels.Height));
        Assert.NotNull(session.ActiveLayer.Mask);
        Assert.Equal((4d, 3d, 6d, 6d), (session.ActiveLayer.Transform.X, session.ActiveLayer.Transform.Y,
            session.ActiveLayer.Transform.Width, session.ActiveLayer.Transform.Height));
    }

    [Fact]
    public async Task Bundled_upscale_keeps_the_live_canvas_size_when_a_selection_exists()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_URL");
        if (string.IsNullOrWhiteSpace(url)) return;
        var session = EditorSession.NewCanvas(64, 64, SKColors.CornflowerBlue);
        session.SelectRect(new SKRect(12, 10, 52, 50));
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));

        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest
        {
            Task = AiTaskKind.Upscale,
            Settings = new AiGenerationSettings { Values = new() { ["upscaleModel"] = "4x-UltraSharpV2.safetensors" } }
        }, TestContext.Current.CancellationToken);

        Assert.Equal((64, 64), (session.Document.Width, session.Document.Height));
        Assert.Equal((40, 40), (session.ActiveLayer!.Pixels!.Width, session.ActiveLayer.Pixels.Height));
        Assert.NotNull(session.ActiveLayer.Mask);
        Assert.Equal((12d, 10d, 40d, 40d), (session.ActiveLayer.Transform.X, session.ActiveLayer.Transform.Y,
            session.ActiveLayer.Transform.Width, session.ActiveLayer.Transform.Height));
    }

    [Theory]
    [InlineData(AiTaskKind.SelectSubject)]
    [InlineData(AiTaskKind.ObjectSelection)]
    public async Task Bundled_subject_selection_returns_an_editable_mask_when_live_files_are_requested(AiTaskKind task)
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_URL");
        var sourcePath = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_SOURCE");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return;
        var source = ImageFiles.Load(sourcePath);
        var document = new Document(source.Width, source.Height);
        var layer = Layer.Raster("Source", source);
        document.Layers.Add(layer);
        document.SetActive(layer.Id);
        var session = new EditorSession(document);
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));

        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest { Task = task }, TestContext.Current.CancellationToken);

        Assert.NotNull(session.Selection);
        var selected = 0;
        foreach (var value in session.Selection!.GetPixelSpan()) if (value >= 128) selected++;
        Assert.InRange(selected, 1, source.Width * source.Height - 1);
        Assert.Equal("AI " + task.DisplayName(), session.History.UndoName);
    }

    [Fact]
    public void Change_background_result_is_an_editable_background_and_untouched_subject_group()
    {
        var session = EditorSession.NewCanvas(20, 16, SKColors.White);
        session.SelectRect(new SKRect(4, 3, 10, 9));
        using var inputs = AiTaskInputPreparer.Prepare(session, new AiTaskRequest { Task = AiTaskKind.ChangeBackground });
        var generated = Pixels.NewColor(20, 16);
        generated.Erase(SKColors.CornflowerBlue);

        AiTaskService.Insert(new EditorCommandService(session), AiTaskKind.ChangeBackground, AiOutputMode.LayerGroup,
            [generated], session.Document.Bounds, inputs);

        var group = Assert.Single(session.Document.Layers.Where(layer => layer.IsGroup));
        Assert.Equal(["AI Background", "Original Subject"], group.Children.Select(layer => layer.Name));
        Assert.Contains("background", group.Children[0].Tags);
        Assert.Contains("product", group.Children[1].Tags);
        Assert.Null(group.Children[0].Mask);
        Assert.Equal((byte)255, group.Children[1].Mask!.GetPixel(6, 5).Alpha);
    }

    [Fact]
    public void Background_workflow_does_not_condition_on_or_redraw_the_original_subject()
    {
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var engine = Assert.Single(catalog.Profiles);
        var binding = engine.Binding(AiTaskKind.ChangeBackground)!;
        Assert.Equal("background", binding.Workflow);
        Assert.DoesNotContain("sourceImage", binding.Inputs.Keys);
        Assert.DoesNotContain("preprocessedImage", binding.Inputs.Keys);
        var graph = JsonNode.Parse(File.ReadAllText(Path.Combine(catalog.DirectoryOf(engine), engine.Workflow(binding.Workflow).File)))!.AsObject();
        var bound = WorkflowBinder.Bind(graph, binding, new Dictionary<string, object?>
        { ["prompt"] = "empty beach", ["negativePrompt"] = "", ["seed"] = 7L, ["width"] = 1024, ["height"] = 768 });
        Assert.Equal("prompt", bound["sampler"]!["inputs"]!["positive"]![0]!.GetValue<string>());
        Assert.False(bound.ContainsKey("source"));
        Assert.False(bound.ContainsKey("crop"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bundled_change_background_uses_the_live_masked_workflow_when_requested(bool manualSelection)
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_URL");
        var sourcePath = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_SOURCE");
        var maskPath = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_MASK");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(maskPath)
            || !File.Exists(sourcePath) || !File.Exists(maskPath)) return;
        var source = ImageFiles.Load(sourcePath);
        using var encodedMask = ImageFiles.Load(maskPath);
        var selection = Pixels.NewMask(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                selection.GetPixelSpan()[y * selection.RowBytes + x] = encodedMask.GetPixel(x, y).Alpha;
        var document = new Document(source.Width, source.Height);
        var layer = Layer.Raster("Source", source);
        document.Layers.Add(layer);
        document.SetActive(layer.Id);
        var session = new EditorSession(document);
        if (manualSelection) session.PreviewSelection(selection);
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));

        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest
        {
            Task = AiTaskKind.ChangeBackground, Prompt = "a softly blurred warm sunset beach",
            Settings = new AiGenerationSettings
            {
                Width = 1024, Height = 1024, Seed = 11,
                Values = new() { ["maskGrow"] = 8, ["maskBlend"] = 32, ["maskContext"] = 2.0 }
            }
        }, TestContext.Current.CancellationToken);

        var group = Assert.Single(session.Document.Layers, item => item.IsGroup);
        Assert.Equal(["AI Background", "Original Subject"], group.Children.Select(item => item.Name));
        Assert.Equal(AiOperationStatus.Completed, service.Operation?.Status);
        var appliedMask = group.Children[1].Mask!;
        if (!manualSelection)
        {
            Assert.Null(session.Selection);
            Assert.Equal("AI Change Background", session.History.UndoName);
        }
        using var displayed = session.Flatten();
        var changedBackground = 0;
        var backgroundPixels = 0;
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var original = source.GetPixel(x, y);
                var output = displayed.GetPixel(x, y);
                var alpha = appliedMask.GetPixel(x, y).Alpha;
                if (alpha == 255) Assert.Equal(original, output);
                if (alpha != 0) continue;
                backgroundPixels++;
                if (Math.Abs(original.Red - output.Red) + Math.Abs(original.Green - output.Green) + Math.Abs(original.Blue - output.Blue) > 40)
                    changedBackground++;
            }
        Assert.True(changedBackground > backgroundPixels / 4, "The background did not visibly change.");
        Directory.CreateDirectory(Screenshots.Folder);
        ImageFiles.Save(displayed, Path.Combine(Screenshots.Folder, manualSelection ? "live-ChangeBackground.png" : "live-ChangeBackground-auto.png"), ExportFormat.Png);
        if (!manualSelection)
        {
            session.Undo();
            Assert.Single(session.Document.Layers);
            Assert.Null(session.Selection);
        }
    }

    [Fact]
    public async Task Bundled_engine_runs_end_to_end_when_live_Comfy_is_requested()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY_URL");
        if (string.IsNullOrWhiteSpace(url)) return;
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var compatibility = await service.TestConnectionAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(compatibility);
        Assert.True(compatibility.IsCompatible, AiTaskService.CompatibilityMessage(service.SelectedEngine!, compatibility));
        var session = EditorSession.NewCanvas(512, 512);

        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest
        {
            Task = AiTaskKind.GenerateImage,
            Prompt = "a small red cube on a clean white studio background",
            Settings = new AiGenerationSettings { Width = 512, Height = 512, Seed = 1 }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationStatus.Completed, service.Operation?.Status);
        Assert.Equal(2, session.Document.Layers.Count);
        Assert.Contains("ai-generated", session.ActiveLayer!.Tags);

        foreach (var model in new[] { "4x-UltraSharpV2.safetensors", "4x_NMKD-Siax_200k.pth" })
        {
            var upscaleSession = EditorSession.NewCanvas(64, 64, SkiaSharp.SKColors.CornflowerBlue);
            await service.RunAsync(new EditorCommandService(upscaleSession), new AiTaskRequest
            {
                Task = AiTaskKind.Upscale,
                Settings = new AiGenerationSettings { Values = new() { ["upscaleModel"] = model } }
            }, TestContext.Current.CancellationToken);
            Assert.Equal((256, 256), (upscaleSession.Document.Width, upscaleSession.Document.Height));
            Assert.Equal(AiOperationStatus.Completed, service.Operation?.Status);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:8188", "http://127.0.0.1:8188", "ws://127.0.0.1:8188/ws")]
    [InlineData("https://render.example:443/", "https://render.example", "wss://render.example/ws")]
    public void Comfy_url_is_normalized_and_maps_to_websocket(string input, string http, string socket)
    {
        var address = ComfyServerAddress.Parse(input);
        Assert.Equal(http, address.ToString());
        Assert.Equal(socket, address.WebSocket.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("localhost:8188")]
    [InlineData("ftp://localhost")]
    [InlineData("http://localhost:8188/api")]
    [InlineData("http://user:pass@localhost:8188")]
    public void Comfy_url_rejects_ambiguous_or_unsafe_values(string input) => Assert.Throws<FormatException>(() => ComfyServerAddress.Parse(input));

    [Fact]
    public async Task Connection_test_reads_server_information_nodes_and_assets()
    {
        using var http = new HttpClient(new JsonHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/system_stats" => """{"system":{"os":"windows","comfyui_version":"0.9","python_version":"3.12"},"devices":[{"name":"Intel XPU"}]}""",
            "/object_info" => """{"CheckpointLoaderSimple":{"input":{"required":{"ckpt_name":[["model.safetensors"],{}]}}},"LoraLoader":{"input":{"required":{"lora_name":[["style.safetensors"],{}]}}},"UpscaleModelLoader":{"input":{"required":{"model_name":["COMBO",{"options":["4x-UltraSharpV2.safetensors","4x_NMKD-Siax_200k.pth"]}]}}}}""",
            _ => "{}"
        }));
        using var client = new ComfyClient("http://127.0.0.1:8188", http);

        var (info, capabilities) = await client.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("0.9", info.Version);
        Assert.Equal("0.9", capabilities.Version);
        Assert.Contains("Intel XPU", info.Devices);
        Assert.Contains("CheckpointLoaderSimple", capabilities.NodeTypes);
        Assert.Contains("model.safetensors", capabilities.Assets[EngineAssetKind.Checkpoint]);
        Assert.Contains("style.safetensors", capabilities.Assets[EngineAssetKind.Lora]);
        Assert.Contains("4x_NMKD-Siax_200k.pth", capabilities.Assets[EngineAssetKind.Upscaler]);
    }

    [Fact]
    public async Task Connection_state_moves_from_connecting_to_connected()
    {
        using var http = new HttpClient(new JsonHandler(request => request.RequestUri!.AbsolutePath == "/system_stats"
            ? """{"system":{"comfyui_version":"0.9"},"devices":[]}""" : "{}"));
        var service = new AiTaskService(() => "http://localhost:8188", Path.Combine(Path.GetTempPath(), "missing-composa-engines"),
            url => new ComfyClient(url, http));
        var states = new List<ComfyConnectionState>();
        service.StateChanged += () => states.Add(service.ConnectionState);

        await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal([ComfyConnectionState.Connecting, ComfyConnectionState.Connected], states);
    }

    [Fact]
    public async Task Queue_and_interrupt_use_the_Comfy_http_contract()
    {
        var seenInterrupt = false;
        using var http = new HttpClient(new JsonHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/interrupt") { seenInterrupt = true; return "{}"; }
            return """{"queue_running":[[1,"running"]],"queue_pending":[[2,"mine"],[3,"other"]]}""";
        }));
        using var client = new ComfyClient("http://localhost:8188", http);

        var queue = await client.QueueAsync("mine", TestContext.Current.CancellationToken);
        await client.InterruptAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, queue.Running);
        Assert.Equal(2, queue.Pending);
        Assert.Equal(1, queue.Position);
        Assert.True(seenInterrupt);
    }

    [Fact]
    public void Websocket_events_cover_running_progress_completion_cancel_and_error()
    {
        var state = new AiOperationState { PromptId = "p", Status = AiOperationStatus.Queued };
        state = ComfyEventParser.Parse("""{"type":"executing","data":{"prompt_id":"p","node":"7"}}""", "p", state);
        Assert.Equal(AiOperationStatus.Running, state.Status);
        Assert.True(state.IsIndeterminate);
        state = ComfyEventParser.Parse("""{"type":"progress","data":{"prompt_id":"p","node":"7","value":3,"max":10}}""", "p", state);
        Assert.Equal(3, state.Value);
        Assert.Equal(10, state.Maximum);
        state = ComfyEventParser.Parse("""{"type":"executing","data":{"prompt_id":"p","node":null}}""", "p", state);
        Assert.Equal(AiOperationStatus.Running, state.Status);
        Assert.Equal("Finalizing result", state.Stage);
        state = ComfyEventParser.Parse("""{"type":"execution_success","data":{"prompt_id":"p"}}""", "p", state);
        Assert.Equal(AiOperationStatus.Completed, state.Status);

        var cancelled = ComfyEventParser.Parse("""{"type":"execution_interrupted","data":{"prompt_id":"p"}}""", "p", state);
        Assert.Equal(AiOperationStatus.Cancelled, cancelled.Status);
        var failed = ComfyEventParser.Parse("""{"type":"execution_error","data":{"prompt_id":"p","exception_message":"bad node"}}""", "p", state);
        Assert.Equal(AiOperationStatus.Failed, failed.Status);
        Assert.Equal("bad node", failed.Error);
    }

    [Fact]
    public void History_result_finds_output_images()
    {
        using var json = JsonDocument.Parse("""{"abc":{"outputs":{"9":{"images":[{"filename":"out.png","subfolder":"jobs","type":"output"}]}}}}""");
        var images = ComfyClient.FindImages(json.RootElement, "abc");
        Assert.Single(images);
        Assert.Equal("out.png", images[0].Filename);
        Assert.Equal("9", images[0].NodeId);
    }

    [Fact]
    public void Ai_settings_round_trip_without_secrets_or_local_paths()
    {
        var settings = new Settings { ComfyServerUrl = "http://192.168.1.50:8188", AiEngineId = "flux", AiMegapixels = 1.5, AiSeed = 123,
            AiReferenceMegapixels = null, AiMaskGrow = 12, AiMaskBlend = 24, AiMaskContext = 2.5,
            AiLoras = [new("style.safetensors", 0.7)], ComfyConnectionTimeoutSeconds = 12 };
        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(settings.ComfyServerUrl, loaded.ComfyServerUrl);
        Assert.Equal("flux", loaded.AiEngineId);
        Assert.Equal(1.5, loaded.AiMegapixels);
        Assert.Null(loaded.AiReferenceMegapixels);
        Assert.Equal(12, loaded.AiMaskGrow);
        Assert.Equal(24, loaded.AiMaskBlend);
        Assert.Equal(2.5, loaded.AiMaskContext);
        Assert.Equal(12, loaded.ComfyConnectionTimeoutSeconds);
        Assert.Equal("4x-UltraSharpV2.safetensors", loaded.AiUpscalerModel);
        Assert.Equal("style.safetensors", loaded.AiLoras[0].Name);
    }

    [Fact]
    public void Bundled_flux_engine_has_valid_workflows_and_explicit_binding_targets()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ai", "engines");
        var catalog = new EngineCatalog(root);
        var engine = Assert.Single(catalog.Profiles, profile => profile.Id == "flux2-klein-intel-xpu");
        Assert.Empty(catalog.Errors);

        foreach (var task in engine.Tasks)
        {
            var declaration = engine.Workflow(task.Workflow);
            var path = Path.Combine(catalog.DirectoryOf(engine), declaration.File);
            var graph = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (var target in task.Inputs.Values)
                Assert.True(graph[target.NodeId]?["inputs"]?[target.Input] != null,
                    $"{task.Task}: missing {target.NodeId}.{target.Input} in {declaration.File}");
        }

        var relightBinding = engine.Binding(AiTaskKind.Relight)!;
        var relight = JsonNode.Parse(File.ReadAllText(Path.Combine(catalog.DirectoryOf(engine), engine.Workflow(relightBinding.Workflow).File)))!.AsObject();
        var bound = WorkflowBinder.Bind(relight, relightBinding, new Dictionary<string, object?>
        {
            ["sourceImage"] = "source.png", ["selectionMask"] = "mask.png", ["prompt"] = "warm side light",
            ["width"] = 1024, ["height"] = 768, ["seed"] = 1L
        });
        Assert.DoesNotContain(bound, node => node.Key.StartsWith("ref", StringComparison.Ordinal));
        Assert.Equal("basePos", bound["sampler"]!["inputs"]!["positive"]![0]!.GetValue<string>());
        Assert.Equal("EmptyFlux2LatentImage", bound["latent"]!["class_type"]!.GetValue<string>());
        Assert.False(bound["crop"]!["inputs"]!["mask_fill_holes"]!.GetValue<bool>());
        Assert.DoesNotContain(bound, node => node.Value?["class_type"]?.GetValue<string>() == "InpaintModelConditioning");
    }

    private sealed class JsonHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
    }
}
