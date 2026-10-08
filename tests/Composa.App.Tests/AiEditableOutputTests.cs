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

public class AiEditableOutputTests
{
    [AvaloniaFact]
    public async Task Key_dialog_masks_entry_accepts_only_nonempty_keys_and_closes_on_cancellation()
    {
        var owner = new Window(); owner.Show();
        try
        {
            var pending = AiDialogs.ApiKey(owner, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows);
            var entry = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
            var accept = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Continue");
            Assert.NotEqual('\0', entry.PasswordChar); Assert.False(accept.IsEnabled);
            entry.Text = "   "; Dispatcher.UIThread.RunJobs(); Assert.False(accept.IsEnabled);
            entry.Text = " example-key "; Dispatcher.UIThread.RunJobs(); Assert.True(accept.IsEnabled);
            accept.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("example-key", await pending);
            using var cancelled = new CancellationTokenSource();
            pending = AiDialogs.ApiKey(owner, cancelled.Token);
            Dispatcher.UIThread.RunJobs(); Assert.Single(owner.OwnedWindows);
            cancelled.Cancel(); Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending); Assert.Empty(owner.OwnedWindows);
        }
        finally { owner.Close(); }
    }

    [Fact]
    public void Requested_defaults_migrate_once_and_then_keep_the_users_changes_and_credentials_private()
    {
        var old = Settings.FromJson("""{"AiOriginalSize":false,"AiLorasEnabled":true,"AiMaskGrow":8,"AiMaskBlend":32,"AiMaskBlur":4,"AiColorMatch":"subtle","AiGptContextPadding":32,"ComfyServerUrl":"http://server:8188"}""");
        Assert.Equal((true,false,16,48,16,"off",0),(old.AiOriginalSize,old.AiLorasEnabled,old.AiMaskGrow,old.AiMaskBlend,old.AiMaskBlur,old.AiColorMatch,old.AiGptContextPadding));
        old.AiMaskBlend=20; old.AiGptContextPadding=10; old.AssistantApiKey="not-for-disk";
        var json = System.Text.Json.JsonSerializer.Serialize(old); Assert.DoesNotContain("not-for-disk",json);
        var reloaded=Settings.FromJson(json); Assert.Equal((20,10),(reloaded.AiMaskBlend,reloaded.AiGptContextPadding)); Assert.Equal("http://server:8188",reloaded.ComfyServerUrl);
        reloaded.AiTaskEngineIds[nameof(AiTaskKind.GenerativeFill)]=""; Assert.Null(reloaded.EngineForTask(AiTaskKind.GenerativeFill));
    }

    [Fact]
    public void Flux_fill_defaults_upgrade_existing_preferences_without_changing_other_tasks_and_roundtrip()
    {
        var settings = Settings.FromJson("""{"AiDefaultsRevision":1,"AiMaskGrow":16,"AiMaskBlend":48,"AiMaskBlur":16,"AiMaskContext":2,"AiGptMaskGrow":4,"AiGptMaskBlend":8}""");
        Assert.Equal((4,8,4,1.2), (settings.AiFluxFillMaskGrow,settings.AiFluxFillMaskBlend,settings.AiFluxFillMaskBlur,settings.AiFluxFillMaskContext));
        Assert.Equal((16,48,16,2.0), (settings.AiMaskGrow,settings.AiMaskBlend,settings.AiMaskBlur,settings.AiMaskContext));
        Assert.Equal((4,8), (settings.AiGptMaskGrow,settings.AiGptMaskBlend));
        settings.AiFluxFillMaskContext = 1.5; settings.AiFluxFillMaskBlend = 12;
        var reloaded = Settings.FromJson(System.Text.Json.JsonSerializer.Serialize(settings));
        Assert.Equal((4,12,4,1.5), (reloaded.AiFluxFillMaskGrow,reloaded.AiFluxFillMaskBlend,reloaded.AiFluxFillMaskBlur,reloaded.AiFluxFillMaskContext));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gpt_keeps_unmasked_crop_pixels_and_blends_only_through_the_editable_layer_mask(bool expand)
    {
        var s = EditorSession.NewCanvas(173, 121, SKColors.White); s.SelectRect(new SKRect(50, 40, 90, 80));
        var request = new AiTaskRequest { Task = expand ? AiTaskKind.GenerativeExpand : AiTaskKind.GenerativeFill,
            ExpansionBounds = expand ? new(-20, -10, 193, 141) : null,
            Settings = new() { Values = new() { ["gptContextPadding"] = 12, ["maskBlend"] = 8, ["maskGrow"] = 0 } } };
        using var inputs = AiTaskInputPreparer.Prepare(s, request); using var api = new PartnerImageInputs(inputs, request);
        var decoded = PartnerImageTests.GeneratedPatch(inputs, api, SKColors.Blue);
        if (!expand) { decoded.SetPixel(49 - api.SourceBounds.Left, 60 - api.SourceBounds.Top, SKColors.Blue); Pixels.Invalidate(decoded); }
        var raw = api.FinishUnmasked(decoded); using var mask = api.OutputMask();
        AiTaskService.Insert(new EditorCommandService(s), request.Task, AiOutputMode.NewLayerWithMask, [raw], inputs.TargetBounds, inputs, true, localOutputMask: mask);
        var layer = s.ActiveLayer!; Assert.NotNull(layer.Mask);
        if (!expand)
        {
            Assert.Equal(SKColors.White, layer.Pixels!.GetPixel(40, 60)); // unchanged context restored exactly
            Assert.Equal((byte)0, layer.Mask.GetPixel(40, 60).Alpha);
            using var composite = s.Flatten(); Assert.Equal(SKColors.White, composite.GetPixel(40, 60));
            var m = layer.Mask.GetPixel(49, 60).Alpha;
            Assert.InRange(m, (byte)1, (byte)254);
            Assert.InRange(composite.GetPixel(49, 60).Red, 254 - m, 256 - m); // no double feather
        }
        else { using var composite = s.Flatten(); Assert.Equal(SKColors.White, composite.GetPixel(100, 100)); Assert.Equal(SKColors.White, layer.Pixels!.GetPixel(100, 100)); }
        s.Undo(); Assert.Single(s.Document.Layers); s.Redo(); Assert.NotNull(s.ActiveLayer!.Mask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flux_native_crop_matches_context_geometry_and_exports_decoded_pixels_without_stitch(bool pixaroma)
    {
        var s = EditorSession.NewCanvas(641, 423, SKColors.White); s.SelectRect(new SKRect(250, 160, 330, 240));
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeFill, Settings = new() { Width = 1024, Height = 1024,
            Values = new() { ["maskGrow"] = 16, ["maskBlend"] = 16, ["maskContext"] = 2, ["maskBlur"] = 16, ["imageOriginalSize"] = true } } };
        using var inputs = AiTaskInputPreparer.Prepare(s, request);
        var catalog = new EngineCatalog(System.IO.Path.Combine(AppContext.BaseDirectory, "ai", "engines")); var pack = catalog.Find("flux2-klein-intel-xpu")!;
        var binding = pack.Binding(request.Task)!;
        var graph = WorkflowBinder.Bind(catalog.ReadWorkflow(pack, pack.Workflow(binding.Workflow)), binding,
            inputs.Values(inputs.Images().ToDictionary(pair => pair.Key, pair => pair.Key + ".png")));
        var server = new ComfyServerCapabilities { NodeTypes = pixaroma ? new() { "PixaromaInpaintCrop", "PixaromaInpaintStitch" } : [] };
        WorkflowExecution.MaskedEdit(graph, inputs, request, server);
        using var editable = new EditableMaskedWorkflow(inputs, request); editable.Bind(graph);
        Assert.Equal("composa_edit_padded_mask", graph["composa_condition"]!["inputs"]!["mask"]![0]!.GetValue<string>());
        Assert.Equal("composa_condition", graph["sampler"]!["inputs"]!["positive"]![0]!.GetValue<string>());
        Assert.False(graph.ContainsKey("composa_noise_latent"));
        Assert.DoesNotContain(graph, pair => pair.Value?["class_type"]?.GetValue<string>()?.StartsWith("Pixaroma") == true);
        Assert.Equal(AutomaticAiMask.Geometry(new(250,160,330,240), s.Document.Bounds).Bounds, editable.Bounds);
        Assert.False(graph.ContainsKey("stitch")); Assert.False(graph.ContainsKey("crop"));
        Assert.Equal("decode", graph["save"]!["inputs"]!["images"]![0]!.GetValue<string>());
        Assert.Equal(editable.Bounds.Left, graph["composa_edit_crop"]!["inputs"]!["x"]!.GetValue<int>());
        Assert.DoesNotContain(graph, pair => pair.Value?["inputs"] is System.Text.Json.Nodes.JsonObject fields && fields.Any(field =>
            field.Value is System.Text.Json.Nodes.JsonArray link && link.Count == 2 && link[0]?.GetValue<string>() is "crop" or "stitch"));
        var decoded = Pixels.NewColor(editable.GenerationSize.Width, editable.GenerationSize.Height); decoded.Erase(SKColors.White);
        using (var draw = new SKCanvas(decoded)) using (var paint = new SKPaint { Color = SKColors.Blue })
        {
            draw.Scale((float)editable.ContentSize.Width/editable.Bounds.Width, (float)editable.ContentSize.Height/editable.Bounds.Height);
            draw.DrawRect(new SKRect(248-editable.Bounds.Left,158-editable.Bounds.Top,332-editable.Bounds.Left,242-editable.Bounds.Top),paint);
        }
        var raw = editable.Finish(decoded);
        AiTaskService.Insert(new EditorCommandService(s), request.Task, AiOutputMode.NewLayerWithMask, [raw], inputs.TargetBounds, inputs, true, localOutputMask: editable.Mask);
        Assert.Equal(SKColors.Blue, s.ActiveLayer!.Pixels!.GetPixel(249, 200));
        using var composite = s.Flatten(); Assert.Equal(SKColors.White, composite.GetPixel(210, 200));
        Assert.InRange(s.ActiveLayer.Mask!.GetPixel(249,200).Alpha, (byte)1, (byte)254);
        Assert.Equal(SKColors.Blue, composite.GetPixel(290, 200));
    }

    [Theory]
    [InlineData(AiExpansionMode.MaskedRegion)]
    [InlineData(AiExpansionMode.WholeImage)]
    public async Task Gpt_expand_always_has_automatic_prompt_even_with_empty_user_and_additional_prompt(AiExpansionMode mode)
    {
        var s = EditorSession.NewCanvas(79,61,SKColors.White); var connection = new PartnerImageTests.Connection();
        var service = PartnerImageTests.Service(connection); service.AdditionalPromptForPack = _ => "";
        await service.RunAsync(new EditorCommandService(s), new() { Task = AiTaskKind.GenerativeExpand,
            ExpansionMode = mode, ExpansionBounds = new(-10,-10,89,71), ExpansionMinimumSide = 0 }, TestContext.Current.CancellationToken);
        Assert.Equal(AiPromptDefaults.Expand, connection.Graphs[0]["gpt"]!["inputs"]!["prompt"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_key_is_requested_before_uploads_and_cancelling_never_submits(bool cancel)
    {
        var s = EditorSession.NewCanvas(79,61); var connection = new PartnerImageTests.Connection();
        var service = PartnerImageTests.Service(connection, key:false); var asked = 0;
        service.RequestApiKey = _ => { asked++; Assert.Empty(connection.Uploads); Assert.Empty(connection.Graphs); return Task.FromResult<string?>(cancel ? null : "test-session-key"); };
        var pending = service.RunAsync(new EditorCommandService(s), new() { Task = AiTaskKind.GenerateImage, Prompt = "a cup" }, TestContext.Current.CancellationToken);
        if (cancel) { await Assert.ThrowsAsync<OperationCanceledException>(() => pending); Assert.Empty(connection.Graphs); Assert.Equal(0,s.History.Count); }
        else { await pending; Assert.Single(connection.Graphs); Assert.Equal("test-session-key",service.Credential); }
        Assert.Equal(1,asked);
    }

    [AvaloniaFact]
    public async Task Defaults_context_overlay_crop_reset_and_generation_advanced_are_consistent()
    {
        var w = new MainWindow { Width = 1280, Height = 900 }; w.Settings.CheckForUpdates = false; w.Show();
        try
        {
            Assert.False(w.Settings.AiLorasEnabled); Assert.Equal((16,48,16,"off"), (w.Settings.AiMaskGrow,w.Settings.AiMaskBlend,w.Settings.AiMaskBlur,w.Settings.AiColorMatch));
            Assert.Equal((4,8,0),(w.Settings.AiGptMaskGrow,w.Settings.AiGptMaskBlend,w.Settings.AiGptContextPadding));
            Assert.Equal((4,8,4,1.2),(w.Settings.AiFluxFillMaskGrow,w.Settings.AiFluxFillMaskBlend,w.Settings.AiFluxFillMaskBlur,w.Settings.AiFluxFillMaskContext));
            Assert.False(w.AiTasks.EngineFor(AiTaskKind.GenerativeExpand)!.PaidApi); Assert.False(w.AiTasks.EngineFor(AiTaskKind.GenerativeFill)!.PaidApi);
            var s = EditorSession.NewCanvas(641,423,SKColors.White); w.AddSession(s); w.AiTasks.SetConnectedForTests(); s.SelectRect(new SKRect(250,160,330,240));
            Assert.Equal(AutomaticAiMask.Geometry(new(250,160,330,240),s.Document.Bounds).Bounds,w.Canvas.AiContextBounds);
            Assert.True(Screenshots.Save(w,"ai-context-bounds"));
            w.Settings.AiShowContextBounds = false; w.AiTasks.SetConnectedForTests(); Dispatcher.UIThread.RunJobs(); Assert.Null(w.Canvas.AiContextBounds);
            w.Settings.AiShowContextBounds = true; w.AiTasks.SetConnectedForTests(); Dispatcher.UIThread.RunJobs(); Assert.NotNull(w.Canvas.AiContextBounds);
            s.CropRatio = "1:1"; w.SelectTool(Tool.Crop); s.Crop(new(-11,-7,691,451),"Expand Canvas");
            Assert.Equal(new SKRect(0,0,702,458),w.Canvas.CropRect); s.Undo(); Assert.Equal(new SKRect(0,0,641,423),w.Canvas.CropRect);
            var pending = AiDialogs.Advanced(w,w.Settings,w.AiTasks,AiTaskKind.GenerateImage); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(w.OwnedWindows); var labels = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text).ToArray();
            Assert.DoesNotContain("Mask grow",labels); Assert.DoesNotContain("Mask context",labels);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(),b=>b.Content as string=="123");
            Assert.True(Screenshots.Save(dialog,"ai-generate-advanced-no-mask")); dialog.Close(false); await pending;
        }
        finally { w.Close(); }
    }
}
