using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class QwenImageTests
{
    private static string Engines => Path.Combine(AppContext.BaseDirectory, "ai/engines");
    private static EditorSession Scene()
    {
        var session = EditorSession.NewCanvas(131, 97, SKColors.Gray);
        var png = Pixels.NewColor(21, 31);
        using (var draw = new SKCanvas(png)) using (var paint = new SKPaint { Color = SKColors.Coral }) draw.DrawRect(2, 2, 17, 27, paint);
        Pixels.Invalidate(png); session.AddImageLayer("PNG", png, new(65, 48), fit: false); return session;
    }
    private static AiTaskService Service(Connection connection)
    {
        var service = new AiTaskService(() => "http://localhost:8188", Engines, _ => connection, (input, _, _) => Pixels.Clone(input.ObjectAlpha));
        service.SelectedEngine = service.Engines.Find(QwenImageWorkflow.EngineId); return service;
    }
    internal static AiTaskRequest Request(AiTaskKind task) => new()
    {
        Task = task, Prompt = task is AiTaskKind.Harmonize or AiTaskKind.MatchToScene ? "" : "natural photographic lighting",
        Settings = new() { Seed = 73, Width = 131, Height = 97, Values = new() { ["imageOriginalSize"] = false } }
    };

    [Fact]
    public void Pack_has_three_shared_model_slots_and_nine_generative_tasks()
    {
        var catalog = new EngineCatalog(Engines); Assert.Empty(catalog.Errors);
        var engine = catalog.Find(QwenImageWorkflow.EngineId)!;
        Assert.Equal(QwenImageWorkflow.EngineId, engine.Id); Assert.False(engine.PaidApi); Assert.False(engine.Lora.Supported);
        Assert.Equal(9, engine.Tasks.Count); Assert.Null(engine.Binding(AiTaskKind.Upscale)); Assert.Null(engine.Binding(AiTaskKind.ObjectSelection));
        Assert.Equal(3, catalog.ModelSlots(engine).Count);
        foreach (var workflow in engine.Workflows)
        {
            var graph = catalog.ReadWorkflow(engine, workflow);
            Assert.Equal("BasicGuider", graph["guider"]!["class_type"]!.GetValue<string>());
            Assert.Equal(9, graph["sigmas"]!["inputs"]!["sigmas"]!.GetValue<string>().Split(',').Length);
            Assert.DoesNotContain(graph, node => node.Value?["class_type"]?.GetValue<string>() is "KSampler" or "ModelSamplingAuraFlow");
        }
    }

    [Theory]
    [InlineData(AiTaskKind.GenerateImage)]
    [InlineData(AiTaskKind.ImageEdit)]
    [InlineData(AiTaskKind.GenerativeFill)]
    [InlineData(AiTaskKind.RemoveObject)]
    [InlineData(AiTaskKind.GenerativeExpand)]
    [InlineData(AiTaskKind.ChangeBackground)]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.MatchToScene)]
    [InlineData(AiTaskKind.Relight)]
    public async Task Every_tool_routes_to_Qwen_preserves_frame_and_is_atomic_and_undoable(AiTaskKind task)
    {
        var session = Scene();
        if (task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject) session.SelectRect(new SKRect(50, 30, 80, 65));
        var request = Request(task);
        if (task == AiTaskKind.GenerativeExpand) request = request with { ExpansionBounds = new(-10, -10, 145, 110), ExpansionMinimumSide = 192 };
        var connection = new Connection(); var service = Service(connection);
        var before = session.History.CurrentId; var history = session.History.Count; var width = session.Document.Width;
        var source = session.ActiveLayer!.Pixels!.Bytes.ToArray();
        await service.RunAsync(new EditorCommandService(session), request, TestContext.Current.CancellationToken);
        Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
        Assert.Equal(task == AiTaskKind.ChangeBackground ? 2 : 1, connection.Graphs.Count);
        Assert.All(connection.Graphs, graph => Assert.Equal("TextEncodeQwenImage21", graph["conditioning"]!["class_type"]!.GetValue<string>()));
        if (task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.GenerativeExpand)
        {
            var graph = Assert.Single(connection.Graphs);
            Assert.Equal("SetLatentNoiseMask", graph["masked"]!["class_type"]!.GetValue<string>());
            Assert.Equal("red", graph["mask"]!["inputs"]!["channel"]!.GetValue<string>());
            Assert.Equal("masked", graph["sample"]!["inputs"]!["latent_image"]![0]!.GetValue<string>());
            Assert.False(graph.ContainsKey("align"));
        }
        Assert.Equal(history + 1, session.History.Count);
        Assert.Equal(source, session.Document.AllLayers().First(layer => layer.Name == "PNG").Pixels!.Bytes);
        session.Undo(); Assert.Equal(before, session.History.CurrentId); Assert.Equal(width, session.Document.Width);
    }

    [Fact]
    public async Task Harmonize_uses_tested_prompt_and_never_uploads_masks_or_an_automatic_background_reference()
    {
        var connection = new Connection(); var service = Service(connection);
        await service.RunAsync(new EditorCommandService(Scene()), Request(AiTaskKind.Harmonize), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "sourceImage" }, connection.Uploads);
        var graph = Assert.Single(connection.Graphs);
        Assert.Equal(QwenImageWorkflow.HarmonizePrompt, graph["conditioning"]!["inputs"]!["prompt"]!.GetValue<string>());
        Assert.Equal("align", graph["save"]!["inputs"]!["images"]![0]!.GetValue<string>());
        Assert.NotNull(graph["baseline"]);
        Assert.DoesNotContain("Lighting guidance", graph.ToJsonString());
    }

    [Fact]
    public async Task References_keep_UI_order_model_overrides_apply_and_saved_batch_becomes_sequential()
    {
        using var reference = Pixels.NewColor(64, 64); reference.Erase(SKColors.Blue);
        var connection = new Connection(); var service = Service(connection);
        var slot = service.Engines.ModelSlots(service.SelectedEngine!).Single(slot => slot.Kind == EngineAssetKind.Vae);
        service.ModelSelections = _ => new Dictionary<string, string> { [slot.Key] = "shared/qwen-vae.safetensors" };
        var request = Request(AiTaskKind.ImageEdit) with { ReferenceImages = [reference] };
        request = request with { Settings = request.Settings with { Variants = 2, VariantMode = AiVariantMode.Batch } };
        await service.RunAsync(new EditorCommandService(Scene()), request, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "sourceImage", "referenceImage1" }, connection.Uploads);
        Assert.Equal(2, connection.Graphs.Count);
        for (var index = 0; index < 2; index++)
        {
            var graph = connection.Graphs[index];
            Assert.Equal(73 + index, graph["noise"]!["inputs"]!["noise_seed"]!.GetValue<long>());
            Assert.Equal("shared/qwen-vae.safetensors", graph["vae"]!["inputs"]!["vae_name"]!.GetValue<string>());
            Assert.Equal("source", graph["conditioning"]!["inputs"]!["images.image_1"]![0]!.GetValue<string>());
            Assert.Equal("qwen_reference_1", graph["conditioning"]!["inputs"]!["images.image_2"]![0]!.GetValue<string>());
        }
    }

    [Fact]
    public void Odd_frame_padding_is_removed_without_rescaling_source_coordinates()
    {
        var session = Scene(); var request = Request(AiTaskKind.ImageEdit);
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var qwen = new QwenImageInputs(inputs, request);
        Assert.Equal((131, 97), qwen.ContentSize); Assert.Equal((160, 128), qwen.PaddedSize);
        using var returned = qwen.Finish(Pixels.Clone(qwen.Images["sourceImage"]));
        Assert.Equal(inputs.ContextImage.Bytes, returned.Bytes);
        Assert.Throws<InvalidDataException>(() => qwen.Finish(Pixels.NewColor(128, 128)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_nodes_fail_before_upload_and_failures_never_commit_partial_scene(bool missing)
    {
        var session = Scene(); var before = session.History.CurrentId;
        var connection = new Connection { MissingAlignment = missing, FailAt = missing ? 0 : 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(connection).RunAsync(new EditorCommandService(session), Request(AiTaskKind.ChangeBackground), TestContext.Current.CancellationToken));
        Assert.Equal(before, session.History.CurrentId);
        if (missing) Assert.Empty(connection.Uploads);
    }

    [Fact]
    public async Task Replacement_background_does_not_fit_a_homography_to_the_old_environment()
    {
        var connection=new Connection();
        await Service(connection).RunAsync(new EditorCommandService(Scene()),Request(AiTaskKind.ChangeBackground),TestContext.Current.CancellationToken);
        Assert.False(connection.Graphs[0].ContainsKey("align"));
        Assert.Equal("decode",connection.Graphs[0]["save"]!["inputs"]!["images"]![0]!.GetValue<string>());
        Assert.True(connection.Graphs[1].ContainsKey("align"));
        Assert.Contains("Its support is at",connection.Graphs[0]["conditioning"]!["inputs"]!["prompt"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_stale_documents_never_insert_Qwen_output(bool cancel)
    {
        var session=Scene();var state=session.History.CurrentId;
        var connection=new Connection {OnExecute=()=>
        {
            if(cancel) throw new OperationCanceledException();
            session.AddShape(new(ShapeKind.Rectangle,(uint)SKColors.Blue,0),new(0,0,10,10));
        }};
        var service=Service(connection);
        if(cancel) await Assert.ThrowsAsync<OperationCanceledException>(()=>service.RunAsync(new EditorCommandService(session),Request(AiTaskKind.Harmonize),TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RunAsync(new EditorCommandService(session),Request(AiTaskKind.Harmonize),TestContext.Current.CancellationToken));
        Assert.DoesNotContain(session.Document.AllLayers(),layer=>layer.Tags.Contains("scene-object"));
        if(cancel) Assert.Equal(state,session.History.CurrentId);
    }

    [Fact]
    public void Explicit_lighting_has_plain_projected_direction_and_default_harmonize_stays_short()
    {
        var prompt=QwenImageWorkflow.ScenePrompt(Request(AiTaskKind.Relight) with {Scene=new() {Preset=RelightPreset.GoldenHour}});
        Assert.Contains("shadow falls diagonally toward the lower-right",prompt);
        Assert.Equal(QwenImageWorkflow.HarmonizePrompt,QwenImageWorkflow.ScenePrompt(Request(AiTaskKind.Harmonize)));
    }

    [Fact]
    public void Background_cleanup_restores_a_separate_cast_at_the_original_PNG_support()
    {
        var session=Scene();var request=Request(AiTaskKind.ChangeBackground);
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var layout=Pixels.Clone(input.Before);
        var b=input.SupportBounds;
        using(var draw=new SKCanvas(layout))using(var paint=new SKPaint {Color=new(65,65,65)})
            draw.DrawRect(b.Left,b.Bottom,b.Width,12,paint);
        Pixels.Invalidate(layout);
        var source=input.ModelForeground.Bytes.ToArray();
        using var registered=new QwenBackgroundFrame(input,layout,input.ObjectAlpha);
        var result=registered.RestoreShadow(input,input.Background,request,TestContext.Current.CancellationToken);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            Assert.True(shadow.GetPixel(b.MidX,b.Bottom).Alpha>0);
            Assert.Equal(source,input.ModelForeground.Bytes);
            Assert.Equal(3,result.Layers.Count);
        }
        finally {foreach(var layer in result.Layers) {layer.Pixels.Dispose();layer.Mask?.Dispose();}}
    }

    [AvaloniaFact]
    public async Task Settings_and_advanced_show_Qwen_models_assignments_and_the_fixed_schedule()
    {
        var window = new MainWindow { Width = 1280, Height = 1000 }; window.Settings.CheckForUpdates = false; window.Show();
        try
        {
            var service = Service(new Connection()); await service.TestConnectionAsync(TestContext.Current.CancellationToken);
            var settings = new Settings {ComfyServerUrl="http://localhost:8188",AiTaskEngineIds=new() {{"Harmonize",QwenImageWorkflow.EngineId}}};
            var pending = AiDialogs.SettingsDialog(window, settings, service); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows);
            var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single(tabs => tabs.Name == "AiSettingsTabs"); tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains("Qwen Image 2.1 Turbo · local 8-step") == true);
            Assert.True(Screenshots.Save(dialog, "qwen21-settings"));
            var page=(ScrollViewer)((TabItem)tabs.Items.Cast<TabItem>().ElementAt(1)).Content!;
            page.Offset=new(0,1000);Dispatcher.UIThread.RunJobs();Assert.True(Screenshots.Save(dialog,"qwen21-models"));
            tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(), field => field.Text?.Contains("Qwen-Image-2.1") == true);
            Assert.True(Screenshots.Save(dialog, "prompt-enhancer-settings"));
            dialog.Close(false); Assert.False(await pending);
            var advanced = AiDialogs.Advanced(window, settings, service, AiTaskKind.Harmonize); Dispatcher.UIThread.RunJobs();
            dialog = Assert.Single(window.OwnedWindows);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "FLUX denoise");
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains("fixed 8-step") == true);
            Assert.True(Screenshots.Save(dialog, "qwen21-advanced")); dialog.Close(false); Assert.False(await advanced);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Enhancer_uses_selected_workflow_instructions_and_reads_text_history()
    {
        var connection = new Connection();
        var service = Service(connection);
        service.SessionApiKey = "test-only";
        var settings = new Settings { EnhancerEffort = "low", EnhancerMaxTokens = 512 };
        var engine = service.SelectedEngine!;
        settings.EnhancerInstructions[engine.Id] = "Keep exact lettering.";
        var text = await service.EnhanceAsync(settings, engine, AiTaskKind.GenerativeFill, "replace the label");
        Assert.Equal("Rewritten prompt", text);
        var graph = Assert.Single(connection.Graphs);
        Assert.Equal("gpt-6-luna", (string?)graph["chat"]!["inputs"]!["model"]);
        Assert.Equal("Keep exact lettering.", (string?)graph["config"]!["inputs"]!["instructions"]);
        Assert.Equal(512, (int?)graph["config"]!["inputs"]!["max_output_tokens"]);
        Assert.Equal("low", (string?)graph["config"]!["inputs"]!["reasoning_effort"]);
        Assert.Empty(connection.Uploads);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("kimi")]
    [InlineData("compatible")]
    [InlineData("openai")]
    public async Task Server_enhancement_runs_the_selected_adapter_with_separate_instructions(string adapter)
    {
        var connection = new Connection();
        var service = Service(connection);
        var settings = new Settings { EnhancerProvider = adapter, EnhancerServerModels = new() { [adapter] = "test-model" } };
        var engine = service.SelectedEngine!;
        Assert.Equal("Rewritten prompt", await service.EnhanceAsync(settings, engine, AiTaskKind.ImageEdit, "Change the label"));
        var graph = Assert.Single(connection.Graphs);
        Assert.Equal("AVSPromptHelper", (string?)graph["chat"]!["class_type"]);
        var state = JsonNode.Parse((string)graph["chat"]!["inputs"]!["state"]!)!;
        Assert.Equal(adapter, (string?)state["settings"]!["adapter"]);
        Assert.Equal("test-model", (string?)state["settings"]!["model"]);
        Assert.Contains("Qwen-Image-2.1", (string?)state["system_prompt"]);
        Assert.Contains("Change the label", (string?)state["prompt"]);
    }

    [Fact]
    public void User_credentials_roundtrip_replace_and_forget()
    {
        var target = UserApiKeys.Target("UnitTest", new Uri("https://unused.invalid/" + Guid.NewGuid().ToString("N")));
        try
        {
            Assert.Null(UserApiKeys.Read(target));
            UserApiKeys.Write(target, "test sample");
            Assert.Equal("test sample", UserApiKeys.Read(target));
            UserApiKeys.Write(target, "test replacement");
            Assert.Equal("test replacement", UserApiKeys.Read(target));
        }
        finally { UserApiKeys.Write(target, null); }
        Assert.Null(UserApiKeys.Read(target));
    }

    public static bool LiveCli => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMPOSA_TEST_CLI_SERVER"));
    [Fact(Skip = "Requires an explicitly selected local ComfyUI CLI account.", SkipUnless = nameof(LiveCli))]
    public async Task Server_Codex_CLI_enhances_a_text_only_sample()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_TEST_CLI_SERVER")!;
        var ids = await AiTaskService.DiscoverServerModels(url, "codex", "");
        var model = ids.First(id => id == "gpt-6.2-sol");
        var service = new AiTaskService(() => url, Engines) { ConnectionTimeoutSeconds = 30 };
        var settings = new Settings { EnhancerProvider = "codex", EnhancerServerModels = new() { ["codex"] = model } };
        var text = await service.EnhanceAsync(settings, service.Engines.Find(QwenImageWorkflow.EngineId)!, AiTaskKind.ImageEdit,
            "Change the blue circle to red. Keep the white background.");
        Assert.Contains("red", text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Connection : IComfyConnection
    {
        public ComfyServerAddress Address => ComfyServerAddress.Parse("http://localhost:8188");
        public TimeSpan ConnectionTimeout { get; set; }
        internal List<string> Uploads { get; } = [];
        internal List<JsonObject> Graphs { get; } = [];
        internal bool MissingAlignment; internal int FailAt;
        internal Action? OnExecute;
        private SKBitmap? source;
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken token = default)
        {
            var catalog = new EngineCatalog(Engines); var engine = catalog.Find(QwenImageWorkflow.EngineId)!;
            var nodes = engine.RequiredNodeTypes.ToHashSet(); if (MissingAlignment) nodes.Remove("PixelDriftFix");
            nodes.UnionWith(["AVSPromptHelper", "OpenAIChatNode", "OpenAIChatConfig", "PreviewAny"]);
            var choices=catalog.ModelSlots(engine).ToDictionary(slot=>slot.LoaderKey,slot=>new HashSet<string> {slot.Default,"shared/qwen-vae.safetensors"});
            return Task.FromResult((new ComfyServerInfo("0.43.0", "test", "test", ["NVIDIA test"]), new ComfyServerCapabilities { NodeTypes = nodes,ModelChoices=choices, NodeDefinitions = new() { ["OpenAIChatNode"] = JsonNode.Parse("""{"input":{"required":{"model":["COMBO",{"options":["gpt-6-luna"]}]}}}""")!.AsObject() } }));
        }
        public Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken token = default)
        {
            Uploads.Add(semantic); if (semantic == "sourceImage") { source?.Dispose(); source = Pixels.Clone(image); }
            return Task.FromResult(semantic + ".png");
        }
        public Task<ComfyExecutionResult> ExecuteAsync(JsonObject graph, IProgress<AiOperationState>? progress = null, CancellationToken token = default)
        {
            Graphs.Add((JsonObject)graph.DeepClone());OnExecute?.Invoke(); if (Graphs.Count == FailAt) throw new InvalidOperationException("test failure");
            if (graph.ContainsKey("enhanced_prompt"))
                return Task.FromResult(new ComfyExecutionResult("test", JsonDocument.Parse("""{"test":{"outputs":{"enhanced_prompt":{"text":["Rewritten prompt"]}}}}"""), []));
            var outputs = new List<ComfyImageReference> { new("result.png", "", "output", "save") };
            if (graph.ContainsKey("baseline")) outputs.Add(new("baseline.png", "", "output", "baseline"));
            return Task.FromResult(new ComfyExecutionResult("test", JsonDocument.Parse("{}"), outputs));
        }
        public Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken token = default)
        {
            if (source != null) return Task.FromResult(Pixels.Clone(source));
            var graph = Graphs.Last(); var bitmap = Pixels.NewColor(graph["latent"]!["inputs"]!["width"]!.GetValue<int>(), graph["latent"]!["inputs"]!["height"]!.GetValue<int>());
            bitmap.Erase(SKColors.Gray); return Task.FromResult(bitmap);
        }
        public void Dispose() { source?.Dispose(); source = null; }
    }
}
