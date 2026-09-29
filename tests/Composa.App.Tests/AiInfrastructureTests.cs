using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;

namespace Composa.App.Tests;

public class AiInfrastructureTests
{
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
            AiReferenceMegapixels = null, AiMaskGrow = 12, AiMaskBlend = 24,
            AiLoras = [new("style.safetensors", 0.7)], ComfyConnectionTimeoutSeconds = 12 };
        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(settings.ComfyServerUrl, loaded.ComfyServerUrl);
        Assert.Equal("flux", loaded.AiEngineId);
        Assert.Equal(1.5, loaded.AiMegapixels);
        Assert.Null(loaded.AiReferenceMegapixels);
        Assert.Equal(12, loaded.AiMaskGrow);
        Assert.Equal(24, loaded.AiMaskBlend);
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

        var edit = JsonNode.Parse(File.ReadAllText(Path.Combine(catalog.DirectoryOf(engine), engine.Workflow("edit").File)))!.AsObject();
        var bound = WorkflowBinder.Bind(edit, engine.Binding(AiTaskKind.Relight)!, new Dictionary<string, object?>
        {
            ["sourceImage"] = "source.png", ["prompt"] = "warm side light", ["width"] = 1024, ["height"] = 768, ["seed"] = 1L
        });
        Assert.DoesNotContain(bound, node => node.Key.StartsWith("ref", StringComparison.Ordinal));
        Assert.Equal("basePos", bound["sampler"]!["inputs"]!["positive"]![0]!.GetValue<string>());
    }

    private sealed class JsonHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
    }
}
