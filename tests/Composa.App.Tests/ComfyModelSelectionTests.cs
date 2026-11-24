using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;

namespace Composa.App.Tests;

public class ComfyModelSelectionTests
{
    private static string Engines => Path.Combine(AppContext.BaseDirectory, "ai", "engines");
    private static readonly string Lan = "http://192.168.1.50:8188";

    [Theory]
    [InlineData("flux.safetensors", "shared/flux.safetensors", "shared/flux.safetensors")]
    [InlineData("shared\\flux.safetensors", "shared/flux.safetensors", "shared/flux.safetensors")]
    [InlineData("FLUX.SAFETENSORS", "shared/flux.safetensors", "shared/flux.safetensors")]
    [InlineData("old/flux.safetensors", "new/flux.safetensors", null)]
    [InlineData("flux.safetensors", "shared/other-flux.safetensors", null)]
    public void Paths_use_server_identifiers_and_never_substitute_a_different_model(string requested, string available, string? expected) =>
        Assert.Equal(expected, WorkflowModels.Resolve(requested, [available]));

    [Fact]
    public void Ambiguous_filenames_require_manual_choice()
    {
        string[] choices = ["a/flux.safetensors", "b/flux.safetensors"];
        Assert.Null(WorkflowModels.Resolve("flux.safetensors", choices));
        Assert.Equal(choices[1], WorkflowModels.Resolve(choices[1], choices));
    }

    [Fact]
    public void Shared_loaders_across_tasks_have_one_slot_but_different_loaders_do_not_collide()
    {
        var catalog = new EngineCatalog(Engines);
        var engine = Assert.Single(catalog.Profiles, profile => profile.Id == "flux2-klein-intel-xpu");
        var slots = catalog.ModelSlots(engine);
        Assert.Equal(5, slots.Count);
        Assert.Single(slots, slot => slot.Kind == EngineAssetKind.DiffusionModel);
        Assert.Single(slots, slot => slot.Kind == EngineAssetKind.TextEncoder);
        Assert.Equal(3, slots.Count(slot => slot.NodeId == "model"));
        Assert.Equal(slots.Count, slots.Select(slot => slot.Key).Distinct().Count());
    }

    [Fact]
    public void Klein_filename_alias_uses_the_model_title_and_keeps_the_exact_server_key()
    {
        var catalog = new EngineCatalog(Engines);
        var engine = Assert.Single(catalog.Profiles, profile => profile.Id == "flux2-klein-intel-xpu");
        var slot = Assert.Single(catalog.ModelSlots(engine), slot => slot.Kind == EngineAssetKind.DiffusionModel);
        var choices = new Dictionary<string, string> { [slot.Key] = "shared/flux-2-klein-9b-int8-convrot.safetensors" };

        foreach (var task in new[] { AiTaskKind.GenerateImage, AiTaskKind.ImageEdit, AiTaskKind.GenerativeFill })
        {
            var model = catalog.ModelFor(engine, task, choices);
            Assert.Equal("FLUX 2 Image Klein 9B", model.Name);
            Assert.Equal(choices[slot.Key], model.Identifier);
            Assert.Equal("server:" + choices[slot.Key], model.ModelKey);
        }

        choices[slot.Key] = "shared/flux-2-klein-4b-fp8.safetensors";
        Assert.Equal("flux-2-klein-4b-fp8", catalog.ModelFor(engine, AiTaskKind.GenerateImage, choices).Name);
    }

    [Fact]
    public void Settings_round_trip_preserves_subfolders_and_isolates_normalized_servers()
    {
        var settings = new Settings();
        settings.SetComfyModels(Lan + "/", new Dictionary<string, string> { ["slot"] = "shared/flux.safetensors" });
        settings.SetComfyModels("http://192.168.1.51:8188", new Dictionary<string, string> { ["slot"] = "other/flux.safetensors" });
        var loaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("shared/flux.safetensors", loaded.ComfyModelsFor(Lan)["slot"]);
        Assert.Equal("other/flux.safetensors", loaded.ComfyModelsFor("http://192.168.1.51:8188/")["slot"]);
        Assert.Empty(loaded.ComfyModelsFor("http://127.0.0.1:8188"));
    }

