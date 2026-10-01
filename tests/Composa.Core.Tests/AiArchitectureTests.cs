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
    [Fact]
    public void Stitched_result_does_not_fill_transparent_pixels_outside_transition_support()
    {
        using var context=Pixels.NewColor(20,16); using var generated=Pixels.NewColor(20,16); generated.Erase(SKColors.White);
        using var support=SelectionMask.FromRect(20,16,new SKRect(6,5,12,11));
        using var mask=AiResultPostprocessor.CompositedMask(generated,context,support);
        Assert.Equal(0,mask.GetPixel(0,0).Alpha); Assert.Equal(255,mask.GetPixel(8,8).Alpha);
    }

    [Fact]
    public void Comfy_alpha_loss_is_not_mistaken_for_a_background_edit()
    {
        using var context=Pixels.NewColor(20,16);
        context.SetPixel(0,0,new SKColor(100,80,60,128)); Pixels.Invalidate(context);
        using var result=Pixels.NewColor(20,16); result.Erase(SKColors.Black);
        result.SetPixel(0,0,new SKColor(100,80,60)); Pixels.Invalidate(result);
        using var support=Pixels.NewMask(20,16,255);
        using var selection=SelectionMask.FromRect(20,16,new SKRect(6,5,12,11));
        using var mask=AiResultPostprocessor.CompositedMask(result,context,support,selection);
        Assert.Equal(0,mask.GetPixel(0,0).Alpha); Assert.Equal(0,mask.GetPixel(19,15).Alpha);
        Assert.Equal(255,mask.GetPixel(8,8).Alpha);
    }

    [Fact]
    public void Removal_color_matching_preserves_premultiplied_alpha()
    {
        using var context=Pixels.NewColor(20,16); context.Erase(new SKColor(230,200,180,80));
        using var generated=Pixels.NewColor(20,16); generated.Erase(new SKColor(190,180,160,80));
        using var mask=SelectionMask.FromRect(20,16,new SKRect(6,5,12,11));
        using var matched=AiResultPostprocessor.MatchRemoval(generated,context,mask,1);
        var bytes=matched.GetPixelSpan();
        for (var y=0;y<matched.Height;y++) for (var x=0;x<matched.Width;x++)
        {
            var offset=y*matched.RowBytes+x*4;
            Assert.Equal(80,bytes[offset+3]); Assert.True(bytes[offset]<=80 && bytes[offset+1]<=80 && bytes[offset+2]<=80);
        }
    }
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
    public void Workflow_binding_prunes_missing_optional_reference_branches_and_rewires_the_chain()
    {
        var workflow = JsonNode.Parse("""
        {
          "base":{"inputs":{}},
          "load":{"inputs":{"image":"placeholder.png"},"_meta":{"optionalInput":"referenceImage1"}},
          "ref":{"inputs":{"conditioning":["base",0],"latent":["load",0]},"_meta":{"optionalInput":"referenceImage1","fallback":["base",0]}},
          "sampler":{"inputs":{"positive":["ref",0]}}
        }
        """)!.AsObject();
        var binding = Profile().Binding(AiTaskKind.GenerativeFill)!;

        var bound = WorkflowBinder.Bind(workflow, binding, new Dictionary<string, object?>());

        Assert.Null(bound["load"]);
        Assert.Null(bound["ref"]);
        Assert.Equal("base", bound["sampler"]!["inputs"]!["positive"]![0]!.GetValue<string>());
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
        using var reference = Pixels.NewColor(5, 7);
        using var inputs = AiTaskInputPreparer.Prepare(session, new AiTaskRequest { Task = AiTaskKind.RemoveObject, ReferenceImage = reference, Settings = new() { Seed = 42 } });

        Assert.Equal(20, inputs.CanvasWidth);
        Assert.Equal(16, inputs.CanvasHeight);
        Assert.NotNull(inputs.ActiveLayerImage);
        Assert.NotNull(inputs.SelectionMask);
        Assert.NotNull(inputs.AlphaMask);
        Assert.NotNull(inputs.PreprocessedImage);
        Assert.NotNull(inputs.PreprocessedMask);
        Assert.Equal(SKColors.Black, inputs.PreprocessedImage!.GetPixel(6, 5));
        Assert.Contains("surrounding content", inputs.Prompt);
        Assert.NotNull(inputs.ReferenceImage);
        Assert.NotSame(reference, inputs.ReferenceImage);
        Assert.Contains("referenceImage", inputs.Images());
        Assert.Equal(new SKRectI(4, 3, 10, 9), inputs.TargetBounds);
        Assert.Equal(42, inputs.Seed);

        using var guided = AiTaskInputPreparer.Prepare(session, new AiTaskRequest { Task = AiTaskKind.RemoveObject, Prompt = "leave the branch intact" });
        Assert.Contains("Remove the black patch", guided.Prompt);
        Assert.Contains("leave the branch intact", guided.Prompt);
    }

    [Fact]
    public void Upscale_with_selection_sends_context_but_retains_exact_insertion_bounds()
    {
        var session = EditorSession.NewCanvas(20, 16, SKColors.CornflowerBlue);
        session.SelectRect(new SKRect(4, 3, 10, 9));

        using var inputs = AiTaskInputPreparer.Prepare(session, new AiTaskRequest { Task = AiTaskKind.Upscale });

        Assert.Equal((20, 16), (inputs.SourceImage.Width, inputs.SourceImage.Height));
        Assert.Equal(session.Document.Bounds,inputs.UpscaleSourceBounds);
        Assert.Equal((20, 16), (inputs.ContextImage.Width, inputs.ContextImage.Height));
        Assert.Equal(new SKRectI(4, 3, 10, 9), inputs.TargetBounds);
    }

    [Fact]
    public void Change_background_uses_the_inverse_selection_and_preserves_the_subject_instruction()
    {
        var session = EditorSession.NewCanvas(20, 16, SKColors.CornflowerBlue);
        session.SelectRect(new SKRect(4, 3, 10, 9));

        using var inputs = AiTaskInputPreparer.Prepare(session, new AiTaskRequest
        {
            Task = AiTaskKind.ChangeBackground, Prompt = "a warm sunset beach"
        });

        Assert.NotNull(inputs.BackgroundMask);
        Assert.Equal((byte)0, inputs.BackgroundMask!.GetPixel(6, 5).Alpha);
        Assert.Equal((byte)255, inputs.BackgroundMask.GetPixel(0, 0).Alpha);
        Assert.Contains("original subject will be composited separately", inputs.Prompt);
        Assert.Equal(SKColors.Black, inputs.PreprocessedImage!.GetPixel(0, 0));
        Assert.Equal(SKColors.CornflowerBlue, inputs.PreprocessedImage.GetPixel(6, 5));
    }

    [Fact]
    public void Remove_hides_even_soft_selected_pixels_with_opaque_black_before_feathering_the_blend()
    {
        using var source = Pixels.NewColor(24, 24);
        source.Erase(SKColors.Red);
        using var mask = Pixels.NewMask(24, 24);
        mask.GetPixelSpan()[12 * mask.RowBytes + 12] = 96;
        var (prepared, blend) = RemoveObjectPreprocessor.Prepare(source, mask, new RemoveObjectSettings { Dilation = 0, Feather = 4 });
        using (prepared)
        using (blend)
        {
            Assert.Equal(SKColors.Black, prepared.GetPixel(12, 12));
            Assert.Equal(SKColors.Red, source.GetPixel(12, 12));
            Assert.Equal(SKColors.Red, prepared.GetPixel(0, 0));
        }
    }

    [Fact]
    public void Remove_postprocessing_matches_local_color_and_texture_without_changing_the_source()
    {
        using var context = Pixels.NewColor(24, 24);
        using var generated = Pixels.NewColor(24, 24);
        using var mask = SelectionMask.FromRect(24, 24, new SKRect(8, 8, 16, 16));
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++)
                context.SetPixel(x, y, (x + y) % 2 == 0 ? new SKColor(90, 110, 130) : new SKColor(130, 150, 170));
        generated.Erase(new SKColor(25, 30, 35));

        using var matched = AiResultPostprocessor.MatchRemoval(generated, context, mask, 7);

        Assert.True(matched.GetPixel(12, 12).Red > generated.GetPixel(12, 12).Red);
        Assert.InRange(matched.GetPixel(12, 12).Red, 25, 70);
        Assert.Equal(new SKColor(25, 30, 35), generated.GetPixel(12, 12));
        Assert.Equal(generated.GetPixel(0, 0), matched.GetPixel(0, 0));
        Assert.NotEqual(matched.GetPixel(11, 12).Red, matched.GetPixel(12, 12).Red);
    }

    [Fact]
    public void Remove_does_not_recolor_a_stitched_patch_to_the_unrelated_scene_average()
    {
        using var context = Pixels.NewColor(40, 40);
        context.Erase(SKColors.DarkGreen);
        using var generated = Pixels.Clone(context);
        using (var canvas = new SKCanvas(generated)) canvas.Clear(SKColors.DarkGreen);
        generated.SetPixel(20, 20, SKColors.Brown);
        using var mask = SelectionMask.FromRect(40, 40, new SKRect(10, 10, 30, 30));
        using var matched = AiResultPostprocessor.MatchRemoval(generated, context, mask, 7);
        Assert.Equal(SKColors.Brown, matched.GetPixel(20, 20));
    }

    [Fact]
    public void Match_to_scene_adds_editable_clipped_adjustments_as_one_undo_step()
    {
        var session = EditorSession.NewCanvas(40, 30, new SKColor(170, 150, 120));
        var subject = Pixels.NewColor(12, 10);
        subject.Erase(new SKColor(45, 70, 120));
        session.AddImageLayer("Subject", subject, new SKPoint(20, 15), fit: false);
        var before = session.Document.Layers.Count;

        var added = session.MatchActiveLayerToScene();

        Assert.True(added.Count >= 2);
        Assert.All(added, layer =>
        {
            Assert.True(layer.IsAdjustment);
            Assert.True(layer.Clipped);
            Assert.Contains("editable", layer.Tags);
        });
        Assert.Equal("AI Match to Scene", session.History.UndoName);
        session.Undo();
        Assert.Equal(before, session.Document.Layers.Count);
    }

    [Fact]
    public void Megapixel_size_preserves_the_target_aspect_ratio()
    {
        var size = AiDimensions.FromMegapixels(0.75, 400, 200);

        Assert.InRange((double)size.Width / size.Height, 1.98, 2.02);
        Assert.InRange((long)size.Width * size.Height, 720_000, 780_000);
        Assert.Equal(0, size.Width % 8);
        Assert.Equal(0, size.Height % 8);
    }

    [Fact]
    public void Up_to_six_references_keep_order_and_can_preserve_original_size()
    {
        var session = EditorSession.NewCanvas(16, 16);
        session.SelectRect(new SKRect(2, 2, 14, 14));
        var references = Enumerable.Range(1, 7).Select(index =>
        {
            var image = Pixels.NewColor(20 + index, 10 + index);
            image.Erase(new SKColor((byte)index, 0, 0));
            return image;
        }).ToList();
        try
        {
            using var inputs = AiTaskInputPreparer.Prepare(session, new AiTaskRequest
            {
                Task = AiTaskKind.Relight, ReferenceImages = references, ReferenceMegapixels = null
            });
            Assert.Equal(6, inputs.ReferenceImages.Count);
            Assert.Equal(references[0].Width, inputs.ReferenceImages[0].Width);
            Assert.Equal((byte)6, inputs.ReferenceImages[5].GetPixel(0, 0).Red);
            Assert.Same(inputs.ReferenceImages[0], inputs.Images()["referenceImage1"]);
        }
        finally { references.ForEach(image => image.Dispose()); }
    }

    [Fact]
    public void Generative_expand_prepares_exact_canvas_and_outside_mask()
    {
        var session = EditorSession.NewCanvas(10, 8, SKColors.Red);
        using var inputs = AiTaskInputPreparer.Prepare(session, new AiTaskRequest
        {
            Task = AiTaskKind.GenerativeExpand, ExpansionBounds = new SKRectI(-3, -2, 15, 12)
        });

        Assert.Equal((18, 14), (inputs.PreprocessedImage!.Width, inputs.PreprocessedImage.Height));
        Assert.Equal(255, inputs.PreprocessedMask!.GetPixelSpan()[0]);
        Assert.Equal(0, inputs.PreprocessedMask.GetPixelSpan()[2 * inputs.PreprocessedMask.RowBytes + 3]);
        Assert.Equal(new SKRectI(-3, -2, 15, 12), inputs.TargetBounds);
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
    public void Ai_patch_output_is_positioned_over_its_target_bounds()
    {
        var session = EditorSession.NewCanvas(20, 16, SKColors.White);
        var pixels = Pixels.NewColor(100, 50);

        var layer = Assert.Single(session.InsertAiOutput(AiTaskKind.GenerativeFill,
            [new AiOutput("AI patch", pixels, Bounds: new SKRect(4, 3, 16, 9))]));

        Assert.Equal(4, layer.Transform.X);
        Assert.Equal(3, layer.Transform.Y);
        Assert.Equal(12, layer.Transform.Width);
        Assert.Equal(6, layer.Transform.Height);
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
        Assert.Equal(7, ProjectFile.Version);
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

    [Fact]
    public void Automation_transform_does_not_fold_previous_user_history()
    {
        var session = EditorSession.NewCanvas(8, 8);
        var original = session.ActiveLayer!;
        session.SetTransform(original, original.Transform with { X = 1 });
        session.AddBlankLayer(); // Inspector's last edit is now one revision behind.
        var entries = session.History.Count;

        session.RunTransaction("Assistant edit", editor =>
            editor.SetTransform(original, original.Transform with { X = 3 }));

        Assert.Equal(entries + 1, session.History.Count);
        Assert.Equal("Assistant edit", session.History.UndoName);
        session.Undo();
        Assert.Equal(2, session.Document.Layers.Count);
        Assert.Equal(1, session.Document.Layers[0].Transform.X);
        session.Undo();
        Assert.Single(session.Document.Layers);
        Assert.Equal(1, session.ActiveLayer!.Transform.X);
        session.Undo();
        Assert.Equal(0, session.ActiveLayer!.Transform.X);
    }
}
