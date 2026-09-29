using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Automation;
using Composa.App.Assistant;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;
using System.Net;
using System.Text.Json;

namespace Composa.App.Tests;

public class AssistantTests
{
    [Fact]
    public async Task API_provider_sends_history_and_attached_script_without_local_server_contract()
    {
        JsonElement payload = default;
        string? bearer = null;
        Uri? address = null;
        using var client = new HttpClient(new ApiHandler(async request =>
        {
            address = request.RequestUri;
            bearer = request.Headers.Authorization?.Parameter;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            payload = json.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"summary\\\":\\\"Done\\\",\\\"script\\\":\\\"\\\"}\"}}]}")
            };
        }));
        var settings = new Settings { AssistantProvider = "api", AssistantApiUrl = "https://example.test/custom/v1",
            AssistantApiModel = "any-model", AssistantApiKey = "test-session-key" };
        var provider = new ChatCompletionAssistantProvider(settings, transport: client);

        var reply = await provider.PlanAsync(new AssistantRequest("Explain this script", "{}", JavaScriptRuntime.Reference)
        {
            History = [new AssistantMessage("user", "Rename a layer"), new AssistantMessage("assistant", "Which layer?")],
            Attachments = [new AssistantAttachment("edit.js", "app.activeDocument.activeLayer.name = 'Test';")]
        }, TestContext.Current.CancellationToken);

        Assert.Equal("Done", reply.Summary);
        Assert.Equal("https://example.test/custom/v1/chat/completions", address!.ToString());
        Assert.Equal("test-session-key", bearer);
        Assert.Equal("any-model", payload.GetProperty("model").GetString());
        Assert.Equal(4, payload.GetProperty("messages").GetArrayLength());
        Assert.Contains("edit.js", payload.GetProperty("messages")[3].GetProperty("content").GetString());
        Assert.False(payload.TryGetProperty("chat_template_kwargs", out _));
        Assert.DoesNotContain("test-session-key", JsonSerializer.Serialize(settings));
    }

    [Theory]
    [InlineData("https://example.test", "https://example.test/v1/chat/completions")]
    [InlineData("https://example.test/api/v3/", "https://example.test/api/v3/chat/completions")]
    [InlineData("https://example.test/v1/chat/completions", "https://example.test/v1/chat/completions")]
    public void Assistant_supports_custom_API_paths(string input, string expected) =>
        Assert.Equal(expected, ChatCompletionAssistantProvider.Endpoint(input).ToString());

    [Fact]
    public async Task Local_chat_bounds_combined_history_and_files_to_the_configured_context()
    {
        JsonElement payload = default;
        using var client = new HttpClient(new ApiHandler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); payload = json.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"summary\\\":\\\"OK\\\",\\\"script\\\":\\\"\\\"}\"}}]}") };
        }));
        var provider = new ChatCompletionAssistantProvider(new Settings { AssistantContextSize = 8192, AssistantMaxTokens = 8192 }, local: true, transport: client);
        await provider.PlanAsync(new AssistantRequest("Explain the files", "{}", JavaScriptRuntime.Reference)
        {
            History = Enumerable.Range(0, 20).Select(_ => new AssistantMessage("user", new string('x', 8000))).ToArray(),
            Attachments = Enumerable.Range(0, 6).Select(index => new AssistantAttachment(index + ".js", new string('y', 20000))).ToArray()
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2048, payload.GetProperty("max_tokens").GetInt32());
        var messages = payload.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("content").GetString()!).ToArray();
        Assert.True(messages.Sum(text => text.Length) < 13000);
        Assert.Contains("truncated", messages[^1]);
    }

    [Fact]
    public async Task API_errors_do_not_expose_session_keys_in_chat()
    {
        using var client = new HttpClient(new ApiHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("Rejected placeholder-secret-key") })));
        var provider = new ChatCompletionAssistantProvider(new Settings { AssistantApiUrl = "https://example.test/v1",
            AssistantApiModel = "test", AssistantApiKey = "placeholder-secret-key" }, transport: client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.PlanAsync(new AssistantRequest("Hi", "{}", ""), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("placeholder-secret-key", error.Message);
        Assert.Contains("[redacted]", error.Message);
    }

    [Fact]
    public void Assistant_can_answer_a_conversation_without_a_script()
    {
        var reply = LlamaAssistantProvider.ParsePlan("What would you like to change in the background?");
        Assert.Contains("background", reply.Summary);
        Assert.Empty(reply.Script);
    }

    [Theory]
    [InlineData("{\"script\":\"doSomething()\"}")]
    [InlineData("{\"summary\":\"Done\",\"script\":null}")]
    public void Incomplete_plans_are_never_automatically_executed(string content)
    {
        Assert.Empty(LlamaAssistantProvider.ParsePlan(content).Script);
    }

    [AvaloniaFact]
    public async Task Chat_reads_attached_scripts_as_data_and_keeps_their_context_for_followups()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-chat-" + Guid.NewGuid() + ".js");
        await File.WriteAllTextAsync(path, "// Attached script\napp.activeDocument.activeLayer.name = 'Do not run';");
        var owner = new MainWindow(); owner.AddSession(EditorSession.NewCanvas(80, 60)); owner.Show();
        using var host = new LlamaServerHost(owner.Settings);
        var provider = new RecordingAssistant();
        var chat = new AssistantWindow(owner, () => owner.Session, owner.Settings, host, new JavaScriptRuntime(), owner.AiTasks)
        { ProviderFactory = () => provider };
        chat.Show(owner);
        try
        {
            await chat.AddFilesAsync([path]);
            Assert.Equal(1, chat.AttachmentCount);
            await chat.SendAsync("Use the attached script as reference to rename the layer to Chat edit");
            Assert.Contains("Do not run", Assert.Single(provider.Requests[0].Attachments).Text);
            Assert.Equal(0, chat.AttachmentCount);
            Assert.Equal("Chat edit", owner.Session!.ActiveLayer!.Name);
            await chat.SendAsync("Explain the script I attached earlier");
            Assert.Contains("Do not run", provider.Requests[1].History[0].Text);
        }
        finally { chat.Close(); owner.Close(); File.Delete(path); }
    }

    [Fact]
    public async Task JavaScript_imports_only_explicitly_attached_images_and_undoes_the_import()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-chat-" + Guid.NewGuid() + ".png");
        using var bitmap = Pixels.NewColor(12, 10); bitmap.Erase(SKColors.CornflowerBlue);
        ImageFiles.Save(bitmap, path, ExportFormat.Png);
        try
        {
            var session = EditorSession.NewCanvas(80, 60);
            var runtime = new JavaScriptRuntime();
            await runtime.ExecuteAsync(session, "app.activeDocument.addAttachedImage(0);", new RecordingAiRunner(), new Settings(),
                "Assistant import", TestContext.Current.CancellationToken, [path]);
            Assert.Equal(2, session.Document.Layers.Count);
            session.Undo(); Assert.Single(session.Document.Layers);
            await Assert.ThrowsAnyAsync<Exception>(() => runtime.ExecuteAsync(session, "app.activeDocument.addAttachedImage(1);",
                new RecordingAiRunner(), new Settings(), "Invalid import", TestContext.Current.CancellationToken, [path]));
            Assert.Single(session.Document.Layers);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Chat_remembers_previous_messages_and_applies_edits_as_one_undo_step()
    {
        var owner = new MainWindow();
        owner.AddSession(EditorSession.NewCanvas(80, 60, SKColors.White)); owner.Show();
        using var host = new LlamaServerHost(owner.Settings);
        var provider = new RecordingAssistant();
        var conversation = new AssistantConversation();
        var chat = new AssistantWindow(owner, () => owner.Session, owner.Settings, host, new JavaScriptRuntime(), owner.AiTasks, conversation)
        { ProviderFactory = () => provider };
        chat.Show(owner);
        await chat.SendAsync("Rename the current layer");
        Assert.Equal("Chat edit", owner.Session!.ActiveLayer!.Name);
        Assert.Equal("Assistant edit", owner.Session.History.UndoName);
        await chat.SendAsync("Why did you choose that name?");
        Assert.Equal(2, provider.Requests[1].History.Count);
        Assert.Equal(4, chat.MessageCount);
        Assert.True(Screenshots.Save(chat, "assistant-chat-conversation"));
        chat.Width = 390; chat.Height = 500;
        Assert.True(Screenshots.Save(chat, "assistant-chat-narrow"));
        owner.Session.Undo();
        Assert.Equal("Background", owner.Session.ActiveLayer!.Name);
        chat.Close(); owner.Close();
    }

    private sealed class RecordingAssistant : IAssistantProvider
    {
        public string Id => "test";
        public List<AssistantRequest> Requests { get; } = [];
        public Task<AssistantPlan> PlanAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Requests.Count == 1 ? new AssistantPlan("Renamed the active layer.", "app.activeDocument.activeLayer.name = 'Chat edit';")
                : new AssistantPlan("That name reflects your requested edit.", ""));
        }
    }

    private sealed class ApiHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }

    [Theory]
    [InlineData("{\"summary\":\"Done\",\"script\":\"\"}")]
    [InlineData("The requested edit is safe.\n{\"summary\":\"Done\",\"script\":\"const value = { nested: true };\"}\n")]
    public void Assistant_extracts_a_complete_json_plan_from_chat_template_output(string response)
    {
        var json = LlamaAssistantProvider.ExtractJsonObject(response);
        var plan = System.Text.Json.JsonSerializer.Deserialize<AssistantPlan>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal("Done", plan?.Summary);
    }

    [Fact]
    public void Assistant_accepts_the_fenced_javascript_fallback_used_by_local_qwen()
    {
        var plan = LlamaAssistantProvider.ParsePlan("Rename the tagged layer.\n```javascript\napp.activeDocument.findLayersByTag('title')[0].name = 'Winter Sale';\n```");
        Assert.Equal("Rename the tagged layer.", plan.Summary);
        Assert.Contains("findLayersByTag", plan.Script);
    }

    [Fact]
    public async Task Local_llama_provider_returns_a_structured_plan_when_live_test_is_requested()
    {
        var executable = Environment.GetEnvironmentVariable("COMPOSA_LIVE_LLAMA_SERVER");
        var model = Environment.GetEnvironmentVariable("COMPOSA_LIVE_LLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(model)) return;
        var settings = new Settings
        {
            AssistantServerExecutable = executable, AssistantModelPath = model, AssistantServerUrl = "http://127.0.0.1:18080",
            AssistantContextSize = 8192, AssistantMaxTokens = 512, AssistantAutoStart = true
        };
        using var host = new LlamaServerHost(settings);
        var provider = new LlamaAssistantProvider(settings, host);

        var plan = await provider.PlanAsync(new AssistantRequest(
            "Rename the layer tagged title to Winter Sale.",
            "{\"title\":\"Test\",\"width\":640,\"height\":420,\"layers\":[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"Heading\",\"kind\":\"text\",\"tags\":[\"title\"],\"text\":\"Old\"}]}",
            JavaScriptRuntime.Reference), TestContext.Current.CancellationToken);

        Assert.NotEmpty(plan.Summary);
        Assert.Contains("findLayersByTag", plan.Script);
    }

    [Fact]
    public void JavaScript_edits_use_live_layer_api_and_one_undo_step()
    {
        var session = EditorSession.NewCanvas(640, 420, SKColors.White);
        var title = session.AddText(new SKPoint(80, 100), new TextStyle { Text = "Old title", Size = 48 });
        session.SetLayerTags(title, ["title", "editable"]);
        var before = session.History.Count;
        var runtime = new JavaScriptRuntime();

        runtime.Execute(session, """
            const title = app.activeDocument.findLayersByTag('title')[0];
            title.text = 'Winter Sale';
            title.opacity = 0.75;
            title.transform = { x: 120, y: 90, width: 360 };
            """, "Assistant edit", TestContext.Current.CancellationToken);

        Assert.Equal("Winter Sale", title.Text!.Text);
        Assert.Equal(0.75, title.Opacity);
        Assert.Equal(120, title.Transform.X);
        Assert.Equal(before + 1, session.History.Count);
        Assert.Equal("Assistant edit", session.History.UndoName);
        session.Undo();
        Assert.Equal("Old title", session.Document.Find(title.Id)!.Text!.Text);
    }

    [Fact]
    public void JavaScript_failure_rolls_back_the_whole_transaction()
    {
        var session = EditorSession.NewCanvas(80, 60, SKColors.White);
        var runtime = new JavaScriptRuntime();
        var before = session.History.Count;

        Assert.ThrowsAny<Exception>(() => runtime.Execute(session, "app.activeDocument.addLayer('Temporary'); throw new Error('stop');", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(session.Document.Layers);
        Assert.Equal(before, session.History.Count);
    }

    [Fact]
    public async Task JavaScript_can_queue_an_AI_task_inside_the_same_undo_transaction()
    {
        var session = EditorSession.NewCanvas(320, 180, SKColors.White);
        var before = session.History.Count;
        var runner = new RecordingAiRunner();
        var runtime = new JavaScriptRuntime();

        await runtime.ExecuteAsync(session, "app.activeDocument.activeLayer.name = 'Prepared'; ai.generativeFill('add flowers', { seed: 42 });",
            runner, new Settings { AiMegapixels = 0.5 }, "Assistant mixed edit", TestContext.Current.CancellationToken);

        Assert.Equal(AiTaskKind.GenerativeFill, runner.Request?.Task);
        Assert.Equal("add flowers", runner.Request?.Prompt);
        Assert.Equal(42, runner.Request?.Settings.Seed);
        Assert.Equal(before + 1, session.History.Count);
        Assert.Equal("Assistant mixed edit", session.History.UndoName);
        Assert.Equal("AI result", session.ActiveLayer?.Name);
        session.Undo();
        Assert.Equal("Background", session.ActiveLayer?.Name);
    }

    [AvaloniaFact]
    public void Assistant_is_a_real_enabled_AI_menu_command()
    {
        var window = new MainWindow();
        window.Show();
        var ai = Assert.Single(window.GetVisualDescendants().OfType<MenuItem>(), menu => menu.Header?.ToString()?.Replace("_", "") == "AI");
        var item = Assert.Single(ai.Items.OfType<MenuItem>(), menu => menu.Header?.ToString() == "Assistant…");
        Assert.True(item.IsEnabled);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Assistant" && button.IsEnabled);
    }

    [AvaloniaFact]
    public void Assistant_window_renders_as_a_native_reviewable_plan()
    {
        var owner = new MainWindow();
        owner.AddSession(EditorSession.NewCanvas(640, 420, SKColors.White));
        owner.Show();
        using var host = new LlamaServerHost(owner.Settings);
        var assistant = new AssistantWindow(owner, () => owner.Session, owner.Settings, host, new JavaScriptRuntime(), owner.AiTasks);
        assistant.Show(owner);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Screenshots.Save(assistant, "assistant-local-scripting"));

        assistant.Close();
        owner.Close();
    }

    private sealed class RecordingAiRunner : IAiTaskRunner
    {
        public AiTaskRequest? Request { get; private set; }

        public Task RunAsync(IEditorCommandService editor, AiTaskRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            editor.Transaction("Fake AI", session =>
            {
                var result = session.AddBlankLayer();
                session.Rename(result, "AI result");
            });
            return Task.CompletedTask;
        }
    }
}
