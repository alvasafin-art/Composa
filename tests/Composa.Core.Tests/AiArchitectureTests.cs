using System.Text;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class AiArchitectureTests
{
    private const string Manifest = """
    {
      "id": "test-engine",
      "displayName": "Test Engine",
      "manifestVersion": 1,
      "requiredNodeTypes": ["LoadModel", "Sampler"],
      "requiredAssets": [{ "kind": "checkpoint", "name": "model.safetensors" }],
      "workflows": [{ "id": "edit", "version": 2, "file": "workflows/edit.json", "outputNodes": ["9"] }],
      "tasks": [
        { "task": "generativeFill", "workflow": "edit", "version": 1, "outputMode": "newLayerWithMask",
          "inputs": { "prompt": { "nodeId": "6", "input": "text" }, "sourceImage": { "nodeId": "1", "input": "image" } } },
        { "task": "removeObject", "workflow": "edit", "version": 1, "preprocess": "remove-object", "outputMode": "newLayerWithMask",
          "inputs": { "preprocessedImage": { "nodeId": "1", "input": "image" }, "selectionMask": { "nodeId": "2", "input": "image" } } }
      ],
      "parameters": [{ "id": "steps", "name": "Steps", "kind": "integer", "minimum": 1, "maximum": 100, "default": 20 }],
      "lora": { "supported": true, "maximum": 3, "minimumStrength": -2, "maximumStrength": 2 }
    }
    """;

    private static EngineProfile Profile()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Manifest));
        return EngineProfile.Parse(stream);
    }

    [Fact]
    public void Engine_profile_parses_explicit_versioned_bindings_and_reuses_a_workflow()
    {
        var profile = Profile();

        Assert.Equal("Test Engine", profile.DisplayName);
        Assert.Equal("edit", profile.Binding(AiTaskKind.GenerativeFill)!.Workflow);
        Assert.Equal("edit", profile.Binding(AiTaskKind.RemoveObject)!.Workflow);
        Assert.Same(profile.Workflow("edit"), profile.Workflow(profile.Binding(AiTaskKind.RemoveObject)!.Workflow));
        Assert.Equal("6", profile.Binding(AiTaskKind.GenerativeFill)!.Inputs["prompt"].NodeId);
        Assert.True(profile.Lora.Supported);
    }

    [Fact]
    public void Prompt_presets_are_data_driven_and_separate_from_tasks()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""[{"id":"recolor","name":"Recolor","task":"generativeFill","prompt":"Recolor to "}]"""));
        var presets = PromptPresetCatalog.Parse(stream);
        Assert.Single(presets);
        Assert.Equal(AiTaskKind.GenerativeFill, presets[0].Task);
        Assert.Equal("Recolor to ", presets[0].Prompt);
    }

    [Fact]
    public void Capability_resolution_lists_missing_nodes_and_assets()
    {
        var capabilities = new ComfyServerCapabilities
        {
            NodeTypes = new HashSet<string>(["LoadModel"], StringComparer.Ordinal),
            Assets = new Dictionary<EngineAssetKind, HashSet<string>> { [EngineAssetKind.Checkpoint] = new(["other.safetensors"], StringComparer.OrdinalIgnoreCase) }
        };

        var result = EngineCompatibility.Check(Profile(), capabilities);

        Assert.False(result.IsCompatible);
        Assert.Contains("node Sampler", result.Missing);
        Assert.Contains("checkpoint model.safetensors", result.Missing);
    }

    [Fact]
    public void Workflow_binding_targets_node_ids_not_display_names()
    {
        var workflow = JsonNode.Parse("""{"1":{"inputs":{"image":"old"}},"6":{"inputs":{"text":"old"}}}""")!.AsObject();
        var binding = Profile().Binding(AiTaskKind.GenerativeFill)!;

        var bound = WorkflowBinder.Bind(workflow, binding, new Dictionary<string, object?> { ["prompt"] = "new prompt", ["sourceImage"] = "input.png" });

        Assert.Equal("new prompt", bound["6"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal("input.png", bound["1"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("old", workflow["6"]!["inputs"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Remove_object_preprocessing_expands_feathers_and_hides_source_pixels()
    {
        using var source = Pixels.NewColor(21, 21);
        source.Erase(SKColors.Red);
        using var rawMask = SelectionMask.FromRect(21, 21, new SKRect(9, 9, 12, 12));

        var (prepared, mask) = RemoveObjectPreprocessor.Prepare(source, rawMask, new RemoveObjectSettings { Dilation = 2, Feather = 2, FillColor = SKColors.Blue, FillMode = RemoveObjectFillMode.Solid });
        using (prepared)
        using (mask)
        {
            Assert.True(SelectionMask.Bounds(mask).Width > SelectionMask.Bounds(rawMask).Width);
            Assert.True(prepared.GetPixel(10, 10).Blue > prepared.GetPixel(10, 10).Red);
            Assert.Equal(SKColors.Red, source.GetPixel(10, 10));
        }
    }

    [Fact]
    public void Standard_inputs_include_composite_layer_selection_alpha_and_preprocessed_assets()
    {
        var session = EditorSession.NewCanvas(20, 16, SKColors.White);
        session.SelectRect(new SKRect(4, 3, 10, 9));
        using var inputs = AiTaskInputPreparer.Prepare(session, new AiTaskRequest { Task = AiTaskKind.RemoveObject, Prompt = "clean background", Settings = new() { Seed = 42 } });

        Assert.Equal(20, inputs.CanvasWidth);
        Assert.Equal(16, inputs.CanvasHeight);
        Assert.NotNull(inputs.ActiveLayerImage);
        Assert.NotNull(inputs.SelectionMask);
        Assert.NotNull(inputs.AlphaMask);
        Assert.NotNull(inputs.PreprocessedImage);
        Assert.NotNull(inputs.PreprocessedMask);
        Assert.Equal(42, inputs.Seed);
    }

    [Fact]
    public void Ai_output_is_a_new_masked_layer_and_one_undo_step()
    {
        var session = EditorSession.NewCanvas(12, 10, SKColors.White);
        using var selected = SelectionMask.FromRect(12, 10, new SKRect(2, 2, 8, 8));
        var pixels = Pixels.NewColor(12, 10);
        pixels.Erase(SKColors.CornflowerBlue);
        var mask = Pixels.Clone(selected);

        var inserted = session.InsertAiOutput(AiTaskKind.GenerativeFill, [new AiOutput("AI Fill", pixels, mask)]);

        Assert.Equal(2, session.Document.Layers.Count);
        Assert.Same(pixels, inserted[0].Pixels);
        Assert.Same(mask, inserted[0].Mask);
        Assert.Contains("ai-generated", inserted[0].Tags);
        Assert.Equal("AI Generative Fill", session.History.UndoName);
        session.Undo();
        Assert.Single(session.Document.Layers);
        session.Redo();
        Assert.Equal(2, session.Document.Layers.Count);
    }

    [Fact]
    public void Layer_tags_round_trip_and_old_files_without_tags_remain_valid()
    {
        var session = EditorSession.NewCanvas(8, 8);
        session.SetLayerTags(session.ActiveLayer!, ["Title", "hero image", "bad/tag"]);
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;

        var loaded = ProjectFile.Read(stream);

        Assert.Contains("title", loaded.ActiveLayer!.Tags);
        Assert.Contains("hero-image", loaded.ActiveLayer.Tags);
        Assert.DoesNotContain("bad/tag", loaded.ActiveLayer.Tags);
        Assert.Equal(ProjectFile.Version, 6);
    }

    [Fact]
    public void Task_availability_is_centralized_on_selection_and_engine_capabilities()
    {
        var session = EditorSession.NewCanvas(10, 10);
        var profile = Profile();
        Assert.False(AiTaskAvailability.Resolve(session, profile, AiTaskKind.GenerativeFill).Available);
        session.SelectRect(new SKRect(1, 1, 4, 4));
        Assert.True(AiTaskAvailability.Resolve(session, profile, AiTaskKind.GenerativeFill).Available);
        Assert.False(AiTaskAvailability.Resolve(session, profile, AiTaskKind.Relight).Available);
    }

    [Fact]
    public void Selection_brush_uses_the_document_selection_and_undo()
    {
        var session = EditorSession.NewCanvas(40, 30);
        session.SelectionBrushSize = 8;
        session.BeginSelectionBrush(new SKPoint(5, 10));
        session.ContinueSelectionBrush(new SKPoint(25, 10));
        session.EndSelectionBrush();

        Assert.NotNull(session.Selection);
        Assert.True(SelectionMask.Bounds(session.Selection!).Width >= 20);
        Assert.Equal("Selection Brush", session.History.UndoName);
        session.Undo();
        Assert.Null(session.Selection);
    }

    [Fact]
    public void Automation_batches_normal_commands_into_one_transaction()
    {
        var session = EditorSession.NewCanvas(8, 8);
        var commands = new EditorCommandService(session);
        commands.Transaction("Assistant edit", editor => { editor.AddBlankLayer(); editor.AddBlankLayer(); });

        Assert.Equal(3, session.Document.Layers.Count);
        Assert.Equal("Assistant edit", session.History.UndoName);
        session.Undo();
        Assert.Single(session.Document.Layers);
    }
}
