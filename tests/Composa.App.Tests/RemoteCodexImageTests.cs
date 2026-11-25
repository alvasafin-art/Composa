using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class RemoteCodexImageTests
{
    public static bool MockServerAvailable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_TEST_REMOTE_CODEX"));

    [Fact]
    public void Workflow_uses_typed_request_and_keeps_attachments_in_socket_order()
    {
        var id = Guid.NewGuid();
        var graph = RemoteCodexImage.Workflow("test-model", "high", "edit me", 1536, 1024, ["source.png", "mask.png"], true, id);
        var helper = graph["cli_image"]!["inputs"]!;
        var state = JsonNode.Parse(helper["state"]!.GetValue<string>())!;
        Assert.Equal("text_and_image", state["settings"]!["output_mode"]!.GetValue<string>());
        Assert.Equal("edit me", state["prompt"]!.GetValue<string>());
        var request = state["image_request"]!;
        Assert.Equal(1, request["version"]!.GetValue<int>());
        Assert.Equal("edit", request["operation"]!.GetValue<string>());
        Assert.Equal(1536, request["width"]!.GetValue<int>());
        Assert.Equal(id.ToString(), request["request_id"]!.GetValue<string>());
        Assert.Equal("source.png", graph["image_1"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("mask.png", graph["image_2"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("image_2", helper["image_2"]![0]!.GetValue<string>());
        Assert.Equal(1, graph[RemoteCodexImage.Preview]!["inputs"]!["images"]![1]!.GetValue<int>());
        Assert.False(ComfyClient.UsesApiCredits(graph));
    }

    [AvaloniaTheory(Skip = "Requires an isolated ComfyUI instance with a deterministic fake Codex executable.",
        SkipUnless = nameof(MockServerAvailable))]
    [InlineData(AiTaskKind.GenerateImage)]
    [InlineData(AiTaskKind.GenerativeFill)]
    public async Task Comfy_server_image_reaches_the_document_through_the_editor_command(AiTaskKind task)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var window = new MainWindow();
        try
        {
            window.Show();
            var document = EditorSession.NewCanvas(256, 256, SKColors.CornflowerBlue);
            if (task == AiTaskKind.GenerativeFill) document.SelectRect(new(96, 96, 160, 160));
            window.AddSession(document);
            window.Settings.ComfyServerUrl = Environment.GetEnvironmentVariable("COMPOSA_TEST_REMOTE_CODEX")!;
            window.Settings.ImageRemoteCodexModel = "server-fixture-model";
            window.Settings.ImageOperationRoutes[task] = ImageGenerationRoute.RemoteCodex;
            var before = document.History.CurrentId;
            await window.RunAiForTests(task, "Return the fixture color.").WaitAsync(timeout.Token);
            Assert.True(window.AiTasks.Operation?.Status == AiOperationStatus.Completed, window.AiTasks.Operation?.Error);
            Assert.Single(document.Document.AllLayers(), layer => layer.Tags.Contains("ai-generated"));
            Assert.Equal(SKColors.OrangeRed, document.Composite().GetPixel(128, 128));
            if (task == AiTaskKind.GenerativeFill)
                Assert.Equal(SKColors.CornflowerBlue, document.Composite().GetPixel(16, 16));
            document.Undo();
            Assert.Equal(before, document.History.CurrentId);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Skip = "Requires an isolated ComfyUI instance with a deterministic fake Codex executable.",
        SkipUnless = nameof(MockServerAvailable))]
    [InlineData("fixture cancel", "server-fixture-model", AiOperationStatus.Cancelled)]
    [InlineData("fixture error", "server-fixture-model", AiOperationStatus.Failed)]
    [InlineData("unknown model", "missing-fixture-model", AiOperationStatus.Failed)]
    public async Task Cancellation_and_server_errors_leave_the_document_unchanged(string prompt, string model,
        AiOperationStatus expected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var settings = new Settings
        {
            ComfyServerUrl = Environment.GetEnvironmentVariable("COMPOSA_TEST_REMOTE_CODEX")!,
            ImageRemoteCodexModel = model
        };
        var service = new AiTaskService(() => settings.ComfyServerUrl, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var document = EditorSession.NewCanvas(256, 256, SKColors.CornflowerBlue);
        var before = document.History.CurrentId;
        var request = new AiTaskRequest { Task = AiTaskKind.GenerateImage, Prompt = prompt };
        var run = service.RunDirectImageAsync(new EditorCommandService(document),
            DirectImageGenerator.From(settings) with { Route = ImageGenerationRoute.RemoteCodex }, request, () => true, timeout.Token);
        if (expected == AiOperationStatus.Cancelled)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
            service.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
            Assert.Contains(model == "missing-fixture-model" ? "Choose an available" : "Codex image generation failed", error.Message);
        }
        Assert.Equal(expected, service.Operation?.Status);
        Assert.Equal(before, document.History.CurrentId);
        Assert.DoesNotContain(document.Document.AllLayers(), layer => layer.Tags.Contains("ai-generated"));
    }
}
