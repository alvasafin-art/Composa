using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class MaskedEditTests
{
    private static EngineCatalog Catalog() => new(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));

    [Theory]
    [InlineData(1, AiVariantMode.List, false)]
    [InlineData(3, AiVariantMode.Batch, true)]
    public async Task Masked_removal_runs_real_model_and_keeps_the_original_outside_the_seam_when_requested(int variants, AiVariantMode mode, bool originalSize)
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_MASKED_EDIT_GPU_URL"); if (string.IsNullOrWhiteSpace(url)) return;
        var session = EditorSession.NewCanvas(301, 189, new SKColor(200, 200, 200));
        var subject = Pixels.NewColor(48, 36); subject.Erase(new SKColor(30, 40, 50));
        session.AddImageLayer("Test object", subject, new SKPoint(150, 94), fit: false);
        session.SelectRect(new SKRect(120, 70, 180, 118));
        using var original = session.Flatten();
        var service = new AiTaskService(() => url, Catalog().Root);
        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest { Task = AiTaskKind.RemoveObject,
            Prompt = "Continue the same clean plain light gray background. No object, no pattern, no grain.",
            Settings = new() { Width = 256, Height = 256, Seed = 19, Variants = variants, VariantMode = mode,
                Values = new() { ["imageOriginalSize"] = originalSize, ["maskGrow"] = 4, ["maskBlend"] = 32, ["maskContext"] = 2.0, ["colorMatch"] = "subtle" } } }, TestContext.Current.CancellationToken);
        using var result = session.Flatten(); Assert.Equal((301, 189), (result.Width, result.Height));
        Assert.Equal(original.GetPixel(0, 0), result.GetPixel(0, 0)); Assert.Equal(original.GetPixel(50, 90), result.GetPixel(50, 90));
        Assert.True(result.GetPixel(150, 94).Red > 60, "The selected dark object/black preconditioning patch was not removed.");
        Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
        if (variants == 3) { Assert.Equal(3, session.AiVariantGroup!.Children.Count); Assert.Single(session.AiVariantGroup.Children, layer => layer.Visible); }
        // A plain source must not come back with a dark rectangle or an artificially noisy seam.
        var originalTone = original.GetPixel(50, 90).Red;
        Assert.InRange(Math.Abs(result.GetPixel(150, 94).Red - originalTone), 0, 16);
        Directory.CreateDirectory(Screenshots.Folder);
        ImageFiles.Save(original, Path.Combine(Screenshots.Folder, "masked-gpu-before.png"), ExportFormat.Png);
        ImageFiles.Save(result, Path.Combine(Screenshots.Folder, "masked-gpu-after.png"), ExportFormat.Png);
    }

    [Theory]
    [InlineData(AiTaskKind.RemoveObject, true)]
    [InlineData(AiTaskKind.GenerativeFill, true)]
    [InlineData(AiTaskKind.RemoveObject, false)]
    [InlineData(AiTaskKind.GenerativeFill, false)]
    public void Mask_conditions_sampling_and_stitch_uses_original_full_resolution_mask(AiTaskKind task, bool pixaroma)
    {
        var session = EditorSession.NewCanvas(301, 189, SKColors.White); session.SelectRect(new SKRect(110, 65, 180, 120));
        var request = new AiTaskRequest { Task = task, RemoveObject = new() { Dilation = 0, Feather = 0 },
            Settings = new() { Width = 256, Height = 256, Values = new() { ["maskGrow"] = 0, ["maskBlend"] = 24, ["maskContext"] = 2.0 } } };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        var catalog = Catalog(); var engine = catalog.Profiles.Single(profile => profile.Id == "flux2-klein-intel-xpu"); var binding = engine.Binding(task)!;
        var values = inputs.Values(inputs.Images().ToDictionary(pair => pair.Key, pair => pair.Key + ".png"));
        foreach (var (key, value) in request.Settings.Values) values[key] = value;
        var graph = WorkflowBinder.Bind(catalog.ReadWorkflow(engine, engine.Workflow(binding.Workflow)), binding, values);
        var caps = new ComfyServerCapabilities { NodeTypes = pixaroma ? ["PixaromaInpaintCrop", "PixaromaInpaintStitch"] : [] };
        WorkflowExecution.MaskedEdit(graph, inputs, request, caps);
        Assert.True(graph["composa_condition"]!["inputs"]!["noise_mask"]!.GetValue<bool>());
        Assert.Equal("composa_condition", graph["latent"]!["inputs"]!["samples"]![0]!.GetValue<string>());
        Assert.Equal(2, graph["latent"]!["inputs"]!["samples"]![1]!.GetValue<int>());
        Assert.Equal("model", graph["sampler"]!["inputs"]!["model"]![0]!.GetValue<string>());
        Assert.False(graph.ContainsKey("sampling"));
        Assert.Equal("euler", graph["sampler"]!["inputs"]!["sampler_name"]!.GetValue<string>());
        if (pixaroma)
        {
            Assert.Equal(16, graph["crop"]!["inputs"]!["multiple"]!.GetValue<int>());
            Assert.Equal(24, graph["crop"]!["inputs"]!["softness"]!.GetValue<int>());
            Assert.Equal(4, graph["crop"]!["inputs"]!["mask_blur"]!.GetValue<int>());
            Assert.Equal("mask", graph["stitch"]!["inputs"]!["blend_mode"]!.GetValue<string>());
            Assert.Equal(2, graph["stitch"]!["inputs"]!["crop_info"]![1]!.GetValue<int>());
            Assert.Null(graph["stitch"]!["inputs"]!["mask"]); // no second blur from the conditioning mask
            Assert.Equal(task == AiTaskKind.RemoveObject ? "originalSource" : "source", graph["crop"]!["inputs"]!["image"]![0]!.GetValue<string>());
            if (task == AiTaskKind.RemoveObject)
            {
                Assert.Equal(0, graph["blackPatch"]!["inputs"]!["destination"]![1]!.GetValue<int>());
                Assert.Equal("composa_black_mask", graph["blackPatch"]!["inputs"]!["mask"]![0]!.GetValue<string>());
                Assert.Equal(1, graph["composa_black_mask"]!["inputs"]!["mask"]![1]!.GetValue<int>());
            }
        }
        else Assert.Equal(0, graph["crop"]!["inputs"]!["mask_hipass_filter"]!.GetValue<int>());
        Assert.True(graph["composa_sampling_mask"]!["inputs"]!["expand"]!.GetValue<int>() > 0);
        Assert.Equal("composa_sampling_mask", graph["composa_condition"]!["inputs"]!["mask"]![0]!.GetValue<string>());
        WorkflowExecution.Batch(graph, 3);
        Assert.Equal(3, graph["latent"]!["inputs"]!["amount"]!.GetValue<int>());
        if (pixaroma) Assert.Equal("PixaromaInpaintStitch", graph["stitch"]!["class_type"]!.GetValue<string>());
        else Assert.Equal(3, graph.Count(pair => pair.Value?["class_type"]?.GetValue<string>() == "InpaintStitchImproved"));
    }

    [Fact]
    public void Faint_selection_does_not_trigger_pixaroma_whole_crop_fallback()
    {
        var session = EditorSession.NewCanvas(301, 189, SKColors.White); session.SelectRect(new SKRect(110, 65, 180, 120));
        using var inputs = AiTaskInputPreparer.Prepare(session, new() { Task = AiTaskKind.GenerativeFill });
        var pixels = inputs.SelectionMask!.GetPixelSpan();
        for (var y = 0; y < inputs.SelectionMask.Height; y++) for (var x = 0; x < inputs.SelectionMask.Width; x++)
            if (pixels[y * inputs.SelectionMask.RowBytes + x] != 0) pixels[y * inputs.SelectionMask.RowBytes + x] = 96;
        Pixels.Invalidate(inputs.SelectionMask);
        var catalog = Catalog(); var engine = catalog.Profiles.Single(profile => profile.Id == "flux2-klein-intel-xpu");
        var graph = catalog.ReadWorkflow(engine, engine.Workflow(engine.Binding(AiTaskKind.GenerativeFill)!.Workflow));
        WorkflowExecution.MaskedEdit(graph, inputs, new() { Task = AiTaskKind.GenerativeFill }, new ComfyServerCapabilities
            { NodeTypes = ["PixaromaInpaintCrop", "PixaromaInpaintStitch"] });
        Assert.Equal("InpaintCropImproved", graph["crop"]!["class_type"]!.GetValue<string>());
        Assert.Equal(0, graph["crop"]!["inputs"]!["mask_hipass_filter"]!.GetValue<int>());
    }

    [Fact]
    public void Additional_prompt_applies_to_generation_and_edits_and_can_be_disabled_without_removing_task_instructions()
    {
        var session = EditorSession.NewCanvas(32, 32, SKColors.White); session.SelectRect(new SKRect(8, 8, 24, 24));
        using var enabled = AiTaskInputPreparer.Prepare(session, new() { Task = AiTaskKind.RemoveObject, Prompt = "erase cup", AdditionalPrompt = "KEEP COLORS UNIQUE" });
        Assert.Contains("KEEP COLORS UNIQUE", enabled.Prompt); Assert.Contains("Remove the black patch", enabled.Prompt); Assert.Contains("erase cup", enabled.Prompt);
        using var disabled = AiTaskInputPreparer.Prepare(session, new() { Task = AiTaskKind.RemoveObject, Prompt = "erase cup" });
        Assert.DoesNotContain("KEEP COLORS UNIQUE", disabled.Prompt); Assert.Contains("Remove the black patch", disabled.Prompt);
        using var fresh = AiTaskInputPreparer.Prepare(session, new() { Task = AiTaskKind.GenerateImage, Prompt = "a forest", AdditionalPrompt = "KEEP COLORS UNIQUE" });
        Assert.Contains("a forest", fresh.Prompt); Assert.Contains("KEEP COLORS UNIQUE", fresh.Prompt);
    }

    [AvaloniaFact]
    public async Task Comfy_prompt_settings_are_editable_toggleable_and_cancel_safe()
    {
        var window = new MainWindow(); window.Show(); var settings = new Settings();
        var service = new AiTaskService(() => "http://localhost:8188", Catalog().Root);
        var asking = AiDialogs.SettingsDialog(window, settings, service); Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        var prompt = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn);
        var enabled = dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Content as string == "Append for this workflow pack");
        prompt.Text = "custom preservation instruction"; enabled.IsChecked = false;
        Assert.False(prompt.IsEnabled);
        var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(view => !double.IsInfinity(view.MaxHeight));
        scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height); Dispatcher.UIThread.RunJobs();
        Assert.True(Screenshots.Save(dialog, "comfy-additional-prompt"));
        dialog.Close(false); Assert.False(await asking); Assert.True(settings.ComfyAdditionalPromptEnabled);
        Assert.Equal(AiPromptDefaults.PreserveAppearance, settings.ComfyAdditionalPrompt);
        asking = AiDialogs.SettingsDialog(window, settings, service); Dispatcher.UIThread.RunJobs(); dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn).Text = "custom preservation instruction";
        dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Content as string == "Append for this workflow pack").IsChecked = false;
        dialog.Close(true); Assert.True(await asking); Assert.False(settings.PromptFor(service.SelectedEngine!.Id).Enabled);
        Assert.Equal("custom preservation instruction", settings.PromptFor(service.SelectedEngine.Id).Text);
        Assert.Equal(AiPromptDefaults.PreserveAppearance, settings.PromptFor("chatgpt-image-2.5").Text); window.Close();
    }

    [Fact]
    public async Task Pixaroma_real_server_stitch_has_soft_outward_edge_and_preserves_odd_canvas_when_requested()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_PIXAROMA_TEST_URL"); if (string.IsNullOrWhiteSpace(url)) return;
        var session = EditorSession.NewCanvas(301, 189, new SKColor(200, 200, 200)); session.SelectRect(new SKRect(110, 65, 180, 120));
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeFill, Settings = new() { Width = 256, Height = 256,
            Values = new() { ["maskGrow"] = 0, ["maskBlend"] = 24, ["maskContext"] = 2.0, ["colorMatch"] = "off" } } };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var client = new ComfyClient(url); var (_, caps) = await client.TestConnectionAsync(TestContext.Current.CancellationToken);
        var catalog = Catalog(); var engine = catalog.Profiles.Single(profile => profile.Id == "flux2-klein-intel-xpu"); var binding = engine.Binding(request.Task)!;
        var uploads = new Dictionary<string, string>();
        foreach (var (name, bitmap) in inputs.Images()) if (binding.Inputs.ContainsKey(name)) uploads[name] = await client.UploadPngAsync(name, bitmap, TestContext.Current.CancellationToken);
        var values = inputs.Values(uploads); foreach (var (key, value) in request.Settings.Values) values[key] = value;
        var graph = WorkflowBinder.Bind(catalog.ReadWorkflow(engine, engine.Workflow(binding.Workflow)), binding, values);
        WorkflowExecution.MaskedEdit(graph, inputs, request, caps);
        Assert.Equal("PixaromaInpaintStitch", graph["stitch"]!["class_type"]!.GetValue<string>());
        graph["patch"] = new JsonObject { ["class_type"] = "EmptyImage", ["inputs"] = new JsonObject
        { ["width"] = new JsonArray("crop", 3), ["height"] = new JsonArray("crop", 4), ["batch_size"] = 1, ["color"] = 0x787878 } };
        graph["stitch"]!["inputs"]!["image"] = new JsonArray("patch", 0);
        var reachable = new HashSet<string>();
        void Visit(string id)
        {
            if (!reachable.Add(id)) return;
            foreach (var (_, value) in graph[id]!["inputs"]!.AsObject())
                if (value is JsonArray link && link.Count == 2 && link[0] is JsonValue node && node.TryGetValue<string>(out var next) && graph.ContainsKey(next)) Visit(next);
        }
        Visit("save"); foreach (var key in graph.Select(pair => pair.Key).Where(key => !reachable.Contains(key)).ToArray()) graph.Remove(key);
        var execution = await client.ExecuteAsync(graph, cancellationToken: TestContext.Current.CancellationToken);
        using (execution.History)
        using (var result = await client.DownloadAsync(Assert.Single(execution.Images), TestContext.Current.CancellationToken))
        {
            Assert.Equal((301, 189), (result.Width, result.Height)); Assert.Equal(120, result.GetPixel(140, 90).Red);
            Assert.Equal(200, result.GetPixel(70, 90).Red); Assert.Equal(inputs.ContextImage.GetPixel(0, 0), result.GetPixel(0, 0));
            var edge = Enumerable.Range(86, 25).Select(x => result.GetPixel(x, 90).Red).ToArray();
            Assert.True(edge.Distinct().Count() > 15); // real feather, not a hard threshold
            Assert.True(edge.Zip(edge.Skip(1), (a, b) => a >= b).All(value => value));
            AiTaskService.Insert(new EditorCommandService(session), request.Task, binding.OutputMode, [Pixels.Clone(result)], inputs.TargetBounds, inputs, true);
            using var rendered = session.Flatten(); Assert.Equal(result.GetPixelSpan().ToArray(), rendered.GetPixelSpan().ToArray()); // not alpha-blended twice
            Directory.CreateDirectory(Screenshots.Folder);
            ImageFiles.Save(result, Path.Combine(Screenshots.Folder, "pixaroma-real-soft-stitch.png"), ExportFormat.Png);
        }
    }
}
