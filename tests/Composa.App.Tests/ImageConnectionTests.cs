using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class ImageConnectionTests
{
    [Fact(Skip = "Requires the signed-in local Codex CLI.", SkipUnless = nameof(CheckLocalCatalog))]
    public async Task Local_codex_lists_models_and_supported_efforts_without_a_generation_request()
    {
        var models = await LocalCodex.DiscoverModelsAsync("", TestContext.Current.CancellationToken);
        Assert.NotEmpty(models);
        Assert.Contains(models, model => model.Efforts.Length > 0);
    }

    public static bool CheckLocalCatalog => Environment.GetEnvironmentVariable("COMPOSA_CHECK_CODEX_CATALOG") == "1";

    [AvaloniaFact]
    public async Task Connection_page_updates_both_api_status_lights_with_catalog_gets_only()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var root = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}";
        var settings = new Settings { EnhancerProvider = "direct", EnhancerApiUrl = root + "/text/v1", EnhancerApiModel = "text-example",
            ImageGenerationApiUrl = root + "/image/v1" };
        var owner = new Window { Width = 1280, Height = 850 };
        owner.Show();
        try
        {
            var pending = AiDialogs.SettingsDialog(owner, settings,
                new AiTaskService(() => "http://127.0.0.1:1", Path.Combine(AppContext.BaseDirectory, "ai", "engines")), page: 5);
            if (pending.IsFaulted) await pending;
            var dialog = Assert.Single(owner.OwnedWindows);
            var requests = new[] { await Reply(), await Reply() };
            Assert.Contains("GET /text/v1/models HTTP/1.1", requests);
            Assert.Contains("GET /image/v1/models HTTP/1.1", requests);
            await WaitFor(() => Lights().Count(light => ReferenceEquals(light.Background, Palette.SettingsSuccessText)) >= 3);
            var pages = Assert.Single(dialog.GetVisualDescendants().OfType<StudioSettingsPages>());
            pages.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>(), field => field.IsEnabled && field.Items.Contains("high"));
            dialog.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(button =>
                Avalonia.Automation.AutomationProperties.GetName(button) == Locale.T("Connection details")).IsChecked = true;
            Assert.True(Screenshots.Save(dialog, "studio-settings-followup-prompt-details"));
            pages.SelectedIndex = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible &&
                (Equals(button.Content, Locale.T("Check connection")) || Equals(button.Content, Locale.T("Add"))));
            Assert.True(Screenshots.Save(dialog, "studio-settings-followup-api"));
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ImageApiKey").Text = "rejected";
            Assert.Equal("GET /image/v1/models HTTP/1.1", await Reply("401 Unauthorized"));
            await WaitFor(() => Lights().Count(light => ReferenceEquals(light.Background, Palette.SettingsConnectionError)) >= 2);
            dialog.Close(false);
            Assert.False(await pending);

            IEnumerable<Border> Lights() => dialog.GetVisualDescendants().OfType<Border>().Where(border => border.IsEffectivelyVisible);
            async Task WaitFor(Func<bool> ready)
            {
                while (!ready())
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(25, timeout.Token);
                }
            }
        }
        finally { owner.Close(); }

        async Task<string?> Reply(string status = "200 OK")
        {
            using var client = await server.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var request = new StreamReader(stream, leaveOpen: true);
            var method = await request.ReadLineAsync(timeout.Token);
            while (await request.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            var data = method?.Contains("/text/", StringComparison.Ordinal) == true
                ? """{"data":[{"id":"text-example","supported_reasoning_efforts":["medium","high"]}]}"""
                : """{"data":[{"id":"image-example","name":"Example image model"}]}""";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n{data}");
            await stream.WriteAsync(response, timeout.Token);
            return method;
        }
    }

    private const string CupPrompt = "Product photograph of a cobalt-blue ceramic cup on a pale gray background.";
    public static bool LiveLocalCodex => Environment.GetEnvironmentVariable("COMPOSA_LIVE_CODEX") == "1";
    public static bool LiveComfyUI => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY"));

    [AvaloniaFact]
    public async Task Direct_api_inserts_in_the_starting_tab_and_undo_removes_only_the_result()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(LiveLocalCodex ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(20));
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var payload = JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(png.ToArray()) } } });
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/v1";
        var contacted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = ServeAsync(server, "/v1/images/generations", "test-image-key", payload, contacted, respond.Task, timeout.Token);
        var previous = Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY");
        Environment.SetEnvironmentVariable("COMPOSA_IMAGE_API_KEY", "test-image-key");
        var window = new MainWindow();
        try
        {
            window.Show();
            var original = EditorSession.NewCanvas(256, 256);
            window.AddSession(original);
            window.Settings.ComfyServerUrl = "http://127.0.0.1:1";
            window.Settings.EnhancerProvider = "local-codex";
            window.Settings.ImageGenerationRoute = ImageGenerationRoute.DirectApi;
            window.Settings.ImageGenerationApiUrl = endpoint;
            window.Settings.ImageGenerationApiModel = "gpt-image-2";
            var before = original.History.CurrentId;
            var prompt = CupPrompt;
            if (LiveLocalCodex)
            {
                var engine = DirectImageGenerator.From(window.Settings).PromptProfile();
                prompt = await window.AiTasks.EnhanceAsync(window.Settings, engine, AiTaskKind.GenerateImage, CupPrompt, TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
            }
            var run = window.RunAiForTests(AiTaskKind.GenerateImage, prompt);
            await contacted.Task.WaitAsync(timeout.Token);
            var other = EditorSession.NewCanvas(256, 256);
            window.AddSession(other);
            var otherBefore = other.History.CurrentId;
            respond.SetResult();
            await run.WaitAsync(timeout.Token);
            await reply;
            Assert.Same(other, window.Session);
            Assert.Equal(otherBefore, other.History.CurrentId);
            Assert.Equal("local-codex", window.Settings.EffectiveEnhancerProvider());
            Assert.Equal(AiOperationStatus.Completed, window.AiTasks.Operation?.Status);
            Assert.Contains(original.Document.AllLayers(), layer => layer.Tags.Contains("ai-generated"));
            original.Undo();
            Assert.Equal(before, original.History.CurrentId);
            Assert.DoesNotContain(original.Document.AllLayers(), layer => layer.Tags.Contains("ai-generated"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPOSA_IMAGE_API_KEY", previous);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Cancelling_the_image_api_preserves_the_document()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/v1";
        var contacted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = ServeAsync(server, "/v1/images/generations", "test-image-key", "", contacted, respond.Task, timeout.Token);
        var previous = Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY");
        Environment.SetEnvironmentVariable("COMPOSA_IMAGE_API_KEY", "test-image-key");
        var window = new MainWindow();
        try
        {
            window.Show();
            var target = EditorSession.NewCanvas(256, 256);
            window.AddSession(target);
            window.Settings.ImageGenerationRoute = ImageGenerationRoute.DirectApi;
            window.Settings.ImageGenerationApiUrl = endpoint;
            window.Settings.ImageGenerationApiModel = "gpt-image-2";
            var before = target.History.CurrentId;
            var run = window.RunAiForTests(AiTaskKind.GenerateImage, CupPrompt);
            await contacted.Task.WaitAsync(timeout.Token);
            window.AiTasks.Cancel();
            await run.WaitAsync(timeout.Token);
            Assert.Equal(AiOperationStatus.Cancelled, window.AiTasks.Operation?.Status);
            Assert.Equal(before, target.History.CurrentId);
            Assert.DoesNotContain(target.Document.AllLayers(), layer => layer.Tags.Contains("ai-generated"));
        }
        finally
        {
            timeout.Cancel();
            try { await reply; }
            catch (OperationCanceledException) { }
            Environment.SetEnvironmentVariable("COMPOSA_IMAGE_API_KEY", previous);
            window.Close();
        }
    }

    [AvaloniaFact(Skip = "Requires a signed-in local Codex CLI.", SkipUnless = nameof(LiveLocalCodex))]
    public async Task Local_codex_can_generate_on_the_canvas_and_undo_without_comfyui()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var target = EditorSession.NewCanvas(720, 480);
            window.AddSession(target);
            window.Settings.ComfyServerUrl = "http://127.0.0.1:1";
            window.Settings.ImageGenerationRoute = ImageGenerationRoute.LocalCodex;
            window.Settings.EnhancerProvider = "local-codex";
            var before = target.History.CurrentId;
            var profile = DirectImageGenerator.From(window.Settings).PromptProfile();
            var prompt = await window.AiTasks.EnhanceAsync(window.Settings, profile, AiTaskKind.GenerateImage, CupPrompt, TestContext.Current.CancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(prompt));
            await window.RunAiForTests(AiTaskKind.GenerateImage, prompt);
            Assert.True(window.AiTasks.Operation?.Status == AiOperationStatus.Completed, window.AiTasks.Operation?.Error);
            Assert.Contains(target.Document.AllLayers(), layer => layer.Tags.Contains("ai-generated") && layer.Pixels is { Width: > 0, Height: > 0 });
            Assert.True(Screenshots.Save(window, "direct-codex-image"));
            target.Undo();
            Assert.Equal(before, target.History.CurrentId);
            Assert.DoesNotContain(target.Document.AllLayers(), layer => layer.Tags.Contains("ai-generated"));
        }
        finally { window.Close(); }
    }

    [Fact(Skip = "Requires a selected ComfyUI instance with AVS Prompt Helper.", SkipUnless = nameof(LiveComfyUI))]
    public async Task Remote_codex_connection_check_works_without_a_discoverable_local_cli()
    {
        var server = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY")!;
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            Assert.Null(LocalCodex.FindExecutable());
            var service = new AiTaskService(() => server, Path.Combine(AppContext.BaseDirectory, "ai", "engines"))
            {
                ConnectionTimeoutSeconds = 15
            };
            var settings = new Settings { EnhancerProvider = "codex", EnhancerServerModels = new() { ["codex"] = "gpt-6.2-sol" } };
            await service.TestEnhancerAsync(settings, TestContext.Current.CancellationToken, null);
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    [AvaloniaFact(Skip = "Requires a selected ComfyUI instance with a FLUX.2 Klein workflow and model.", SkipUnless = nameof(LiveComfyUI))]
    public async Task Existing_comfy_workflow_can_generate_after_using_a_direct_route()
    {
        var server = Environment.GetEnvironmentVariable("COMPOSA_LIVE_COMFY")!;
        var window = new MainWindow();
        try
        {
            window.Show();
            var target = EditorSession.NewCanvas(720, 480);
            window.AddSession(target);
            window.Settings.ComfyServerUrl = server;
            window.Settings.ImageGenerationRoute = ImageGenerationRoute.LocalCodex;
            window.Settings.EnhancerProvider = "local-codex";
            var engine = window.AiTasks.Engines.Find("flux2-klein-intel-xpu")!;
            if (Environment.GetEnvironmentVariable("COMPOSA_LIVE_FLUX_MODEL") is { Length: > 0 } model)
            {
                var slot = window.AiTasks.Engines.ModelSlots(engine).First(item => item.Default == "flux-2-klein-9b_int8_convrot.safetensors");
                window.Settings.SetComfyModels(server, new Dictionary<string, string> { [slot.Key] = model });
            }
            window.Settings.AiTaskEngineIds[nameof(AiTaskKind.GenerateImage)] = engine.Id;
            window.Settings.ImageGenerationRoute = ImageGenerationRoute.ComfyWorkflow;
            var before = target.History.CurrentId;
            await window.RunAiForTests(AiTaskKind.GenerateImage, CupPrompt);
            Assert.True(window.AiTasks.Operation?.Status == AiOperationStatus.Completed, window.AiTasks.Operation?.Error);
            Assert.Equal("local-codex", window.Settings.EffectiveEnhancerProvider());
            Assert.True(Screenshots.Save(window, "existing-comfy-image"));
            target.Undo();
            Assert.Equal(before, target.History.CurrentId);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Direct_text_legacy_flag_keeps_using_the_chat_api_without_comfyui()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/v1";
        var payload = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "An improved product prompt." } } } });
        var contacted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = ServeAsync(server, "/v1/chat/completions", "test-text-key", payload, contacted, Task.CompletedTask, timeout.Token);
        var service = new AiTaskService(() => "http://127.0.0.1:1", Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var settings = new Settings { EnhancerUseCompatibleApi = true, EnhancerApiUrl = endpoint, EnhancerApiModel = "test-text" };
        var engine = new EngineProfile { Id = "test-image", DisplayName = "Image Test" };
        var result = await service.EnhanceAsync(settings, engine, AiTaskKind.GenerateImage, CupPrompt, timeout.Token, "test-text-key");
        await reply;
        Assert.Equal("direct", settings.EffectiveEnhancerProvider());
        Assert.Equal("An improved product prompt.", result);
    }

    private static async Task ServeAsync(TcpListener server, string path, string key, string payload, TaskCompletionSource contacted,
        Task respond, CancellationToken token)
    {
        using var client = await server.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        using var input = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Assert.Equal($"POST {path} HTTP/1.1", await input.ReadLineAsync(token));
        var headers = new List<string>();
        while (await input.ReadLineAsync(token) is { Length: > 0 } line) headers.Add(line);
        Assert.Contains(headers, header => header.Equals($"Authorization: Bearer {key}", StringComparison.OrdinalIgnoreCase));
        contacted.SetResult();
        await respond.WaitAsync(token);
        var data = Encoding.UTF8.GetBytes(payload);
        var headerBytes = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headerBytes, token);
        await stream.WriteAsync(data, token);
    }
}
