using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.AI;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;
using System.Text.Json.Nodes;

namespace Composa.App.Tests;

public class GenerationDockTests
{
    [AvaloniaFact]
    public void The_last_AI_panel_interaction_routes_paste_and_hides_the_empty_prompt_hint()
    {
        var oldClipboard = EditorSession.Clipboard;
        using var copied = Composa.Rendering.Pixels.NewColor(24, 16);
        copied.Erase(SKColors.Coral);
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        try
        {
            var session = EditorSession.NewCanvas(500, 350, SKColors.White);
            window.AddSession(session);
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation");
            dock.SetVisible(section, true);
            Pump();
            EditorSession.Clipboard = new ClipboardImage(copied, default);
            var prompt = section.Content.GetVisualDescendants().OfType<AiPromptField>().Single();
            var hint = prompt.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "AiPromptPlaceholder");
            void Click(Point point)
            {
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Pump();
            }
            void Paste()
            {
                window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
                Pump();
            }

            Assert.True(hint.IsVisible);
            Click(prompt.TranslatePoint(new Point(10, 10), window)!.Value);
            Assert.False(hint.IsVisible);
            window.KeyTextInput("A long prompt");
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            Pump();
            Assert.Empty(prompt.Text ?? "");
            Assert.False(hint.IsVisible);
            Paste();
            Assert.Equal(1, window.AiReferenceCount);
            Assert.Single(session.Document.Layers);

            Click(window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(20, 20)), window)!.Value);
            Assert.True(hint.IsVisible);
            Paste();
            Assert.Equal(1, window.AiReferenceCount);
            Assert.Equal(2, session.Document.Layers.Count);
        }
        finally { EditorSession.Clipboard = oldClipboard; window.Close(); }
    }

    [AvaloniaFact]
    public void Reference_suggestions_overlay_the_dock_without_moving_its_controls()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Settings.CheckForUpdates = false;
        window.Show();
        try
        {
            window.AddSession(EditorSession.NewCanvas(400, 300));
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var generation = dock.Section("AI Generation");
            dock.SetVisible(generation, true);
            var reference = new SKBitmap(20, 15);
            reference.Erase(SKColors.Coral);
            window.AddAiReferenceForTests(reference);
            Pump();

            var prompt = generation.Content.GetVisualDescendants().OfType<AiPromptField>().Single();
            var run = generation.Content.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AiRun");
            prompt.Focus();
            window.KeyTextInput("Use ");
            Pump();
            var panelSize = generation.Content.Bounds.Size;
            var runPosition = run.TranslatePoint(default, window);
            Assert.NotNull(runPosition);

            window.KeyTextInput("@");
            Pump();

            var popup = Assert.Single(window.GetVisualDescendants().OfType<OverlayPopupHost>());
            var suggestion = Assert.Single(popup.GetVisualDescendants().OfType<TextBlock>());
            Assert.Equal("image 1", suggestion.Text);
            Assert.Equal(panelSize, generation.Content.Bounds.Size);
            Assert.Equal(runPosition, run.TranslatePoint(default, window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Floating_selection_selects_fill_and_submits_a_native_mask_until_image_edit_is_chosen()
    {
        var capabilities = PartnerImageTests.Capabilities();
        var server = new PartnerImageTests.Connection
        {
            Server = capabilities with { NodeTypes = capabilities.NodeTypes.Append("ImageToMask").ToHashSet() }
        };
        var window = new MainWindow(null, _ => server) { Width = 1120, Height = 760 };
        window.Settings.ComfyServerUrl = server.Address.ToString();
        window.AiTasks.ApiKey = () => "test-only";
        foreach (var task in new[] { AiTaskKind.GenerativeFill, AiTaskKind.ImageEdit })
            window.Settings.AiTaskEngineIds[task.ToString()] = "chatgpt-image-2.5";
        var session = EditorSession.NewCanvas(160, 120, SKColors.White);
        window.Show();
        try
        {
            window.AddSession(session);
            window.AiTasks.SetConnectedForTests(server.Server);
            Pump();
            Assert.False(window.AiFloatingVisible);
            var selection = new SKRect(40, 30, 105, 90);
            session.SelectRect(selection);
            Pump();
            var panel = window.AiFloatingPanel;
            Assert.True(window.AiFloatingVisible);
            var operation = Assert.Single(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "AiFloatingOperation");
            var prompt = Assert.Single(panel.GetVisualDescendants().OfType<TextBox>(), t => t.AcceptsReturn);
            var run = Assert.Single(panel.GetVisualDescendants().OfType<Button>(), b => b.Name == "AiRun");
            async Task Run()
            {
                prompt.Text = "Add a blue patch.";
                Assert.True(run.IsEffectivelyEnabled);
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var cutoff = DateTime.UtcNow.AddSeconds(15);
                while (window.AiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running && DateTime.UtcNow < cutoff)
                { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
                Assert.True(window.AiTasks.Operation?.Status == AiOperationStatus.Completed, window.AiTasks.Operation?.Error);
            }

            Assert.Equal(Locale.T("Generative Fill"), operation.Text);
            await Run();
            var fill = Assert.Single(server.Graphs);
            Assert.Equal("composa_api_mask", fill["gpt"]!["inputs"]!["model.mask"]![0]!.GetValue<string>());
            Assert.Equal(new[] { "apiSource", "apiMask" }, server.Uploads);
            Assert.Equal("AI Generative Fill", session.History.UndoName);
            Assert.NotNull(session.ActiveLayer!.Mask);

            var more = Assert.Single(panel.GetVisualDescendants().OfType<Button>(), b => b.Name == "AiMore");
            var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            menu.ShowAt(more); Pump();
            var manual = Assert.Single(menu.Items.OfType<MenuItem>(), i => Equals(i.Header, Locale.T("Image Edit")));
            Assert.True(manual.IsEnabled);
            manual.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Hide(); Pump();
            Assert.Equal(Locale.T("Image Edit"), operation.Text);
            await Run();
            Assert.Equal(2, server.Graphs.Count);
            Assert.Null(server.Graphs[1]["gpt"]!["inputs"]!["model.mask"]);
            Assert.Equal("AI Image Edit", session.History.UndoName);
            Assert.Null(session.ActiveLayer!.Mask);

            session.Deselect(); Pump();
            Assert.False(window.AiFloatingVisible);
            session.SelectRect(selection); Pump();
            Assert.Equal(Locale.T("Generative Fill"), operation.Text);
        }
        finally { session.MarkSaved("floating-selection.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Editor_runs_a_different_route_without_changing_the_cli_default_in_operations()
    {
        const AiTaskKind task = AiTaskKind.GenerateImage;
        const string pack = "flux2-klein-intel-xpu";
        using var capture = new AiVariantsTests.PromptCapture();
        var server = new AiVariantsTests.Connection { Transport = capture };
        var folder = Directory.CreateTempSubdirectory("composa-route-choice-");
        var previousKey = Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY");
        Environment.SetEnvironmentVariable("COMPOSA_IMAGE_API_KEY", "test-route-key");
        var window = new MainWindow(null, _ => server) { Width = 1250, Height = 900 };
        try
        {
            var settings = window.Settings;
            settings.ComfyServerUrl = server.Address.ToString();
            settings.AiTaskEngineIds[task.ToString()] = pack;
            settings.ImageGenerationCodexPath = Path.Combine(folder.FullName, "codex.exe");
            File.WriteAllText(settings.ImageGenerationCodexPath, "");
            settings.ImageGenerationApiUrl = "http://127.0.0.1:1234/v1";
            settings.ImageApiDisplayName = "Images API";
            var scope = DirectImageGenerator.Endpoint(settings.ImageGenerationApiUrl).AbsoluteUri;
            settings.ImageApiCatalogs[scope] = new() { ["api-one"] = "Image provider A", ["api-two"] = "Image provider B" };
            window.Show();
            var canvas = EditorSession.NewCanvas(160, 120);
            window.AddSession(canvas);
            await window.AiTasks.TestConnectionAsync(TestContext.Current.CancellationToken);
            var saving = window.ShowAiSettings(operation: task);
            var dialog = Assert.Single(window.OwnedWindows);
            Pump();
            var defaults = Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(), field => field.Name == "DefaultModel" + task);
            var cli = Assert.Single(defaults.Items.OfType<ImageModelChoice>(), choice => choice.Route == ImageGenerationRoute.LocalCodex);
            defaults.SelectedItem = cli;
            Pump();
            Assert.True(Screenshots.Save(dialog, "studio-settings-operations"));
            dialog.Close(true);
            await saving;

            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var generation = dock.Section("AI Generation");
            dock.SetVisible(generation, true);
            Pump();
            var picker = Assert.Single(generation.Content.GetVisualDescendants().OfType<ComboBox>(), field => field.IsEffectivelyVisible);
            Assert.Equal(ImageGenerationRoute.LocalCodex, Assert.IsType<ImageModelChoice>(picker.SelectedItem).Route);
            Assert.Equal(2, picker.Items.OfType<ImageModelChoice>().Count(choice => choice.Route == ImageGenerationRoute.DirectApi));
            picker.SelectedItem = Assert.Single(picker.Items.OfType<ImageModelChoice>(), choice =>
                choice.Route == ImageGenerationRoute.ComfyWorkflow && choice.Engine.Id == pack);
            await window.RunAiForTests(task, "A ceramic cube");
            Assert.Equal(AiOperationStatus.Completed, window.AiTasks.Operation?.Status);
            Assert.Single(capture.Prompts);
            Assert.Equal(ImageGenerationRoute.LocalCodex, settings.ImageRouteFor(task));
            picker.SelectedItem = Assert.Single(picker.Items.OfType<ImageModelChoice>(), choice => choice.Direct?.Model == "api-two");
            Assert.Equal("api-two", Assert.IsType<ImageModelChoice>(picker.SelectedItem).Direct?.Model);
            Assert.False(settings.ImageOperationApiModels.ContainsKey(task));
            var reopening = window.ShowAiSettings(operation: task);
            dialog = Assert.Single(window.OwnedWindows);
            Pump();
            defaults = Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(), field => field.Name == "DefaultModel" + task);
            Assert.Equal(ImageGenerationRoute.LocalCodex, Assert.IsType<ImageModelChoice>(defaults.SelectedItem).Route);
            dialog.Close(false);
            await reopening;
            canvas.MarkSaved("route-test.cmps", canvas.History.CurrentId);
        }
        finally
        {
            window.Close();
            Environment.SetEnvironmentVariable("COMPOSA_IMAGE_API_KEY", previousKey);
            // The fixture directory contains only the dummy executable.
            File.Delete(Path.Combine(folder.FullName, "codex.exe"));
            folder.Delete();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dock_posts_the_saved_Klein_graph_and_only_explicit_bindings_after_reload(bool imported)
    {
        using var capture = new AiVariantsTests.PromptCapture();
        var server = new AiVariantsTests.Connection { Transport = capture };
        const AiTaskKind task = AiTaskKind.GenerateImage;
        const string engineId = "flux2-klein-intel-xpu";
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var engine = catalog.Find(engineId)!;
        var factory = EditableWorkflows.Factory(catalog, engine, task);
        factory.Graph["sampler"]!["inputs"]!["steps"] = 9;
        factory.Graph["sampler"]!["inputs"]!["sampler_name"] = "heun";
        factory.Graph["sampler"]!["inputs"]!["seed"] = 345;
        factory.Graph["latent"]!["inputs"]!["width"] = 768;
        factory.Graph["extra"] = JsonNode.Parse("""
            {"class_type":"EmptyFlux2LatentImage","inputs":{"width":64,"height":64,"batch_size":1},
             "_meta":{"optionalInput":"custom","unknown":{"value":3}}}
            """);
        var id = imported ? "imported" : WorkflowPreferences.FactoryId(engineId, task);
        var saved = imported ? factory with
        {
            Name = "Imported Klein",
            Graph = EditableWorkflows.Parse(new JsonObject { ["prompt"] = factory.Graph.DeepClone() }.ToJsonString())
        } : factory;
        var preferences = new Settings { AiDefaultsRevision = 1 };
        preferences.AiTaskEngineIds[task.ToString()] = engineId;
        preferences.Workflows.Workflows[id] = saved;
        if (imported) preferences.Workflows.Assignments[task] = id;
        var folder = Directory.CreateTempSubdirectory("composa-workflows-");
        try
        {
            var settingsPath = Path.Combine(folder.FullName, "settings.json");
            await Run(preferences);
            preferences.Workflows.Workflows[id] = saved with { Bindings = new(saved.Bindings) { ["steps"] = new() { NodeId = "sampler", Input = "steps" } } };
            preferences.SetOperation(engineId, task, new() { WorkflowSteps = 13, WorkflowSampler = "ddim", Seed = 123 });
            preferences.SaveTo(settingsPath);
            await Run(Settings.LoadFrom(settingsPath));
            Assert.Equal(2, capture.Prompts.Count);
            var expected = (JsonObject)saved.Graph.DeepClone();
            expected["prompt"]!["inputs"]!["text"] = capture.Prompts[0]["prompt"]!["inputs"]!["text"]!.GetValue<string>();
            Assert.Contains("A ceramic cube", expected["prompt"]!["inputs"]!["text"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(expected, capture.Prompts[0]));
            expected["sampler"]!["inputs"]!["steps"] = 13;
            Assert.True(JsonNode.DeepEquals(expected, capture.Prompts[1]));
        }
        finally { folder.Delete(recursive: true); }

        async Task Run(Settings state)
        {
            var window = new MainWindow(null, _ => server) { Width = 1100, Height = 800 };
            window.Settings.Workflows = state.Workflows;
            window.Settings.AiTaskEngineIds = state.AiTaskEngineIds;
            window.Settings.AiTaskModelKeys.Clear();
            window.Settings.SetOperation(engineId, task, state.OperationFor(engineId, task));
            window.Show();
            var session = EditorSession.NewCanvas(160, 120);
            try
            {
                window.AddSession(session);
                var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
                var section = dock.Section("AI Generation");
                dock.SetVisible(section, true);
                Pump();
                section.Content.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn).Text = "A ceramic cube";
                Pump();
                Assert.Contains(section.Content.GetVisualDescendants().OfType<Button>(), button => button.Name == "AiActiveWorkflow" && button.IsEffectivelyVisible);
                var run = section.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiRun");
                Assert.True(run.IsEffectivelyEnabled);
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var timeout = DateTime.UtcNow.AddSeconds(15);
                while (window.AiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running && DateTime.UtcNow < timeout)
                { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
                Assert.True(window.AiTasks.Operation?.Status == AiOperationStatus.Completed, window.AiTasks.Operation?.Error);
            }
            finally { session.MarkSaved("workflow-run.cmps", session.History.CurrentId); window.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData(AiTaskKind.ChangeBackground, "Change Background", "dock")]
    [InlineData(AiTaskKind.Harmonize, "Harmonize", "main")]
    [InlineData(AiTaskKind.Harmonize, "Harmonize", "quick")]
    [InlineData(AiTaskKind.ChangeBackground, "Change Background", "hidden")]
    [InlineData(AiTaskKind.Harmonize, "Harmonize", "collapsed")]
    [InlineData(AiTaskKind.Harmonize, "Harmonize", "floating")]
    public void Choosing_a_scene_edit_switches_the_shared_editor_without_opening_a_window(AiTaskKind task, string label, string entry)
    {
        var window = new MainWindow { Width = 1100, Height = 800 };
        window.Settings.AiTaskEngineIds[task.ToString()] = "chatgpt-image-2.5";
        window.Show();
        try
        {
            var session = EditorSession.NewCanvas(160, 120, SKColors.White);
            window.AddSession(session);
            window.AiTasks.SetConnectedForTests(PartnerImageTests.Capabilities());
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation");
            var prompt = window.AiFloatingPanel.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            prompt.Text = "A prompt for the previous operation.";
            if (entry == "floating") session.SelectRect(new(30, 25, 90, 70));
            if (entry is not ("hidden" or "floating")) dock.SetVisible(section, true);
            if (entry == "collapsed") dock.SetCollapsed(section, true);
            Pump();

            if (entry is "dock" or "quick" or "floating")
            {
                var selector = entry switch { "dock" => "AiDockOperation", "floating" => "AiMore", _ => "AiTaskMenu" };
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == selector);
                var menu = Assert.IsType<MenuFlyout>(button.Flyout);
                menu.ShowAt(button);
                Pump();
                ClickOperation(menu.Items.OfType<MenuItem>(), label);
                menu.Hide();
            }
            else ClickOperation(AiMenuItems(window), label);

            Pump();
            Assert.Empty(window.OwnedWindows);
            Assert.Null(window.AiTasks.Operation);
            if (entry == "floating")
            {
                Assert.True(window.AiFloatingVisible);
                Assert.False(dock.Presents(section));
                Assert.Equal(Locale.T(label), window.AiFloatingPanel.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "AiFloatingOperation").Text);
                Assert.Equal("chatgpt-image-2.5", Assert.IsType<ImageModelChoice>(window.AiFloatingPanel.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => combo.IsEffectivelyVisible).SelectedItem).Engine.Id);
                return;
            }
            Assert.True(dock.Presents(section));
            Assert.Equal("Choose AI operation · " + label, Operation(section.Content));
            Assert.Same(prompt, section.Content.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn));
            Assert.Equal("", prompt.Text);
            var picker = section.Content.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible);
            var selected = Assert.IsType<ImageModelChoice>(picker.SelectedItem);
            Assert.Equal("chatgpt-image-2.5", selected.Engine.Id);
            if (task == AiTaskKind.ChangeBackground)
            {
                var cost = section.Content.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "AiCreditEstimate");
                var singlePass = PartnerPricing.Estimate(PartnerImageTests.Capabilities(), selected.Engine.ApiModel!, "low", "Custom", 1, 1)!;
                Assert.Equal(new PartnerPriceEstimate(singlePass.MinimumUsd * 2, singlePass.MaximumUsd * 2).RoundedCredits(1),
                    cost.Children.OfType<TextBlock>().Single().Text);
            }

            prompt.Text = "A prompt for this operation.";
            ClickOperation(AiMenuItems(window), label);
            Assert.Equal("A prompt for this operation.", prompt.Text);
            ClickOperation(AiMenuItems(window), "Image Edit");
            Assert.Equal("", prompt.Text);
            ClickOperation(AiMenuItems(window), label);
            Assert.Equal("A prompt for this operation.", prompt.Text);
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Prompt_drafts_follow_their_operations_and_can_be_turned_off_in_settings()
    {
        var window = new MainWindow { Width = 1100, Height = 800 };
        window.Show();
        try
        {
            window.AddSession(EditorSession.NewCanvas(160, 120, SKColors.White));
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation");
            dock.SetVisible(section, true);
            Pump();
            var prompt = section.Content.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            void Choose(string operation) { ClickOperation(AiMenuItems(window), operation); Pump(); }

            prompt.Text = "Soften the shadows.";
            Choose("Generate Image");
            Assert.Equal("", prompt.Text);
            prompt.Text = "A new landscape.";
            Choose("Image Edit");
            Assert.Equal("Soften the shadows.", prompt.Text);
            Choose("Generate Image");
            Assert.Equal("A new landscape.", prompt.Text);
            Choose("Image Edit");
            var serialized = System.Text.Json.JsonSerializer.Serialize(window.Settings);
            Assert.DoesNotContain("AiPromptDrafts", serialized);
            Assert.DoesNotContain("A new landscape.", serialized);
            Assert.DoesNotContain("Soften the shadows.", serialized);
            var reloaded = Settings.FromJson(serialized);
            Assert.True(reloaded.RememberAiPrompts);
            Assert.Empty(reloaded.AiPromptDrafts);

            var settings = AiDialogs.SettingsDialog(window, window.Settings, window.AiTasks, page: 3);
            Pump();
            var dialog = Assert.Single(window.OwnedWindows);
            var remember = dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RememberAiPrompts");
            Assert.True(remember.IsChecked);
            remember.IsChecked = false;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Locale.T("Save settings")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await settings.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(window.Settings.RememberAiPrompts);
            Assert.Empty(window.Settings.AiPromptDrafts);
            Choose("Generate Image");
            Assert.Equal("", prompt.Text);
            prompt.Text = "A temporary prompt.";
            Choose("Image Edit");
            Assert.Equal("", prompt.Text);
            Choose("Generate Image");
            Assert.Equal("", prompt.Text);
            Assert.Empty(window.Settings.AiPromptDrafts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Upscale_keeps_its_scale_dialog()
    {
        var window = new MainWindow { Width = 1100, Height = 800 };
        window.Show();
        try
        {
            window.AddSession(EditorSession.NewCanvas(160, 120, SKColors.White));
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation");
            dock.SetVisible(section, true);
            Pump();
            var operation = Operation(section.Content);

            ClickOperation(AiMenuItems(window), "Upscale");
            Pump();
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Scale");
            Assert.Equal(operation, Operation(section.Content));
            dialog.Close();
            Pump();
            Assert.Null(window.AiTasks.Operation);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
        }
    }

    private static IEnumerable<MenuItem> AiMenuItems(MainWindow window) =>
        window.GetVisualDescendants().OfType<MenuItem>().Single(i => i.Header?.ToString()?.Replace("_", "") == "AI").Items.OfType<MenuItem>();

    [AvaloniaFact]
    public void Ai_choosers_show_only_operations_and_keep_the_run_icon()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            var session = EditorSession.NewCanvas(400, 300, SKColors.White);
            window.AddSession(session);
            window.AiTasks.SetConnectedForTests(PartnerImageTests.Capabilities());
            session.SelectRect(new(40, 40, 160, 160));
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation");
            dock.SetVisible(section, false);
            Pump();

            CheckChooser("AiMore", "Generative Fill", "Image Edit", "Generative Expand");
            CheckChooser("AiTaskMenu", "Generative Fill", "Image Edit", "Generative Expand");
            var run = Assert.Single(window.AiFloatingPanel.GetVisualDescendants().OfType<Button>(), button => button.Name == "AiRun");
            CheckRunIcon(run);
            dock.SetVisible(section, true);
            dock.SetCollapsed(section, false);
            dock.Activate(section);
            Pump();
            CheckChooser("AiDockOperation", "Gen Fill", "Edit Image", "Gen Expand");
            Assert.Contains(run, section.Content.GetVisualDescendants());
            CheckRunIcon(run);

            var aiMenu = window.GetVisualDescendants().OfType<MenuItem>()
                .Single(item => item.Header?.ToString()?.Replace("_", "") == "AI");
            aiMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            var mainMenu = aiMenu.Items.OfType<MenuItem>().ToArray();
            Assert.DoesNotContain(mainMenu, item => Equals(item.Header, "Match to Scene"));
            var relight = Assert.Single(mainMenu, item => Equals(item.Header, "Relight…"));
            Assert.False(relight.IsEnabled);
            relight.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Empty(window.OwnedWindows);
            Assert.Null(window.AiTasks.Operation);
            var presets = Assert.Single(mainMenu, item => Equals(item.Header, "Presets"));
            Assert.False(presets.IsEnabled);
            Assert.Empty(presets.Items);

            void CheckChooser(string name, string fill, string edit, string expand)
            {
                var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Name == name);
                var menu = Assert.IsType<MenuFlyout>(button.Flyout);
                menu.ShowAt(button);
                Pump();
                var items = menu.Items.Select(Assert.IsType<MenuItem>).ToArray();
                Assert.Equal(new[] { "Generate Image", edit, fill, "Remove Object", expand, "Change Background",
                    "Harmonize", "Relight", "Upscale", "Object Selection" }, items.Select(item => item.Header?.ToString()));
                Assert.False(Assert.Single(items, item => Equals(item.Header, "Relight")).IsEnabled);
                menu.Hide();
                Pump();
            }

            static void CheckRunIcon(Button button)
            {
                var glyph = Assert.Single(button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
                Assert.True(glyph.Data!.Bounds.Width > 0);
                Assert.Same(Palette.ActionText, glyph.Fill);
            }
        }
        finally { window.Close(); }
    }

    private static void ClickOperation(IEnumerable<MenuItem> items, string name) =>
        items.Single(i => i.Header?.ToString()?.TrimEnd('…') == name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaTheory]
    [InlineData(AiTaskKind.RemoveObject, "Remove Object", "dock")]
    [InlineData(AiTaskKind.GenerativeExpand, "Gen Expand", "dock")]
    [InlineData(AiTaskKind.RemoveObject, "Remove Object", "main")]
    [InlineData(AiTaskKind.GenerativeExpand, "Generative Expand", "main")]
    [InlineData(AiTaskKind.RemoveObject, "Remove Object", "quick")]
    [InlineData(AiTaskKind.GenerativeExpand, "Generative Expand", "quick")]
    public async Task Choosing_immediate_operation_submits_once_without_another_click(AiTaskKind task, string label, string entry)
    {
        var server = new PartnerImageTests.Connection();
        var window = new MainWindow(null, _ => server) { Width = 1100, Height = 800 };
        window.Settings.ComfyServerUrl = server.Address.ToString();
        window.Settings.AiTaskEngineIds[task.ToString()] = "chatgpt-image-2.5";
        window.AiTasks.ApiKey = () => "test-only";
        window.Show();
        var session = EditorSession.NewCanvas(160, 120, SKColors.White);
        window.AddSession(session);
        session.SelectRect(new(30, 30, 90, 65));
        var before = session.History.CurrentId;
        try
        {
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation"); dock.SetVisible(section, true); Pump();
            MenuItem item; MenuFlyout? menu = null;
            if (entry == "main")
            {
                var top = window.GetVisualDescendants().OfType<MenuItem>().Single(i => i.Header?.ToString()?.Replace("_", "") == "AI");
                item = top.Items.OfType<MenuItem>().Single(i => Equals(i.Header, label));
            }
            else
            {
                var selector = entry == "quick" ? "AiTaskMenu" : "AiDockOperation";
                var chooser = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == selector);
                menu = Assert.IsType<MenuFlyout>(chooser.Flyout);
                menu.ShowAt(chooser); Pump();
                item = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, label));
            }
            Assert.True(item.IsEnabled);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu?.Hide(); Pump();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (window.AiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running && DateTime.UtcNow < deadline)
            { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
            Assert.Empty(window.OwnedWindows);
            Assert.True(window.AiTasks.Operation?.Status == AiOperationStatus.Completed, window.AiTasks.Operation?.Error);
            Assert.Single(server.Graphs);
            Assert.Equal(new[] { "apiSource" }, server.Uploads);
            Assert.NotEqual(before, session.History.CurrentId);
            session.Undo();
            Assert.Equal(before, session.History.CurrentId);
        }
        finally { session.MarkSaved("direct.cmps", session.History.CurrentId); window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(AiTaskKind.GenerateImage, 1)]
    [InlineData(AiTaskKind.ImageEdit, 2)]
    [InlineData(AiTaskKind.GenerativeFill, 3)]
    public async Task Dock_run_submits_the_prompt_and_correct_source_inputs(AiTaskKind task, int count)
    {
        var server = new PartnerImageTests.Connection();
        var window = new MainWindow(null, _ => server) { Width = 1100, Height = 800 };
        window.Settings.ComfyServerUrl = server.Address.ToString();
        window.AiTasks.ApiKey = () => "test-only";
        window.Settings.AiTaskEngineIds[task.ToString()] = "flux2-klein-intel-xpu";
        window.Settings.AiTaskModelKeys.Remove(task.ToString());
        window.Show();
        var session = EditorSession.NewCanvas(160, 120, task == AiTaskKind.GenerateImage ? null : SKColors.White);
        window.AddSession(session);
        if (task == AiTaskKind.GenerativeFill) session.SelectRect(new(20, 20, 70, 65));
        try
        {
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation"); dock.SetVisible(section, true);
            var reference = new SKBitmap(60, 40); reference.Erase(SKColors.Coral); window.AddAiReferenceForTests(reference); Pump();
            var prompt = section.Content.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            var picker = section.Content.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            picker.SelectedItem = picker.Items.OfType<ImageModelChoice>().Single(model => model.Engine.Id == "chatgpt-image-2.5");
            prompt.Text = "Keep the ceramic texture."; Pump();
            var before = session.History.CurrentId;
            var run = section.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == (count == 1 ? "AiRun" : $"AiVariants{count}"));
            Assert.True(run.IsEffectivelyEnabled);
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var timeout = DateTime.UtcNow.AddSeconds(15);
            while (window.AiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running && DateTime.UtcNow < timeout)
            { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
            Assert.True(window.AiTasks.Operation?.Status == AiOperationStatus.Completed, window.AiTasks.Operation?.Error);
            Assert.Equal(count, server.Graphs.Count);
            Assert.Equal("chatgpt-image-2.5", Assert.IsType<ImageModelChoice>(picker.SelectedItem).Engine.Id);
            var graph = server.Graphs[0];
            Assert.Contains("Keep the ceramic texture.", graph["gpt"]!["inputs"]!["prompt"]!.GetValue<string>());
            Assert.Equal(task == AiTaskKind.GenerateImage ? new[] { "referenceImage1" } : new[] { "apiSource", "referenceImage1" }, server.Uploads);
            Assert.NotEqual(before, session.History.CurrentId);
            session.Undo(); Assert.Equal(before, session.History.CurrentId);
        }
        finally { session.MarkSaved("run.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expansion_runs_for_the_original_tab_and_can_be_cancelled_from_the_panel(bool cancel)
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new PartnerImageTests.Connection
        {
            BeforeResult = async token =>
            {
                requested.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var window = new MainWindow(null, _ => server) { Width = 1200, Height = 840 };
        window.Settings.ComfyServerUrl = server.Address.ToString();
        window.AiTasks.ApiKey = () => "test-only";
        window.Settings.AiTaskEngineIds[nameof(AiTaskKind.GenerativeExpand)] = "chatgpt-image-2.5";
        window.Settings.SetOperation("chatgpt-image-2.5", AiTaskKind.GenerativeExpand, new() { Seed = 2042 });
        var source = EditorSession.NewCanvas(159, 121, SKColors.Orange);
        var other = EditorSession.NewCanvas(131, 107, SKColors.Purple);
        var sourceState = source.History.CurrentId;
        var otherState = other.History.CurrentId;
        source.MarkSaved("expand-source.cmps", sourceState);
        other.MarkSaved("expand-other.cmps", otherState);
        window.Show();
        window.AddSession(other);
        window.AddSession(source);
        try
        {
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation");
            dock.SetVisible(section, true);
            window.SelectTool(Tool.Crop);
            ExpandCrop(source, 23, 17);
            var chooser = section.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiDockOperation");
            var menu = Assert.IsType<MenuFlyout>(chooser.Flyout);
            menu.ShowAt(chooser); Pump();
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Gen Expand"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Hide(); Pump();
            var token = TestContext.Current.CancellationToken;
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(15), token);

            var tab = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "expand-other");
            var at = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            Assert.Same(other, window.Canvas.Session);
            window.Canvas.ResetCropToCanvas();
            ExpandCrop(other, 15, 11);
            var pendingCrop = window.Canvas.CropRect;

            if (cancel)
                window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Locale.T("Cancel"))
                    && b.Parent is StackPanel panel && panel.Children.OfType<ProgressBar>().Any())
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else release.SetResult();

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (window.AiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running && DateTime.UtcNow < deadline)
            { await Task.Delay(10, token); Pump(); }
            Assert.Equal(cancel ? AiOperationStatus.Cancelled : AiOperationStatus.Completed, window.AiTasks.Operation?.Status);
            Assert.Equal(otherState, other.History.CurrentId);
            Assert.Equal(pendingCrop, window.Canvas.CropRect);
            if (!cancel)
            {
                Assert.Equal((182, 138), (source.Document.Width, source.Document.Height));
                Assert.NotEqual(sourceState, source.History.CurrentId);
                source.Undo();
            }
            Assert.Equal(sourceState, source.History.CurrentId);
            Assert.Equal((159, 121), (source.Document.Width, source.Document.Height));
        }
        finally
        {
            release.TrySetResult();
            source.MarkSaved("expand-source.cmps", source.History.CurrentId);
            other.MarkSaved("expand-other.cmps", other.History.CurrentId);
            window.Close();
        }

        void ExpandCrop(EditorSession session, int dx, int dy)
        {
            window.Canvas.Fit(); Pump();
            var frame = Assert.IsType<SKRect>(window.Canvas.CropRect);
            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
            window.MouseDown(At(frame.Right, frame.Bottom), MouseButton.Left);
            window.MouseMove(At(frame.Right + dx, frame.Bottom + dy), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(frame.Right + dx, frame.Bottom + dy), MouseButton.Left);
            Pump();
            Assert.Equal(new SKRect(0, 0, session.Document.Width + dx, session.Document.Height + dy), window.Canvas.CropRect);
        }
    }

    [AvaloniaFact]
    public void Prompt_regions_draw_inside_existing_boxes_and_update_without_panning()
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        var session = EditorSession.NewCanvas(480, 320, SKColors.White);
        var outer = session.AddPromptRegion(new(40, 50, 280, 240))!;
        try
        {
            window.AddSession(session);
            window.SelectTool(Tool.PromptRegion);
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            dock.SetVisible(dock.Section("AI Generation"), true);
            window.Canvas.Fit();
            Pump();
            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new(x, y)), window)!.Value;
            void Click(Point point)
            {
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }
            void Drag(Point from, Point to)
            {
                window.MouseDown(from, MouseButton.Left);
                window.MouseMove(to, RawInputModifiers.LeftMouseButton);
                window.MouseUp(to, MouseButton.Left);
                Pump();
            }
            void ClickButton(string name)
            {
                var button = dock.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
                Assert.IsAssignableFrom<Control>(button.Content);
                Assert.InRange(button.Bounds.Width, 1, 27);
                Click(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                Pump();
            }

            Click(At(40, 50) + new Vector(8, -9));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "RegionPromptInput");
            Drag(At(90, 90), At(160, 150));
            Assert.Equal(outer.Bounds, session.Document.PromptRegions[0].Bounds);
            Assert.Equal(new SKRectI(90, 90, 160, 150), session.Document.PromptRegions[1].Bounds);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump();

            var tag = At(40, 50) + new Vector(8, -9);
            Click(tag);
            Drag(At(280, 240), At(300, 255));
            Assert.Equal(new SKRectI(40, 50, 300, 255), session.Document.PromptRegions[0].Bounds);
            Click(tag);
            Click(tag);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "RegionPromptInput");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Pump();

            var sample = At(135, 50);
            Assert.Contains(dock.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Locale.T("Regions"));
            SKColor RegionPixel(string filename)
            {
                Assert.True(Screenshots.Save(window, filename));
                using var image = SKBitmap.Decode(Path.Combine(Screenshots.Folder, filename + ".png"));
                return image.GetPixel((int)sample.X, (int)sample.Y);
            }
            Assert.Equal(outer.Ink.Color, RegionPixel("ai-region-active"));
            ClickButton("ToggleAllPromptRegions");
            Assert.Equal(SKColors.White, RegionPixel("ai-regions-hidden"));
            Assert.Equal(2, session.Document.PromptRegions.Count);
            ClickButton("ToggleAllPromptRegions");
            Assert.Equal(outer.Ink.Color, RegionPixel("ai-regions-visible"));
            session.ChangePromptRegion(session.Document.PromptRegions[0] with { Enabled = false });
            Assert.Equal(SKColors.White, RegionPixel("ai-region-disabled"));
            ClickButton("DeleteAllPromptRegions");
            Assert.Empty(session.Document.PromptRegions);
            session.Undo();
            Assert.Equal(2, session.Document.PromptRegions.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Draw_prompt_region_edit_its_text_and_undo_without_selecting_pixels()
    {
        var previousLanguage = Locale.Language;
        Locale.Language = "ru";
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        var session = EditorSession.NewCanvas(480, 320, SKColors.Bisque);
        if (Environment.GetEnvironmentVariable("COMPOSA_REGION_IMAGE") is { } path)
        {
            using var input = Composa.IO.ImageFiles.Load(path);
            var pixels = session.Document.Layers[0].Pixels!;
            using var paint = new SKCanvas(pixels);
            paint.DrawImage(Composa.Rendering.Pixels.ImageOf(input), SKRect.Create(pixels.Width, pixels.Height),
                new SKSamplingOptions(SKFilterMode.Linear));
            Composa.Rendering.Pixels.Invalidate(pixels);
        }
        try
        {
            window.AddSession(session);
            window.SelectTool(Tool.PromptRegion);
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            dock.SetVisible(dock.Section("AI Generation"), true);
            window.Canvas.Fit();
            Pump();
            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new(x,y)), window)!.Value;
            window.MouseDown(At(19, 85), MouseButton.Left);
            window.MouseMove(At(249, 234), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(249, 234), MouseButton.Left);
            Pump();
            Assert.Single(session.Document.PromptRegions);
            window.KeyTextInput("Покрой чайник синей глазурью");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump();
            var r = Assert.Single(session.Document.PromptRegions);
            Assert.Equal("Покрой чайник синей глазурью", r.Prompt);
            Assert.Null(session.Selection);
            var text = dock.Section("AI Generation").Content.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Покрой чайник синей глазурью");
            var pos = text.TranslatePoint(new Point(3,3),window)!.Value;
            window.MouseDown(pos,MouseButton.Left); window.MouseUp(pos,MouseButton.Left);
            window.MouseDown(pos,MouseButton.Left); window.MouseUp(pos,MouseButton.Left);
            Pump();
            var input = dock.Section("AI Generation").Content.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "RegionPromptInput");
            input.Text = "Cobalt";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump();
            Assert.Equal("Cobalt", session.Document.PromptRegions.Single().Prompt);
            session.Undo();
            Assert.Equal("Покрой чайник синей глазурью", session.Document.PromptRegions.Single().Prompt);
            window.MouseDown(At(293, 113), MouseButton.Left);
            window.MouseMove(At(451, 232), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(451, 232), MouseButton.Left);
            Pump();
            window.KeyTextInput("Добавь на чашку тонкие зелёные полосы");
            Pump();
            Assert.True(Screenshots.Save(window, "ai-regions-canvas"));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump();
            Assert.Equal("Добавь на чашку тонкие зелёные полосы", session.Document.PromptRegions[1].Prompt);
            window.AiTasks.SetConnectedForTests(PartnerImageTests.Capabilities());
            dock.SetVisible(dock.Section("AI Generation"), false);
            Pump();
            Assert.True(window.AiFloatingVisible);
            Assert.True(Screenshots.Save(window, "ai-regions-floating"));

            window.AiTasks.SelectedEngine = window.AiTasks.Engines.Find(Flux3Workflow.EngineId);
            var settingsDialog = AiDialogs.SettingsDialog(window, window.Settings, window.AiTasks, page: 1);
            Pump();
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.True(Screenshots.Save(dialog, "ai-regions-settings"));
            dialog.Close(false);
            Assert.False(await settingsDialog);
        }
        finally
        {
            session.MarkSaved("regions.cmps", session.History.CurrentId);
            window.Close();
            Locale.Language = previousLanguage;
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static string Operation(Control view) =>
        AutomationProperties.GetName(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiDockOperation"))!;

    [AvaloniaFact]
    public async Task Dock_shows_the_selected_paid_model_and_credit_estimate_in_the_run_row()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(400, 300, SKColors.Gray);
        try
        {
            window.AddSession(session);
            var paid = PartnerImageTests.Capabilities();
            var (_, local) = await new AiVariantsTests.Connection().TestConnectionAsync(TestContext.Current.CancellationToken);
            window.AiTasks.SetConnectedForTests(paid with { NodeTypes = paid.NodeTypes.Concat(local.NodeTypes).ToHashSet() });
            session.SelectRect(new(40, 40, 160, 160));
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation");
            dock.SetVisible(section, true);
            Pump();
            var picker = section.Content.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible);
            var badge = section.Content.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "AiCreditEstimate");
            var models = picker.Items.OfType<ImageModelChoice>().ToArray();
            Assert.DoesNotContain(models, model => model.Name.StartsWith("Nano Banana 2", StringComparison.Ordinal));
            picker.SelectedItem = models.Single(m => m.Engine.Id == "chatgpt-image-2.5");
            section.Content.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn).Text = "Add a small ceramic vase beside the plant.";
            Pump();
            Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.EndsWith(" · Comfy credits", StringComparison.Ordinal) == true);
            Assert.True(badge.IsEffectivelyVisible);
            Assert.Equal("≈10", badge.Children.OfType<TextBlock>().Single().Text);
            var row = badge.TranslatePoint(new Rect(badge.Bounds.Size).Center, window)!.Value;
            var run = section.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiRun");
            Assert.Equal(run.TranslatePoint(new Rect(run.Bounds.Size).Center, window)!.Value.Y, row.Y, 1);
            Assert.True(Screenshots.Save(window, "generation-dock-credits"));
            picker.SelectedItem = models.First(m => !m.Engine.PaidApi);
            Pump();
            Assert.False(badge.IsEffectivelyVisible);
        }
        finally { session.MarkSaved("credit-preview.cmps", session.History.CurrentId); window.Close(); }
    }

    internal static void CheckReferenceSpacing(Control host, Window window)
    {
        var model = host.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
        var thumbnails = host.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible &&
            ToolTip.GetTip(b)?.ToString()?.StartsWith("image ", StringComparison.Ordinal) == true).ToArray();
        var first = thumbnails.First().TranslatePoint(default, window)!.Value;
        var modelBottom = model.TranslatePoint(new Point(0, model.Bounds.Height), window)!.Value;
        Assert.Equal(8, first.Y - modelBottom.Y, 2);
        for (int i = 1; i < thumbnails.Length; i++)
        {
            var previous = thumbnails[i - 1];
            var from = previous.TranslatePoint(default, window)!.Value;
            var to = thumbnails[i].TranslatePoint(default, window)!.Value;
            if (from.Y == to.Y) Assert.Equal(8, to.X - from.X - previous.Bounds.Width, 2);
            else
            {
                Assert.Equal(first.X, to.X, 2);
                Assert.Equal(8, to.Y - from.Y - previous.Bounds.Height, 2);
            }
        }
    }

    [AvaloniaFact]
    public void Dock_follows_document_content_selection_history_and_tab_switches()
    {
        var window = new MainWindow { Width = 1120, Height = 760 }; window.Show();
        var session = EditorSession.NewCanvas(480, 320, null, ColorDepth.Sixteen);
        window.AddSession(session);
        var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
        var section = dock.Section("AI Generation");
        Assert.False(section.State.Visible);
        dock.SetVisible(section, true);
        void Is(string name) { Pump(); Assert.Equal("Choose AI operation · " + name, Operation(section.Content)); }
        try
        {
            Is("Generate Image");
            var chooser = section.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiDockOperation");
            var menu = Assert.IsType<MenuFlyout>(chooser.Flyout);
            menu.ShowAt(chooser); Pump();
            Assert.False(menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Gen Fill")).IsEnabled);
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Edit Image")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Hide(); Is("Edit Image");
            session.SelectRect(new(5, 5, 40, 40)); Is("Gen Fill");
            session.Deselect(); Is("Generate Image");
            session.Fill(SKColors.Orange); Is("Edit Image");
            session.Undo(); Is("Generate Image");
            session.Redo(); Is("Edit Image");
            session.Undo();
            session.AddShape(new(10, 10, 50, 80)); Is("Edit Image");
            session.AddBlankLayer(); Is("Edit Image");
            session.SelectRect(new(3, 4, 150, 80)); Is("Gen Fill");
            session.Deselect(); Is("Edit Image");
            window.AddSession(EditorSession.NewCanvas(64, 64)); Is("Generate Image");
        }
        finally { session.MarkSaved("dock.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Floating_and_docked_editors_keep_the_same_prompt_and_ordered_reference_controls()
    {
        var window = new MainWindow { Width = 1120, Height = 760 }; window.Show();
        var session = EditorSession.NewCanvas(600, 400, SKColors.LightSlateGray);
        window.AddSession(session); window.AiTasks.SetConnectedForTests();
        session.SelectRect(new(130, 85, 270, 260)); Pump();
        var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
        var section = dock.Section("AI Generation");
        TextBox Prompt(Control host) => host.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
        Image[] Thumbnails(Control host) => host.GetVisualDescendants().OfType<Button>()
            .Where(b => ToolTip.GetTip(b)?.ToString()?.StartsWith("image ", StringComparison.Ordinal) == true)
            .Select(b => Assert.Single(Assert.IsType<Grid>(b.Content).Children.OfType<Image>())).ToArray();
        void CheckBadge(Control host)
        {
            var remove = host.GetVisualDescendants().OfType<Button>().First(b => Equals(ToolTip.GetTip(b), "Remove reference"));
            var cell = Assert.IsType<Grid>(remove.Parent);
            var frame = Assert.IsType<Border>(cell.Parent);
            var tile = frame.TranslatePoint(default, window)!.Value;
            var badge = remove.TranslatePoint(default, window)!.Value;
            Assert.Equal(3, badge.Y - tile.Y, 2);
            Assert.Equal(3, tile.X + frame.Bounds.Width - badge.X - remove.Bounds.Width, 2);
            var glyph = Assert.IsAssignableFrom<Control>(remove.Content);
            var center = glyph.TranslatePoint(new Rect(glyph.Bounds.Size).Center, remove)!.Value;
            Assert.InRange(Math.Abs(remove.Bounds.Width / 2 - center.X), 0, 0.5);
            Assert.InRange(Math.Abs(remove.Bounds.Height / 2 - center.Y), 0, 0.5);
            Assert.Equal(new CornerRadius(5), remove.CornerRadius);
            Assert.Equal(new CornerRadius(6), frame.CornerRadius);
            Assert.True(frame.ClipToBounds);
        }
        void Add(SKColor color) { var image = new SKBitmap(20, 15); image.Erase(color); window.AddAiReferenceForTests(image); }
        try
        {
            var prompt = Prompt(window.AiFloatingPanel);
            prompt.Text = "Add a small ceramic vase beside the plant.";
            Add(SKColors.Green); Add(SKColors.Coral);
            var initial = Thumbnails(window.AiFloatingPanel);
            Assert.Equal(2, initial.Length);
            dock.SetVisible(section, true); Pump();
            Assert.False(window.AiFloatingVisible); Assert.Same(prompt, Prompt(section.Content));
            Assert.Equal(initial, Thumbnails(section.Content));
            CheckBadge(section.Content);
            CheckReferenceSpacing(section.Content, window);
            Assert.True(Screenshots.Save(window, "generation-dock-references"));
            prompt.Text = "Keep the green reference.";
            section.Content.GetVisualDescendants().OfType<Button>().Last(b => Equals(ToolTip.GetTip(b), "Remove reference"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var remaining = Thumbnails(section.Content);
            Assert.Same(initial[0].Source, Assert.Single(remaining).Source);
            dock.SetCollapsed(section, true); Pump();
            Assert.True(window.AiFloatingVisible); Assert.Same(prompt, Prompt(window.AiFloatingPanel));
            CheckBadge(window.AiFloatingPanel);
            CheckReferenceSpacing(window.AiFloatingPanel, window);
            Assert.True(Screenshots.Save(window, "generation-floating-references"));
            Assert.Equal("Keep the green reference.", prompt.Text); Assert.Equal(remaining, Thumbnails(window.AiFloatingPanel));
            dock.SetCollapsed(section, false); Pump();
            dock.SetTabbed(true); Pump();
            dock.Activate(dock.Section("History")); Pump();
            Assert.True(window.AiFloatingVisible);
            dock.Activate(section); Pump();
            Assert.False(window.AiFloatingVisible); Assert.Same(prompt, Prompt(section.Content)); Assert.Equal(remaining, Thumbnails(section.Content));
            for (int i = 0; i < 5; i++) Add(SKColors.Blue);
            Pump(); CheckReferenceSpacing(section.Content, window);
            dock.SetVisible(section, false); Pump();
            Assert.True(window.AiFloatingVisible); Assert.Equal("Keep the green reference.", Prompt(window.AiFloatingPanel).Text);
        }
        finally { session.MarkSaved("handoff.cmps", session.History.CurrentId); window.Close(); }
    }
}
