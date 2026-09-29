using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Automation;
using Composa.App.Assistant;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class AssistantTests
{
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