    [Fact]
    public void Legacy_upscaler_migrates_only_to_the_configured_endpoint_without_overwriting_choices()
    {
        var settings = new Settings { AiUpscalerModel = "4x_NMKD-Siax_200k.pth" };
        var catalog = new EngineCatalog(Engines);
        var slots = catalog.ModelSlots(Assert.Single(catalog.Profiles, profile => profile.Id == "flux2-klein-intel-xpu"));
        var slot = Assert.Single(slots, slot => slot.Kind == EngineAssetKind.Upscaler);
        settings.MigrateComfyUpscaler(Lan, slots);
        Assert.Equal("4x_NMKD-Siax_200k.pth", settings.ComfyModelsFor(Lan)[slot.Key]);
        Assert.Empty(settings.ComfyModelsFor("http://192.168.1.51:8188"));
        settings.SetComfyModels(Lan, new Dictionary<string, string> { [slot.Key] = "shared/upscaler.pth" });
        settings.MigrateComfyUpscaler(Lan, slots);
        Assert.Equal("shared/upscaler.pth", settings.ComfyModelsFor(Lan)[slot.Key]);
    }

    [Theory]
    [InlineData("shared/flux-2-klein-9b_int8_convrot.safetensors")]
    [InlineData("shared/flux-2-klein-9b-int8-convrot.safetensors")]
    public async Task Subfolder_models_satisfy_the_pack_without_any_client_files(string model)
    {
        using var handler = new Server();
        handler.Objects["UNETLoader"]!["input"]!["required"]!["unet_name"] = new JsonArray(new JsonArray(model), new JsonObject());
        using var http = new HttpClient(handler);
        var service = Service(http);
        var compatibility = await service.TestConnectionAsync(TestContext.Current.CancellationToken);
        Assert.True(compatibility!.IsCompatible, string.Join(", ", compatibility.Missing));
        Assert.Equal(Lan, service.ConnectedServerUrl);
        Assert.All(handler.Requests, request => Assert.Equal("192.168.1.50", request.Host));
        Assert.Contains(model, service.ServerCapabilities!.ModelChoices["UNETLoader.unet_name"]);
    }

