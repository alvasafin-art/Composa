using Avalonia;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
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

    [Fact]
    public void Masked_edit_keeps_the_subject_visible_to_the_image_encoder()
    {
        var session = Scene();
        session.SelectRect(new SKRect(50, 30, 80, 65));
        var request = Request(AiTaskKind.GenerativeFill) with { Prompt = "Recolor the object blue" };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var edit = new QwenImageInputs(inputs, request);
        Assert.Equal(edit.Images["latentSource"].Bytes, edit.Images["sourceImage"].Bytes);
        Assert.NotNull(edit.Mask);
        var engine = new EngineCatalog(Engines);
        var pack = engine.Find(QwenImageWorkflow.EngineId)!;
        var graph = edit.Bind(engine.ReadWorkflow(pack, pack.Workflow("edit")), edit.Images.Keys.ToDictionary(k => k, k => k), 1);
        var instruction = graph["conditioning"]!["inputs"]!["prompt"]!.GetValue<string>();
        Assert.Contains(request.Prompt, instruction);
        Assert.DoesNotContain("black marked", instruction);
        var decoded = Pixels.NewColor(edit.PaddedSize.Width, edit.PaddedSize.Height);
        decoded.Erase(SKColors.Blue);
        var raw = edit.Finish(decoded);
        AiTaskService.Insert(new EditorCommandService(session), request.Task, AiOutputMode.NewLayerWithMask,
            [raw], inputs.TargetBounds, inputs, localOutputMask: edit.Mask);
        using var masked = session.Flatten();
        Assert.Equal(SKColors.Gray, masked.GetPixel(35, 40));
        session.DeleteMask(session.ActiveLayer!);
        using var visible = session.Flatten();
        Assert.Equal(SKColors.Blue, visible.GetPixel(35, 40));
    }

    public static bool LiveRecolor => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_HOOD_TEST_URL"));

    [Fact(Skip = "Requires a local Qwen server", SkipUnless = nameof(LiveRecolor))]
    public async Task Recolor_range_hood_with_a_real_masked_request()
    {
        var session = EditorSession.NewCanvas(512, 384, SKColors.White);
        var source = Pixels.NewColor(512, 384);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(new SKColor(226, 222, 215));
            paint.Color = new SKColor(150, 154, 156);
            canvas.DrawRect(219, 0, 74, 114, paint);
            using var profile = new SKPath();
            profile.MoveTo(208, 108); profile.LineTo(302, 108); profile.LineTo(391, 186); profile.LineTo(121, 186); profile.Close();
            canvas.DrawPath(profile, paint);
            paint.Color = new SKColor(75, 78, 81);
            canvas.DrawRoundRect(new SKRect(119, 182, 393, 203), 3, 3, paint);
            paint.Color = SKColors.White;
            for (int i = 0; i < 4; i++) canvas.DrawCircle(294 + i * 10, 193, 2, paint);
        }
        Pixels.Invalidate(source);
        session.AddImageLayer("Kitchen hood", source, fit: false);
        session.SelectRect(new(115, 105, 397, 206));
        var url = Environment.GetEnvironmentVariable("COMPOSA_HOOD_TEST_URL")!;
        var service = new AiTaskService(() => url, Engines);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var before = session.Flatten();
        var job = Request(AiTaskKind.GenerativeFill) with { EngineId = QwenImageWorkflow.EngineId,
            Prompt = "Recolor the existing silver cooker hood canopy blue. Keep its shape, front lip and control buttons." };
        await service.RunAsync(new EditorCommandService(session), job with { Settings = job.Settings with { Width = 768, Height = 512 } }, deadline.Token);
        using var after = session.Flatten();
        var folder = Environment.GetEnvironmentVariable("COMPOSA_HOOD_TEST_OUTPUT")!;
        Directory.CreateDirectory(folder);
        Composa.IO.ImageFiles.Save(before, Path.Combine(folder, "before.png"), Composa.IO.ExportFormat.Png);
        Composa.IO.ImageFiles.Save(after, Path.Combine(folder, "after.png"), Composa.IO.ExportFormat.Png);
        var center = after.GetPixel(255, 153);
        Assert.True(center.Blue > center.Red + 15);
        Assert.Equal(before.GetPixel(70, 280), after.GetPixel(70, 280));
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

    [Theory]
    [InlineData(AiTaskKind.ChangeBackground)]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.MatchToScene)]
    [InlineData(AiTaskKind.Relight)]
    public async Task Flux3_scene_operations_bind_source_and_VAE_baseline_before_applying_one_edit(AiTaskKind task)
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new Connection { EngineId = Flux3Workflow.EngineId };
        var service = Service(connection);
        var engine = service.Engines.Find(Flux3Workflow.EngineId)!;
        await service.TestConnectionAsync(token);
        var readiness = service.OperationCompatibility(engine, task, new Dictionary<string, string>(), new());
        Assert.True(readiness.IsCompatible, string.Join(", ", readiness.Missing));
        var session = Scene();
        var before = session.History.CurrentId;
        var history = session.History.Count;
        await service.RunAsync(new EditorCommandService(session), Request(task) with { EngineId = engine.Id }, token);
        Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
        Assert.Equal(task == AiTaskKind.ChangeBackground ? 2 : 1, connection.Graphs.Count);
        Assert.All(connection.Uploads, semantic => Assert.Equal("sourceImage", semantic));
        Assert.All(connection.Graphs, graph =>
        {
            Assert.Equal("ReferenceMultiLatent", graph["references"]!["class_type"]!.GetValue<string>());
            Assert.Equal("reference0", graph["encodeBaseline"]!["inputs"]!["pixels"]![0]!.GetValue<string>());
            Assert.Equal("decodeBaseline", graph["baseline"]!["inputs"]!["images"]![0]!.GetValue<string>());
            Assert.Equal(160, graph["latent"]!["inputs"]!["width"]!.GetValue<int>());
            Assert.Equal(128, graph["latent"]!["inputs"]!["height"]!.GetValue<int>());
        });
        Assert.Equal(history + 1, session.History.Count);
        session.Undo();
        Assert.Equal(before, session.History.CurrentId);
    }

    public static bool LiveFlux3Scenes => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_FLUX3_SCENE_TEST_URL"));

    [Fact(Skip = "Requires a ComfyUI server with FLUX 3 Image Klein.", SkipUnless = nameof(LiveFlux3Scenes))]
    public async Task Flux3_scene_operations_complete_on_the_selected_server()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_FLUX3_SCENE_TEST_URL")!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var service = new AiTaskService(() => url, Engines);
        await service.TestConnectionAsync(deadline.Token);
        var engine = service.Engines.Find(Flux3Workflow.EngineId)!;
        foreach (var task in new[] { AiTaskKind.Harmonize, AiTaskKind.Relight, AiTaskKind.MatchToScene, AiTaskKind.ChangeBackground })
        {
            var check = service.OperationCompatibility(engine, task, new Dictionary<string, string>(), new());
            Assert.True(check.IsCompatible, string.Join(", ", check.Missing));
            var session = Scene();
            var before = session.History.CurrentId;
            var request = Request(task) with { EngineId = engine.Id,
                Scene = new SceneEditSettings { MatchMode = SceneMatchMode.LightAndShadows },
                Prompt = task == AiTaskKind.ChangeBackground ? "A light wooden table in a bright workshop." : "Soft lighting from the left." };
            await service.RunAsync(new EditorCommandService(session), request, deadline.Token);
            Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
            session.Undo();
            Assert.Equal(before, session.History.CurrentId);
        }
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
    public async Task Qwen_inserts_a_separate_result_after_edits_and_honors_cancellation(bool cancel)
    {
        var session = Scene();
        var source = session.ActiveLayer!;
        var state = session.History.CurrentId;
        var connection = new Connection { OnExecute = () =>
        {
            if (cancel) throw new OperationCanceledException();
            session.AddShape(new(ShapeKind.Rectangle, (uint)SKColors.Blue, 0), new(0, 0, 10, 10));
            state = session.History.CurrentId;
        } };
        var run = Service(connection).RunAsync(new EditorCommandService(session), Request(AiTaskKind.Harmonize),
            TestContext.Current.CancellationToken);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        else
        {
            await run;
            Assert.True(source.Visible);
            Assert.Contains(session.Document.Layers[^1].Children, layer => layer.Tags.Contains("scene-object"));
            session.Undo();
        }
        Assert.DoesNotContain(session.Document.AllLayers(), layer => layer.Tags.Contains("scene-object"));
        Assert.Equal(state, session.History.CurrentId);
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
            var tabs = dialog.GetVisualDescendants().OfType<StudioSettingsPages>().Single(); tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Qwen Image 2.1 Turbo");
            Assert.True(Screenshots.Save(dialog, "qwen21-settings"));
            var page = Assert.IsType<ScrollViewer>(tabs.Content);
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

    [Fact]
    public async Task Enhancer_sends_distinct_guidance_for_each_bundled_model_and_operation()
    {
        var connection = new Connection();
        var service = Service(connection);
        service.SessionApiKey = "test-only";
        var settings = new Settings();
        var guidance = new HashSet<string>();
        var engines = service.Engines.Profiles.Where(engine =>
            engine.Binding(AiTaskKind.GenerateImage) != null && engine.Binding(AiTaskKind.ImageEdit) != null);

        foreach (var engine in engines)
        foreach (var task in new[] { AiTaskKind.GenerateImage, AiTaskKind.ImageEdit })
        {
            const string draft = "Use the cup with the exact lettering \"Night Ride\".";
            var file = task == AiTaskKind.GenerateImage ? "enhancer-generate.txt" : "enhancer.txt";
            var expected = File.ReadAllText(Path.Combine(Engines, engine.Id, file)).Trim();
            var result = await service.EnhanceAsync(settings, engine, task, draft, referenceCount: 1);
            Assert.Equal("Rewritten prompt", result);
            var graph = connection.Graphs.Last();
            Assert.Equal(expected, (string?)graph["config"]!["inputs"]!["instructions"]);
            Assert.True(guidance.Add(expected), $"Duplicate instructions for {engine.Id} / {task}.");
            var message = (string?)graph["chat"]!["inputs"]!["prompt"];
            Assert.Contains("Added reference images: 1", message);
            Assert.Contains(draft, message);
        }

        Assert.NotEmpty(guidance);
        Assert.Empty(connection.Uploads);
    }

    [Theory]
    [InlineData(ImageGenerationRoute.LocalCodex, "", "direct-codex-image")]
    [InlineData(ImageGenerationRoute.RemoteCodex, "", "direct-codex-image")]
    [InlineData(ImageGenerationRoute.DirectApi, "openai/gpt-image-2", "direct-gpt-image-2")]
    [InlineData(ImageGenerationRoute.DirectApi, "openai/gpt-image-2.5-sunburst-2026-09-08", "chatgpt-image-2.5")]
    [InlineData(ImageGenerationRoute.DirectApi, "gpt-image-2.6-sunburst", "chatgpt-image-2.6")]
    public void Enhancer_resolves_guidance_for_direct_image_routes(ImageGenerationRoute route, string model, string folder)
    {
        var service = Service(new Connection());
        var engine = new DirectImageGenerator(route, "", "", model).PromptProfile();
        foreach (var task in new[] { AiTaskKind.GenerateImage, AiTaskKind.ImageEdit })
        {
            var file = task == AiTaskKind.GenerateImage ? "enhancer-generate.txt" : "enhancer.txt";
            var expected = File.ReadAllText(Path.Combine(Engines, folder, file)).Trim();
            var overrides = new Dictionary<string, string>();
            Assert.Equal(expected, service.InstructionsFor(overrides, engine, task));
            overrides[AiTaskService.InstructionKey(AiTaskService.InstructionScope(engine), task)] = "My override";
            Assert.Equal("My override", service.InstructionsFor(overrides, engine, task));
        }
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
        var model = ids.First(model => model.Id == "gpt-6.2-sol").Id;
        var service = new AiTaskService(() => url, Engines) { ConnectionTimeoutSeconds = 30 };
        var settings = new Settings { EnhancerProvider = "codex", EnhancerServerModels = new() { ["codex"] = model } };
        var text = await service.EnhanceAsync(settings, service.Engines.Find(QwenImageWorkflow.EngineId)!, AiTaskKind.ImageEdit,
            "Change the blue circle to red. Keep the white background.");
        Assert.Contains("red", text, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Model_files_search_selects_Qwen_and_shows_its_loader_fields()
    {
        var connection = new Connection { MissingAlignment = true };
        var settings = new Settings { ComfyServerUrl = connection.Address.ToString() };
        var service = new AiTaskService(() => settings.ComfyServerUrl, Engines, _ => connection);
        service.SelectedEngine = service.Engines.Find("flux2-klein-intel-xpu");
        await service.TestConnectionAsync(TestContext.Current.CancellationToken);
        var owner = new Window { Width = 1100, Height = 800 }; owner.Show();
        try
        {
            var pending = AiDialogs.SettingsDialog(owner, settings, service);
            var dialog = Assert.Single(owner.OwnedWindows);
            var search = dialog.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "SettingsSearch");
            search.Text = "qwen"; Dispatcher.UIThread.RunJobs();
            var matches = dialog.GetVisualDescendants().OfType<ListBox>().Single(b => b.Name == "SettingsMatches");
            Assert.Single(matches.Items);
            search.Focus();
            dialog.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var pages = Assert.Single(dialog.GetVisualDescendants().OfType<StudioSettingsPages>());
            Assert.Equal(1, pages.SelectedIndex);
            Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>(), field =>
                Avalonia.Automation.AutomationProperties.GetName(field) == Locale.T("Main model"));
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "edit.json");
            Assert.True(Screenshots.Save(dialog, "studio2-model-readiness"));
            dialog.Close(false); Assert.False(await pending);
            Assert.Equal("flux2-klein-intel-xpu", service.SelectedEngine.Id);
            Assert.Empty(connection.Graphs); Assert.Empty(connection.Uploads);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Settings_navigation_keeps_the_operation_and_checks_the_helper_without_a_prompt()
    {
        var previousLanguage = Locale.Language;
        Locale.Language = "ru";
        var settings = new Settings { EnhancerProvider = "codex", ComfyServerUrl = "http://localhost:8188",
            EnhancerServerModels = new() { ["codex"] = "test-model" } };
        var connection = new Connection { MissingHelper = true };
        var service = new AiTaskService(() => settings.ComfyServerUrl, Engines, _ => connection);
        var owner = new Window { Width = 1280, Height = 720 };
        owner.Show();
        try
        {
            var pending = AiDialogs.SettingsDialog(owner, settings, service, page: 6);
            if (pending.IsFaulted) await pending;
            var dialog = Assert.Single(owner.OwnedWindows);
            dialog.Width = 1280;
            dialog.Height = 720;
            var pages = Assert.Single(dialog.GetVisualDescendants().OfType<StudioSettingsPages>());
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, Locale.T("Run parameters ›")));
            var labels = dialog.GetVisualDescendants().OfType<TextBlock>().Where(label => label.IsEffectivelyVisible).ToArray();
            Assert.Single(labels, label => label.Text == Locale.T("Language model"));
            Assert.Single(labels, label => label.Text == Locale.T("Generate Image"));
            Assert.Single(labels, label => label.Text == Locale.T("Image Edit"));
            Assert.DoesNotContain(labels, label => label.Text == Locale.T("Overview"));
            Assert.True(Screenshots.Save(dialog, "studio-settings-01-operations-ru"));
            ClickText("Image Edit");
            Assert.Equal(6, pages.SelectedIndex);
            Assert.True(Screenshots.Save(dialog, "studio-settings-01-operations-expanded-ru"));
            Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>(), field => field.Name == "DefaultModelImageEdit");
            ClickText("Model files");
            Assert.Equal(1, pages.SelectedIndex);
            Assert.True(Screenshots.Save(dialog, "studio-settings-08-models-ru"));
            ClickText("FLUX 3 Image Klein");
            var sceneRows = new[] { AiTaskKind.ChangeBackground, AiTaskKind.Harmonize, AiTaskKind.MatchToScene, AiTaskKind.Relight };
            foreach (var task in sceneRows)
            {
                var operation = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(),
                    label => label.Text == Locale.T(task.DisplayName()));
                Assert.Single(operation.GetVisualAncestors().OfType<ScrollViewer>());
            }
            Assert.True(Screenshots.Save(dialog, "studio-settings-flux3-operations-ru"));
            ClickText("Connections");
            Assert.Equal(0, pages.SelectedIndex);
            Assert.True(Screenshots.Save(dialog, "studio-settings-06-connections-ru"));
            ClickText("Operations");
            Assert.Equal(6, pages.SelectedIndex);
            ClickText("Language model");
            Assert.Equal(2, pages.SelectedIndex);
            Assert.True(Screenshots.Save(dialog, "studio-settings-05-prompt-ru"));
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, Locale.T("Test prompt")));
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text ==
                Locale.T("Instructions apply to the selected image model and operation."));
            var instructionEditors = dialog.GetVisualDescendants().OfType<TextBox>()
                .Where(box => box.Name is "EnhancerInstructions" or "EnhancerEditInstructions").ToArray();
            Assert.Equal(2, instructionEditors.Length);
            Assert.All(instructionEditors, box => Assert.Equal(360, box.Bounds.Height, precision: 2));
            Assert.Equal(instructionEditors[0].TranslatePoint(default, dialog)!.Value.Y,
                instructionEditors[1].TranslatePoint(default, dialog)!.Value.Y, precision: 2);
            Click("Refresh models");
            var warning = dialog.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "AvsPromptHelperWarning" && border.IsVisible);
            Assert.True(warning.IsVisible);
            Assert.Empty(connection.Graphs);
            var scroll = Assert.IsType<ScrollViewer>(pages.Content);
            scroll.Offset = new(0, 5000);
            Assert.True(Screenshots.Save(dialog, "studio-settings-avs-warning-ru"));
            dialog.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(panel =>
                Avalonia.Automation.AutomationProperties.GetName(panel) == Locale.T("Connection details")).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var address = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EnhancerComfyServer");
            address.Text = "http://localhost:8189";
            connection.Offline = true;
            Dispatcher.UIThread.RunJobs();
            Click("Refresh models");
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == Locale.T("The ComfyUI server is unavailable."));
            Assert.Empty(connection.Graphs);
            ClickText("Back");
            Assert.Equal(6, pages.SelectedIndex);
            pages.SelectedIndex = 7;
            Assert.True(Screenshots.Save(dialog, "studio-settings-07-performance-ru"));
            dialog.Width = 1024;
            dialog.Height = 576;
            Locale.Apply("en", owner, dialog);
            pages.SelectedIndex = 6;
            Assert.True(Screenshots.Save(dialog, "studio-settings-operations-1024x576-en"));
            Assert.All(((StudioSettingsWindow)dialog).FooterButtons.Children.OfType<Button>(), button =>
                Assert.True(button.TranslatePoint(new(button.Bounds.Width, button.Bounds.Height), dialog) is { } end &&
                    end.X <= dialog.Bounds.Width && end.Y <= dialog.Bounds.Height));
            var save = ((StudioSettingsWindow)dialog).FooterButtons.Children.OfType<Button>().Last();
            var saveLabel = Assert.Single(save.GetVisualDescendants().OfType<TextBlock>());
            var labelCenter = saveLabel.TranslatePoint(new(saveLabel.Bounds.Width / 2, saveLabel.Bounds.Height / 2), save)!.Value;
            Assert.InRange(labelCenter.X, save.Bounds.Width / 2 - 1, save.Bounds.Width / 2 + 1);
            Assert.InRange(labelCenter.Y, save.Bounds.Height / 2 - 1, save.Bounds.Height / 2 + 1);
            dialog.Close(false);
            Assert.False(await pending);
            Assert.Equal("http://localhost:8188", settings.ComfyServerUrl);

            void Click(string title)
            {
                dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible && Equals(button.Content, Locale.T(title)))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
            void ClickText(string title)
            {
                dialog.GetVisualDescendants().OfType<Button>().Last(button =>
                    button.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == Locale.T(title)))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally { Locale.Language = previousLanguage; owner.Close(); }
    }

    [AvaloniaFact]
    public async Task First_launch_connection_check_loads_models_without_sending_a_prompt()
    {
        var settings = new Settings { EnhancerProvider = "comfy", ComfyServerUrl = "http://localhost:8188" };
        var connection = new Connection();
        var service = new AiTaskService(() => settings.ComfyServerUrl, Engines, _ => connection);
        var owner = new Window { Width = 1280, Height = 720 };
        owner.Show();
        try
        {
            var pending = AiDialogs.SettingsDialog(owner, settings, service, firstRun: true);
            if (pending.IsFaulted) await pending;
            var window = Assert.Single(owner.OwnedWindows);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, Locale.T("Test prompt")));
            window.GetVisualDescendants().OfType<Button>().Single(button =>
                    button.IsEffectivelyVisible && Equals(button.Content, Locale.T("Get available models")))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), picker => picker.Items.Contains("gpt-6-luna"));
            Assert.Empty(connection.Graphs);
            var textAddress = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EnhancerComfyServer");
            var imageAddress = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ImageComfyServer");
            Assert.Equal(textAddress.TranslatePoint(default, window)!.Value.X, imageAddress.TranslatePoint(default, window)!.Value.X, precision: 2);
            foreach (var address in new[] { textAddress, imageAddress })
            {
                var label = Assert.IsType<TextBlock>(Assert.IsType<StackPanel>(address.Parent).Children[0]);
                Assert.True(label.TranslatePoint(default, window)!.Value.Y < address.TranslatePoint(default, window)!.Value.Y);
            }
            imageAddress.Text = "http://localhost:8190";
            Assert.Equal(imageAddress.Text, textAddress.Text);
            window.Close(false);
            Assert.False(await pending);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task First_launch_connections_align_the_cli_fields_and_the_image_api_catalog()
    {
        var previousLanguage = Locale.Language;
        Locale.Language = "ru";
        var address = "https://api.openai.com/v1";
        var settings = new Settings
        {
            EnhancerProvider = "local-codex", EnhancerCodexModel = "gpt-5.3-codex", EnhancerEffort = "high",
            ImageGenerationRoute = ImageGenerationRoute.DirectApi, ImageGenerationApiUrl = address, ImageApiDisplayName = "OpenAI Images",
            ImageApiCatalogs = new()
            {
                [DirectImageApi.Parse(address).CredentialEndpoint.AbsoluteUri] = new()
                {
                    ["openai/gpt-image-2"] = "GPT Image 2",
                    ["openai/gpt-image-2.5-sunburst"] = "GPT Image 2.5"
                }
            }
        };
        var owner = new Window { Width = 1280, Height = 760 };
        owner.Show();
        try
        {
            var pending = AiDialogs.SettingsDialog(owner, settings, Service(new Connection()), firstRun: true, firstRunBack: () => { });
            if (pending.IsFaulted) await pending;
            var dialog = Assert.Single(owner.OwnedWindows);
            dialog.Width = 1158;
            dialog.Height = 760;
            Dispatcher.UIThread.RunJobs();
            var controls = dialog.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible).ToArray();
            var left = controls.OfType<ComboBox>().Single(box => box.Name == "TextEnhancerRoute");
            var right = controls.OfType<ComboBox>().Single(box => Equals(box.SelectedItem, "Codex CLI"));
            var api = controls.OfType<TextBox>().Single(box => box.Name == "ImageGenerationApiUrl");
            var name = controls.OfType<TextBox>().Single(box => box.Text == "OpenAI Images");
            var modelId = controls.OfType<TextBox>().Single(box => box.Text == "openai/gpt-image-2");
            var modelName = controls.OfType<TextBox>().Single(box => box.Text == "GPT Image 2");
            foreach (var field in new Control[] { left, right, api, name, modelId, modelName })
                Assert.Equal(34, field.Bounds.Height, precision: 2);
            Assert.Equal(X(left), X(api), precision: 2);
            Assert.Equal(X(left), X(modelId), precision: 2);
            Assert.Equal(X(right), X(name), precision: 2);
            Assert.Equal(left.Bounds.Width, right.Bounds.Width, precision: 2);
            Assert.Equal(14, X(right) - X(left) - left.Bounds.Width, precision: 2);
            Assert.Equal(8, X(modelName) - X(modelId) - modelId.Bounds.Width, precision: 2);
            Assert.Equal(2, controls.OfType<Button>().Count(button => Equals(button.Content, Locale.T("Get available models"))));
            Assert.True(Screenshots.Save(dialog, "studio-settings-03-reference-ru"));
            dialog.Close(false);
            Assert.False(await pending);

            double X(Control control) => control.TranslatePoint(default, dialog)!.Value.X;
        }
        finally { Locale.Language = previousLanguage; owner.Close(); }
    }

    private sealed class Connection : IComfyConnection
    {
        public ComfyServerAddress Address => ComfyServerAddress.Parse("http://localhost:8188");
        public TimeSpan ConnectionTimeout { get; set; }
        internal List<string> Uploads { get; } = [];
        internal List<JsonObject> Graphs { get; } = [];
        internal bool MissingAlignment, MissingHelper, Offline; internal int FailAt;
        internal Action? OnExecute;
        internal string EngineId { get; init; } = QwenImageWorkflow.EngineId;
        private SKBitmap? source;
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken token = default)
        {
            if (Offline) throw new HttpRequestException("connection refused");
            var catalog = new EngineCatalog(Engines); var engine = catalog.Find(EngineId)!;
            var nodes = engine.RequiredNodeTypes.ToHashSet(); if (MissingAlignment) nodes.Remove("PixelDriftFix");
            nodes.UnionWith(["AVSPromptHelper", "OpenAIChatNode", "OpenAIChatConfig", "PreviewAny"]);
            if (MissingHelper) nodes.Remove("AVSPromptHelper");
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
