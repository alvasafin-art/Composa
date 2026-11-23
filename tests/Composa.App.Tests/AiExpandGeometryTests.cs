using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class AiExpandGeometryTests
{
    [Theory]
    [InlineData(0, 0, 19, 0)]
    [InlineData(0, 0, 0, 13)]
    [InlineData(0, 0, 19, 13)]
    [InlineData(17, 11, 0, 0)]
    public void Expansion_keeps_source_coordinates_and_does_not_feed_edge_stripes_to_flux(
        int left, int top, int right, int bottom)
    {
        const int width = 79, height = 61;
        var session = Pattern(width, height);
        using var source = session.Flatten();
        var state = session.History.CurrentId;
        var request = new AiTaskRequest
        {
            Task = AiTaskKind.GenerativeExpand, ExpansionBounds = new(-left, -top, width + right, height + bottom),
            ExpansionMinimumSide = 147, Prompt = "Continue the pattern", Settings = new() { Seed = 2042 }
        };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var edit = new EditableMaskedWorkflow(inputs, request);
        var frame = SKRectI.Create(left, top, width, height);
        var plan = inputs.MaskPlan!;
        Assert.Null(edit.ConditioningImage);
        var conditioning = inputs.PreprocessedImage!;
        for (var y = 0; y < conditioning.Height; y++) for (var x = 0; x < conditioning.Width; x++)
        {
            var sourcePixel = frame.Contains(x, y);
            Assert.Equal(sourcePixel ? source.GetPixel(x - left, y - top) : SKColors.Black, conditioning.GetPixel(x, y));
            Assert.Equal(sourcePixel ? (byte)0 : (byte)255, inputs.OutputMask!.GetPixel(x, y).Alpha);
        }

        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var engine = catalog.Find("flux2-klein-intel-xpu")!;
        var binding = engine.Binding(request.Task)!;
        var files = inputs.Images().ToDictionary(pair => pair.Key, pair => pair.Key + ".png");
        var graph = WorkflowBinder.Bind(catalog.ReadWorkflow(engine, engine.Workflow(binding.Workflow)), binding, inputs.Values(files));
        WorkflowExecution.MaskedEdit(graph, inputs, request, new());
        edit.Bind(graph);
        Assert.Contains("black patches", graph["prompt"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal("source", graph["composa_edit_crop"]!["inputs"]!["image"]![0]!.GetValue<string>());
        Assert.Equal(plan.Bounds.Left, graph["composa_edit_crop"]!["inputs"]!["x"]!.GetValue<int>());
        Assert.Equal(plan.Bounds.Top, graph["composa_edit_mask_crop"]!["inputs"]!["y"]!.GetValue<int>());
        Assert.Equal("red", graph["mask"]!["inputs"]!["channel"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(new JsonArray("composa_edit_size", 0),
            graph["sourceEncode"]!["inputs"]!["pixels"]));
        Assert.True(JsonNode.DeepEquals(graph["basePos"]!["inputs"]!["latent"],
            graph["composa_edit_masked_latent"]!["inputs"]!["samples"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray("composa_edit_padded_mask", 0),
            graph["composa_edit_masked_latent"]!["inputs"]!["mask"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray("composa_edit_masked_latent", 0),
            graph["latent"]!["inputs"]!["samples"]));
        var sampler = graph["sampler"]!["inputs"]!;
        Assert.Equal("ConditioningZeroOut", graph["composa_edit_zero_conditioning"]!["class_type"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(sampler["positive"], graph["composa_edit_zero_conditioning"]!["inputs"]!["conditioning"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray("composa_edit_zero_conditioning", 0), sampler["negative"]));
        Assert.False(graph.ContainsKey("negative"));
        Assert.False(graph.ContainsKey("composa_condition"));
        Assert.True(JsonNode.DeepEquals(new JsonArray("composa_edit_size", 1),
            graph["composa_edit_padded_mask"]!["inputs"]!["destination"]));
        Assert.Equal(147, Math.Max(edit.ContentSize.Width, edit.ContentSize.Height));
        Assert.Equal(0, edit.GenerationSize.Width % 16);
        Assert.Equal(0, edit.GenerationSize.Height % 16);
        Assert.NotEqual(edit.ContentSize, edit.GenerationSize);

        // Padding is outside the content. Changing only its pixels cannot change the edit.
        using var control = edit.Finish(ProviderResponse(SKColors.Magenta));
        var result = edit.Finish(ProviderResponse(SKColors.Yellow));
        Assert.Equal(control.GetPixelSpan().ToArray(), result.GetPixelSpan().ToArray());
        AiTaskService.Insert(new EditorCommandService(session), request.Task, binding.OutputMode, [result], inputs.TargetBounds,
            inputs, outputIsComposited: true, localOutputMask: edit.Mask);
        using var flattened = session.Flatten();
        using var exported = SKBitmap.Decode(ImageFiles.Encode(flattened, ExportFormat.Png));
        Assert.Equal((width + left + right, height + top + bottom), (exported.Width, exported.Height));
        for (var y = 0; y < exported.Height; y++) for (var x = 0; x < exported.Width; x++)
        {
            if (!frame.Contains(x, y))
            {
                Assert.Equal(255, edit.Mask.GetPixel(x, y).Alpha);
                Assert.Equal(255, exported.GetPixel(x, y).Alpha);
                continue;
            }
            // The opaque source can only change within the bounded transition.
            var seam = plan.SeamWidth;
            if ((left == 0 || x >= frame.Left + seam) && (right == 0 || x < frame.Right - seam)
                && (top == 0 || y >= frame.Top + seam) && (bottom == 0 || y < frame.Bottom - seam))
                Assert.Equal(0, edit.Mask.GetPixel(x, y).Alpha);
            if (edit.Mask.GetPixel(x, y).Alpha == 0) Assert.Equal(source.GetPixel(x - left, y - top), exported.GetPixel(x, y));
        }
        session.Undo();
        Assert.Equal(state, session.History.CurrentId);
        using var restored = session.Flatten();
        Assert.Equal(source.GetPixelSpan().ToArray(), restored.GetPixelSpan().ToArray());

        SKBitmap ProviderResponse(SKColor padding)
        {
            var decoded = Pixels.NewColor(edit.GenerationSize.Width, edit.GenerationSize.Height);
            decoded.Erase(padding);
            for (var y = 0; y < edit.ContentSize.Height; y++) for (var x = 0; x < edit.ContentSize.Width; x++)
                decoded.SetPixel(x, y, x < edit.ContentSize.Width / 2 ? SKColors.Blue : SKColors.Green);
            return decoded;
        }
    }

    [Fact]
    public void Canvas_expansion_does_not_translate_the_decoded_rectangle()
    {
        var session = Pattern(79, 61);
        using var source = session.Flatten();
        var request = new AiTaskRequest
        {
            Task = AiTaskKind.GenerativeExpand, ExpansionBounds = new(0, 0, 98, 74),
            ExpansionMinimumSide = 98, AlignMaskedEdits = true
        };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var edit = new QwenImageInputs(inputs, request);
        using var unaligned = new QwenImageInputs(inputs, request with { AlignMaskedEdits = false });
        Assert.Equal((98, 74), edit.ContentSize);
        using var decoded = Pixels.NewColor(edit.PaddedSize.Width, edit.PaddedSize.Height);
        decoded.Erase(SKColors.Magenta);
        for (var y = 0; y < 74; y++) for (var x = 0; x < 98; x++)
        {
            // A shifted source in the provider answer tempts registration to sample
            // past the bottom and right edges of the decoded content.
            var color = x >= 2 && y >= 2 && x < 81 && y < 63
                ? source.GetPixel(x - 2, y - 2) : new SKColor((byte)(2 * x), (byte)(3 * y), 60);
            decoded.SetPixel(x, y, color);
        }
        using var expected = unaligned.Finish(Pixels.Clone(decoded));
        using var result = edit.Finish(Pixels.Clone(decoded));
        Assert.Equal(expected.GetPixelSpan().ToArray(), result.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(0, 0, 17, 11)]
    [InlineData(17, 11, 0, 0)]
    public async Task Assigned_expansion_workflow_uses_the_expanded_frame_and_mask_after_reloading(int left, int top, int right, int bottom)
    {
        const AiTaskKind task = AiTaskKind.GenerativeExpand;
        const string workflowId = "imported-outpaint";
        var settings = new Settings { ComfyServerUrl = "http://localhost:8188", EnhancerUseCompatibleApi = true,
            EnhancerApiUrl = "http://localhost:1234/v1", EnhancerApiModel = "existing-text-model" };
        settings.EnhancerInstructions["flux2-klein-intel-xpu/edit"] = "Keep the composition.";
        settings.SetComfyModels(settings.ComfyServerUrl, new Dictionary<string, string> { ["weight-slot"] = "saved-klein-weight" });
        settings.Workflows.Assignments[task] = workflowId;
        var graph = EditableWorkflows.Parse("""
            {"input":{"class_type":"LoadImage","inputs":{"image":"placeholder.png"}},
             "mask":{"class_type":"LoadImageMask","inputs":{"image":"placeholder.png","channel":"red"}},
             "prompt":{"class_type":"CLIPTextEncode","inputs":{"text":"placeholder"}}}
            """);
        settings.Workflows.Workflows[workflowId] = new("Imported expansion", graph, new()
        {
            ["contextImage"] = new() { NodeId = "input", Input = "image" },
            ["selectionMask"] = new() { NodeId = "mask", Input = "image" },
            ["prompt"] = new() { NodeId = "prompt", Input = "text" }
        });
        var loaded = settings.CopyPreferences();
        Assert.Equal(settings.ComfyServerUrl, loaded.ComfyServerUrl);
        Assert.Equal("direct", loaded.EffectiveEnhancerProvider());
        Assert.Equal(settings.EnhancerApiUrl, loaded.EnhancerApiUrl);
        Assert.Equal(settings.EnhancerApiModel, loaded.EnhancerApiModel);
        Assert.Equal("Keep the composition.", loaded.EnhancerInstructions["flux2-klein-intel-xpu/edit"]);
        Assert.Equal("saved-klein-weight", loaded.ComfyModelsFor(loaded.ComfyServerUrl)["weight-slot"]);
        Assert.Empty(loaded.ImageGenerationApiModel);
        var connection = new AiVariantsTests.Connection();
        var service = new AiTaskService(() => loaded.ComfyServerUrl, Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => connection)
        { WorkflowSettings = () => loaded.Workflows };
        var session = Pattern(79, 61);
        var before = session.History.CurrentId;
        using var original = session.Flatten();
        var request = new AiTaskRequest { Task = task, ExpansionBounds = new(-left, -top, 79 + right, 61 + bottom),
            ExpansionMode = AiExpansionMode.MaskedRegion, Prompt = "Continue the photographed surface." };
        await service.RunAsync(new EditorCommandService(session), request, TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationStatus.Completed, service.Operation?.Status);
        Assert.Equal(new[] { "contextImage", "selectionMask" }, connection.Uploads.Order());
        var posted = Assert.Single(connection.Graphs);
        Assert.Equal("contextImage.png", posted["input"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("selectionMask.png", posted["mask"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("red", posted["mask"]!["inputs"]!["channel"]!.GetValue<string>());
        using var expanded = session.Flatten();
        Assert.Equal((96, 72), (expanded.Width, expanded.Height));
        Assert.Equal(original.GetPixel(40, 30), expanded.GetPixel(40 + left, 30 + top));
        Assert.Equal(SKColors.CornflowerBlue, expanded.GetPixel(left == 0 ? 95 : 0, top == 0 ? 71 : 0));
        session.Undo();
        Assert.Equal(before, session.History.CurrentId);
        using var restored = session.Flatten();
        Assert.Equal(original.GetPixelSpan().ToArray(), restored.GetPixelSpan().ToArray());
    }

    private static EditorSession Pattern(int width, int height)
    {
        var source = Pixels.NewColor(width, height);
        var random = new Random(2042);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            source.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        var session = EditorSession.NewCanvas(width, height);
        session.AddImageLayer("Pattern", source, new(width / 2f, height / 2f), fit: false);
        return session;
    }
}