    [Fact]
    public async Task Manual_replacements_update_compatibility_and_the_submitted_graph()
    {
        using var handler = new Server();
        handler.Objects["UNETLoader"]!["input"]!["required"]!["unet_name"] = new JsonArray(new JsonArray("shared/my-klein.safetensors"), new JsonObject());
        using var http = new HttpClient(handler);
        var settings = new Settings();
        var service = Service(http);
        service.ModelSelections = settings.ComfyModelsFor;
        var engine = service.SelectedEngine!;
        var slot = Assert.Single(service.Engines.ModelSlots(engine), slot => slot.Kind == EngineAssetKind.DiffusionModel);
        var before = await service.TestConnectionAsync(TestContext.Current.CancellationToken);
        Assert.False(before!.IsCompatible);
        settings.SetComfyModels(Lan, new Dictionary<string, string> { [slot.Key] = "shared/my-klein.safetensors" });
        Assert.True(service.Compatibility(engine, settings.ComfyModelsFor(Lan)).IsCompatible);
        var binding = engine.Binding(AiTaskKind.GenerateImage)!;
        var graph = service.Engines.ReadWorkflow(engine, engine.Workflow(binding.Workflow));
        WorkflowModels.ApplyChoices(graph, engine.Id, settings.ComfyModelsFor(Lan));
        graph = WorkflowBinder.Bind(graph, binding, new Dictionary<string, object?> { ["prompt"] = "red cube", ["seed"] = 3 });
        WorkflowModels.ResolvePaths(graph, service.ServerCapabilities!);
        Assert.True(EngineCompatibility.CheckWorkflow(graph, service.ServerCapabilities!).IsCompatible);
        using var client = new ComfyClient(Lan, http);
        await client.SubmitAsync(graph, Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal("shared/my-klein.safetensors", handler.Submitted!["model"]!["inputs"]!["unet_name"]!.GetValue<string>());
        Assert.Equal("shared/qwen_3_8b_fp8mixed.safetensors", handler.Submitted["clip"]!["inputs"]!["clip_name"]!.GetValue<string>());
        Assert.Equal("red cube", handler.Submitted["prompt"]!["inputs"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Explicit_script_model_overrides_the_saved_upscaler()
    {
        using var handler = new Server();
        using var http = new HttpClient(handler);
        var service = Service(http);
        await service.TestConnectionAsync(TestContext.Current.CancellationToken);
        var engine = service.SelectedEngine!;
        var binding = engine.Binding(AiTaskKind.Upscale)!;
        var graph = service.Engines.ReadWorkflow(engine, engine.Workflow(binding.Workflow));
        var slot = Assert.Single(WorkflowModels.Slots(graph, engine.Id));
        WorkflowModels.ApplyChoices(graph, engine.Id, new Dictionary<string, string> { [slot.Key] = "shared/4x-UltraSharpV2.safetensors" });
        graph = WorkflowBinder.Bind(graph, binding, new Dictionary<string, object?> { ["upscaleModel"] = "4x_NMKD-Siax_200k.pth" });
        WorkflowModels.ResolvePaths(graph, service.ServerCapabilities!);
        Assert.Equal("4x_NMKD-Siax_200k.pth", graph["model"]!["inputs"]!["model_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_saved_model_fails_before_upload_or_generation_and_does_not_edit()
    {
        using var handler = new Server();
        using var http = new HttpClient(handler);
        var service = Service(http);
        var slot = Assert.Single(service.Engines.ModelSlots(service.SelectedEngine!), slot => slot.Kind == EngineAssetKind.Upscaler);
        service.ModelSelections = _ => new Dictionary<string, string> { [slot.Key] = "missing/upscale.pth" };
        var session = EditorSession.NewCanvas(40, 30);
        var history = session.History.Count;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new EditorCommandService(session),
            new AiTaskRequest { Task = AiTaskKind.Upscale }, TestContext.Current.CancellationToken));
        Assert.Contains("missing/upscale.pth", error.Message);
        Assert.Contains("ComfyUI Settings", error.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/upload/image" || request.Path == "/prompt");
        Assert.Equal(history, session.History.Count);
        Assert.Equal((40, 30), (session.Document.Width, session.Document.Height));
        Assert.Equal(AiOperationStatus.Failed, service.Operation!.Status);
    }

    [Fact]
    public async Task Failed_refresh_clears_previous_server_capabilities()
    {
        using var handler = new Server();
        using var http = new HttpClient(handler);
        var service = Service(http);
        await service.TestConnectionAsync(TestContext.Current.CancellationToken);
        handler.Fail = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TestConnectionAsync(TestContext.Current.CancellationToken,
            "http://192.168.1.51:8188"));
        Assert.Contains("192.168.1.51", error.Message);
        Assert.Null(service.ConnectedServerUrl);
        Assert.Null(service.ServerCapabilities);
        Assert.Null(service.ServerInfo);
        Assert.Equal(ComfyConnectionState.Error, service.ConnectionState);
    }

    [Fact]
    public async Task Late_response_from_previous_server_cannot_replace_new_connection()
    {
        using var handler = new Server();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeSend = async (request, cancellation) =>
        {
            if (request.RequestUri!.Host == "192.168.1.50" && request.RequestUri.AbsolutePath == "/object_info")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellation);
            }
        };
        using var http = new HttpClient(handler);
        var service = Service(http);
        var first = service.TestConnectionAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.TestConnectionAsync(TestContext.Current.CancellationToken, "http://192.168.1.51:8188");
        }
        finally { release.TrySetResult(); }
        Assert.Null(await first);
        Assert.Equal("http://192.168.1.51:8188", service.ConnectedServerUrl);
        Assert.Equal(ComfyConnectionState.Connected, service.ConnectionState);
    }

    [AvaloniaFact]
    public async Task Complete_settings_dialog_fits_window_and_has_scrollable_model_choices()
    {
        using var handler = new Server();
        using var http = new HttpClient(handler);
        var service = Service(http);
        await service.TestConnectionAsync(TestContext.Current.CancellationToken);
        var settings = new Settings { ComfyServerUrl = Lan };
        var owner = new Window { Width = 800, Height = 740 };
        owner.Show();
        var task = AiDialogs.SettingsDialog(owner, settings, service);
        var dialog = Assert.Single(owner.OwnedWindows);
        try
        {
            Dispatcher.UIThread.RunJobs();
            var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            var availableHeight = (screen?.WorkingArea.Height ?? 960) / (screen?.Scaling ?? 1);
            Assert.Equal(Math.Min(1000, availableHeight - 54), dialog.Bounds.Height, precision: 1);
            var tabs = Assert.Single(dialog.GetVisualDescendants().OfType<StudioSettingsPages>());
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            Assert.Single(dialog.GetVisualDescendants().OfType<ComfyModelPicker>());
            Assert.True(Screenshots.Save(dialog, "comfy-complete-settings"));
            tabs.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(), box => box.Name == "WorkflowJson");
            Assert.True(Screenshots.Save(dialog, "workflow-settings"));
        }
        finally { dialog.Close(false); owner.Close(); }
        Assert.False(await task);
        Assert.Empty(settings.ComfyModelSelections);
    }

    [Fact]
    public void A_new_checkpoint_does_not_invalidate_the_previously_assigned_workflow()
    {
        var service = new AiTaskService(() => Lan, Engines);
        var pack = service.Engines.Find("flux2-klein-intel-xpu")!;
        const AiTaskKind task = AiTaskKind.GenerativeFill;
        var graph = service.Engines.ReadWorkflow(pack, EngineCatalog.WorkflowFor(pack, task));
        var slot = Assert.Single(WorkflowModels.Slots(graph, pack.Id), item => item.Kind == EngineAssetKind.DiffusionModel);
        const string alternate = "local/flux-2-klein-9b-fp8.safetensors";
        Assert.Equal(alternate, WorkflowModels.ResolveSlot(slot, slot.Default, [alternate, "flux-2-klein-4b-fp8.safetensors"]));
        Assert.Null(WorkflowModels.ResolveSlot(slot, slot.Default, [alternate, "flux-2-klein-9b-fp16.safetensors"]));
        service.EngineIdForTask = _ => pack.Id;
        service.ModelKeyForTask = _ => service.Engines.ModelFor(pack, task, new Dictionary<string, string>()).ModelKey;
        service.ModelSelections = _ => new Dictionary<string, string> { [slot.Key] = alternate };
        Assert.Same(pack, service.EngineFor(task));
        WorkflowModels.ApplyChoices(graph, pack.Id, service.ModelSelections(Lan));
        Assert.Equal(alternate, graph[slot.NodeId]!["inputs"]![slot.Input]!.GetValue<string>());
    }

    [AvaloniaFact]
    public void Workflow_editor_preserves_the_previous_graph_on_error_and_restores_the_factory_route()
    {
        var draft = new WorkflowPreferences();
        var page = new WorkflowSettingsPage(draft, new EngineCatalog(Engines), () => new Dictionary<string, string>(), () => null);
        var window = new Window { Content = new ScrollViewer { Content = page.Content }, Width = 650, Height = 500 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var chooser = page.Content.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "WorkflowPicker");
            var json = page.Content.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "WorkflowJson");
            var status = page.Content.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "WorkflowStatus");
            const AiTaskKind task = AiTaskKind.GenerateImage;
            var id = WorkflowPreferences.FactoryId("flux2-klein-intel-xpu", task);
            chooser.SelectedItem = chooser.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, id));
            var factoryJson = json.Text!;
            var graph = EditableWorkflows.Parse(factoryJson);
            graph["sampler"]!["inputs"]!["steps"] = 12;
            json.Text = graph.ToJsonString();
            Click("Save workflow");
            var saved = draft.Workflows[id];
            Assert.Equal(12, saved.Graph["sampler"]!["inputs"]!["steps"]!.GetValue<int>());
            json.Text = "{ invalid";
            Click("Save workflow");
            Assert.Same(saved, draft.Workflows[id]);
            Assert.Contains("Fix the JSON syntax", status.Text);

            json.Text = graph.ToJsonString();
            Click("Duplicate");
            Assert.Equal(2, draft.Workflows.Count);
            Assert.Contains(draft.Workflows.Values, workflow => workflow.Name.EndsWith("Copy", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => page.Import("Layout", """{"nodes": []}"""));
            Assert.Equal(2, draft.Workflows.Count);
            page.Import("Imported", factoryJson);
            Assert.Equal(3, draft.Workflows.Count);
            var fill = page.Content.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "WorkflowForGenerativeFill");
            fill.SelectedItem = fill.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Content, "Imported"));
            Assert.Empty(draft.Assignments);
            Assert.Contains("Bind Prompt", status.Text);
            page.Content.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "WorkflowBindingNode").Text = "prompt";
            page.Content.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "WorkflowBindingInput").Text = "text";
            Click("Add binding");
            Click("Save workflow");
            var generate = page.Content.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "WorkflowForGenerateImage");
            generate.SelectedItem = generate.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Content, "Imported"));
            Assert.Equal("Imported", draft.Active(task, "flux2-klein-intel-xpu")!.Name);
            Click("Remove binding");
            Click("Save workflow");
            Assert.Contains("Bind Prompt", status.Text);
            Assert.Contains("prompt", draft.Active(task, "flux2-klein-intel-xpu")!.Bindings.Keys);
            generate.SelectedIndex = 0;

            chooser.SelectedItem = chooser.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, id));
            Click("Restore factory");
            Assert.Null(draft.Active(task, "flux2-klein-intel-xpu"));
            Assert.Equal(factoryJson, json.Text);
        }
        finally { window.Close(); }

        void Click(string label) => page.Content.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact]
    public void Editing_a_shared_workflow_from_one_operation_preserves_the_other_operations_graph()
    {
        var catalog = new EngineCatalog(Engines);
        var engine = catalog.Find("flux2-klein-intel-xpu")!;
        var shared = EditableWorkflows.Factory(catalog, engine, AiTaskKind.ImageEdit);
        var draft = new WorkflowPreferences
        {
            Workflows = new() { ["shared"] = shared },
            Assignments = new() { [AiTaskKind.ImageEdit] = "shared", [AiTaskKind.GenerateImage] = "shared" }
        };
        var page = new WorkflowSettingsPage(draft, catalog, () => new Dictionary<string, string>(), () => null);
        var window = new Window { Content = page.Content };
        window.Show();
        try
        {
            page.Open("shared", AiTaskKind.ImageEdit);
            Dispatcher.UIThread.RunJobs();
            page.Content.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "WorkflowName").Text = "Image editing only";
            page.Content.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveWorkflow")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("shared", draft.Assignments[AiTaskKind.GenerateImage]);
            Assert.Same(shared, draft.Active(AiTaskKind.GenerateImage, engine.Id));
            Assert.Equal("Image editing only", draft.Active(AiTaskKind.ImageEdit, engine.Id)?.Name);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Picker_saves_only_on_accept_and_never_shows_another_servers_model_list()
    {
        using var handler = new Server();
        handler.Objects["UNETLoader"]!["input"]!["required"]!["unet_name"] = new JsonArray(
            new JsonArray("shared/flux-2-klein-9b_int8_convrot.safetensors", "shared/my-klein.safetensors", "flux-2-klein-4b-fp8.safetensors"),
            new JsonObject());
        using var http = new HttpClient(handler);
        var service = Service(http);
        await service.TestConnectionAsync(TestContext.Current.CancellationToken);
        var settings = new Settings();
        var address = Lan;
        var picker = new ComfyModelPicker(settings, service, () => address);
        var window = new DialogWindow("ComfyUI models", new ScrollViewer { Content = picker, MaxHeight = 620 });
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var combos = picker.GetLogicalDescendants().OfType<ComboBox>().ToArray();
            Assert.Equal(service.Engines.ModelSlots(service.SelectedEngine!).Count, combos.Length);
            Assert.All(combos.Take(5), combo => Assert.True(combo.IsEnabled));
            Assert.Equal("shared/flux-2-klein-9b_int8_convrot.safetensors", combos[0].SelectedItem?.ToString());
            Assert.DoesNotContain(combos[0].Items, item => item?.ToString() == "flux-2-klein-4b-fp8.safetensors");
            var selected = combos[0].Items.Cast<object>().Single(item => item.ToString() == "shared/my-klein.safetensors");
            combos[0].SelectedItem = selected;
            Assert.Empty(settings.ComfyModelSelections); // Closing without Save is Cancel.
            Assert.Contains("shared/my-klein.safetensors", picker.Choices(Lan).Values);
            Assert.True(Screenshots.Save(window, "comfy-server-model-picker"));
            address = "http://192.168.1.51:8188";
            picker.Refresh();
            Assert.All(picker.GetLogicalDescendants().OfType<ComboBox>(), combo => Assert.False(combo.IsEnabled));
            Assert.Empty(picker.Choices(address));
            picker.Save();
            Assert.Contains("shared/my-klein.safetensors", settings.ComfyModelsFor(Lan).Values);
            Assert.Empty(settings.ComfyModelsFor(address));
        }
        finally { window.Close(); }
    }

    private static AiTaskService Service(HttpClient http) => new(() => Lan, Engines, url => new ComfyClient(url, http));

    [AvaloniaFact]
    public async Task Object_tool_lists_installed_BiRefNet_variants_and_routes_the_chosen_server_identifier()
    {
        using var handler = new Server();
        handler.Objects["LoadBackgroundRemovalModel"]!["input"]!["required"]!["bg_removal_name"] =
            new JsonArray(new JsonArray("birefnet.safetensors", "shared/birefnet-hr.safetensors", "birefnet-matting.safetensors", "isnet-general.safetensors"), new JsonObject());
        using var http = new HttpClient(handler);
        var window = new MainWindow(null, url => new ComfyClient(url, http)); window.Settings.CheckForUpdates = false;
        window.Settings.ComfyServerUrl = Lan; window.Show();
        window.AddSession(EditorSession.NewCanvas(600, 400)); window.SelectTool(Tool.ObjectSelectionAi);
        await window.AiTasks.TestConnectionAsync(TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs();
        ComboBox Menu() => window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "ObjectSelectionModel");
        Assert.Contains("birefnet.safetensors · ComfyUI", Menu().Items.Cast<string>());
        Assert.Contains("shared/birefnet-hr.safetensors · ComfyUI", Menu().Items.Cast<string>());
        Assert.Contains("birefnet-matting.safetensors · ComfyUI", Menu().Items.Cast<string>());
        Assert.Equal("Automatic · Local", Menu().SelectedItem);
        Assert.Contains("Quick outline · Local", Menu().Items.Cast<string>());
        Assert.Contains("rembg · CLI", Menu().Items.Cast<string>());
        Assert.Contains("BiRefNet · fal API", Menu().Items.Cast<string>());
        Assert.Equal(7, Menu().Items.Count);
        Assert.DoesNotContain(Menu().Items.Cast<string>(), name => name.Contains("isnet", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ComfyUI", Menu().Items.Cast<string>());
        Menu().SelectedItem = "shared/birefnet-hr.safetensors · ComfyUI";
        Assert.Equal(ObjectSelectionSource.ComfyUI, window.Settings.ObjectSelectionModel);
        var engine = window.AiTasks.EngineFor(AiTaskKind.ObjectSelection)!;
        var graph = window.AiTasks.Engines.ReadWorkflow(engine, engine.Workflow(engine.Binding(AiTaskKind.ObjectSelection)!.Workflow));
        WorkflowModels.ApplyChoices(graph, engine.Id, window.Settings.ComfyModelsFor(Lan));
        Assert.Equal("shared/birefnet-hr.safetensors", graph["model"]!["inputs"]!["bg_removal_name"]!.GetValue<string>());
        Menu().SelectedItem = "rembg · CLI";
        Assert.Equal(ImageGenerationRoute.LocalCodex, window.Settings.ImageRouteFor(AiTaskKind.SelectSubject));
        Assert.Equal("shared/birefnet-hr.safetensors", window.Settings.ObjectSelectionComfyModel);
        Menu().SelectedItem = "shared/birefnet-hr.safetensors · ComfyUI";
        Screenshots.Save(window, "object-model-variants");
        window.Settings.ComfyServerUrl = "http://192.168.1.51:8188";
        window.SelectTool(Tool.Wand); window.SelectTool(Tool.ObjectSelectionAi);
        Assert.DoesNotContain(Menu().Items.Cast<string>(), name => name.Contains("ComfyUI"));
        Assert.Empty(window.Settings.ComfyModelsFor(window.Settings.ComfyServerUrl));
        window.Close();
    }

    private sealed class Server : HttpMessageHandler
    {
        public JsonObject Objects { get; } = new();
        public List<(string Host, string Path)> Requests { get; } = [];
        public JsonObject? Submitted { get; private set; }
        public bool Fail { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeSend { get; set; }

        public Server()
        {
            var catalog = new EngineCatalog(Engines);
            var engine = Assert.Single(catalog.Profiles, profile => profile.Id == "flux2-klein-intel-xpu");
            foreach (var workflow in engine.Workflows)
            {
                var graph = catalog.ReadWorkflow(engine, workflow);
                foreach (var (_, node) in graph)
                    if (node?["class_type"]?.GetValue<string>() is { } type && !Objects.ContainsKey(type)) Objects[type] = new JsonObject();
                foreach (var slot in WorkflowModels.Slots(graph, engine.Id))
                {
                    var node = Objects[slot.NodeType]!.AsObject();
                    if (node["input"] == null) node["input"] = new JsonObject { ["required"] = new JsonObject() };
                    var choices = new JsonArray("shared/" + slot.Default);
                    if (slot.Kind == EngineAssetKind.Upscaler) choices.Add("4x_NMKD-Siax_200k.pth");
                    node["input"]!["required"]![slot.Input] = new JsonArray(choices, new JsonObject());
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.Host, request.RequestUri.AbsolutePath));
            if (BeforeSend != null) await BeforeSend(request, cancellationToken);
            if (Fail) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Server offline") };
            string body;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/system_stats": body = """{"system":{"comfyui_version":"0.99"},"devices":[]}"""; break;
                case "/object_info": body = Objects.ToJsonString(); break;
                case "/prompt":
                    Submitted = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["prompt"]!.AsObject();
                    body = """{"prompt_id":"test-prompt"}"""; break;
                default: body = "{}"; break;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
