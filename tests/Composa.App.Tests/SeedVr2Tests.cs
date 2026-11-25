using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class SeedVr2Tests
{
    private const string SmallModel = "seedvr2_3b_int8_convrot.safetensors";
    private const string LargeModel = "seedvr2_7b_int8_convrot.safetensors";
    public static bool Live => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_SEED_SWITCH_TEST_URL"));

    [Fact(Skip = "Requires a ComfyUI server with SeedVR2 3B and 7B.", SkipUnless = nameof(Live))]
    public async Task Both_SeedVR2_sizes_complete_an_upscale_on_the_selected_server()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_SEED_SWITCH_TEST_URL")!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        await service.TestConnectionAsync(deadline.Token);
        var engine = service.Engines.Find("seedvr2-native")!;
        var slot = service.Engines.ModelSlots(engine).Single(slot => slot.Kind == EngineAssetKind.DiffusionModel);
        var settings = new Settings();
        service.ModelSelections = settings.ComfyModelsFor;
        foreach (var model in new[] { SmallModel, LargeModel })
        {
            settings.SetComfyModels(url, new Dictionary<string, string> { [slot.Key] = model });
            var check = service.OperationCompatibility(engine, AiTaskKind.Upscale, settings.ComfyModelsFor(url), new());
            Assert.True(check.IsCompatible, string.Join(", ", check.Missing));
            var session = EditorSession.NewCanvas(96, 96, SKColors.Bisque);
            session.AddShape(new(Composa.Model.ShapeKind.Rectangle, (uint)SKColors.Blue, 0), new SKRect(24, 20, 72, 76));
            await service.RunAsync(new EditorCommandService(session), new() { Task = AiTaskKind.Upscale, EngineId = engine.Id,
                Settings = new() { UpscaleFactor = 2, Seed = 17 } }, deadline.Token);
            Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
            Assert.Equal((192, 192), (session.Document.Width, session.Document.Height));
        }
    }

    [AvaloniaFact]
    public async Task One_Model_files_profile_switches_both_SeedVR2_sizes_in_the_submitted_workflow()
    {
        using var connection = new Connection { ModelFiles = [SmallModel, LargeModel] };
        var service = Service(connection);
        var token = TestContext.Current.CancellationToken;
        var settings = new Settings { ComfyServerUrl = connection.Address.ToString() };
        service.ModelSelections = settings.ComfyModelsFor;
        await service.TestConnectionAsync(token);
        var owner = new Window { Width = 1200, Height = 850 };
        owner.Show();
        try
        {
            foreach (var file in new[] { SmallModel, LargeModel })
            {
                var pending = AiDialogs.SettingsDialog(owner, settings, service, page: 1);
                if (pending.IsFaulted) await pending;
                var dialog = Assert.Single(owner.OwnedWindows);
                Dispatcher.UIThread.RunJobs();
                var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(button =>
                    button.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text?.StartsWith("SeedVR2", StringComparison.Ordinal) == true));
                var profile = Assert.Single(buttons);
                Assert.Contains(profile.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "SeedVR2");
                profile.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                var picker = Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),
                    field => AutomationProperties.GetName(field) == Locale.T("Main model"));
                Assert.True(picker.IsEnabled);
                picker.SelectedItem = picker.Items.Last(item => item?.ToString() == file);
                Assert.True(Screenshots.Save(dialog, "studio-settings-seedvr2-profile"));
                dialog.Close(true);
                Assert.True(await pending);

                var session = EditorSession.NewCanvas(96, 96, SKColors.Coral);
                await service.RunAsync(new EditorCommandService(session), Request("seedvr2-native"), token);
                Assert.Equal(file, connection.SubmittedModel);
                Assert.Equal((384, 384), (session.Document.Width, session.Document.Height));
            }
        }
        finally { owner.Close(); }
    }

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

    [Fact]
    public async Task Upscale_after_an_edit_keeps_the_full_resolution_in_a_layer_without_resizing_the_canvas()
    {
        var session = EditorSession.NewCanvas(185, 143, SKColors.Coral);
        using var connection = new Connection { OnExecute = () => session.AddBlankLayer() };
        await Service(connection).RunAsync(new EditorCommandService(session), Request("seedvr2-native"),
            TestContext.Current.CancellationToken);

        Assert.Equal((185, 143), (session.Document.Width, session.Document.Height));
        var result = session.Document.Layers[^1];
        Assert.Equal((740, 572), (result.Pixels!.Width, result.Pixels.Height));
        Assert.Equal(new SKRect(0, 0, 185, 143), result.Bounds);
        session.Undo();
        Assert.Equal(2, session.Document.Layers.Count);
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
        internal HashSet<string>? ModelFiles { get; init; }
        internal Action? OnExecute { get; init; }
        internal string? SubmittedModel { get; private set; }
        private SKBitmap? source;
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
            var engine = catalog.Find("seedvr2-native")!;
            var capabilities = new ComfyServerCapabilities { NodeTypes = engine.RequiredNodeTypes.ToHashSet() };
            if (ModelFiles != null)
            {
                capabilities.ModelChoices["UNETLoader.unet_name"] = ModelFiles;
                var vae = catalog.ModelSlots(engine).Single(slot => slot.Kind == EngineAssetKind.Vae);
                capabilities.ModelChoices[vae.LoaderKey] = [vae.Default];
            }
            return Task.FromResult((new ComfyServerInfo("0.37.0", "Windows", "3.12", ["GPU"]) { Memory = [new("GPU", "cuda", 0, 8L << 30, 6L << 30)] }, capabilities));
        }
        public Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken cancellationToken = default)
        {
            Assert.Equal("sourceImage", semantic); source?.Dispose(); source = Pixels.Clone(image);
            Uploads.Add(new(image.Width, image.Height)); return Task.FromResult("source.png");
        }
        public Task<ComfyExecutionResult> ExecuteAsync(JsonObject graph, IProgress<AiOperationState>? progress = null, CancellationToken cancellationToken = default)
        {
            Executions++;
            OnExecute?.Invoke();
            SubmittedModel = graph["model"]!["inputs"]!["unet_name"]!.GetValue<string>();
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
