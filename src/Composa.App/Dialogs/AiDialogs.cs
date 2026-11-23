using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Composa.AI;
using Composa.App.AI;
using SkiaSharp;
using Composa.Model;

namespace Composa.App.Dialogs;

public sealed record AiPromptResult(string Prompt, int Width, int Height, long Seed, string? EngineId = null, int Variants = 1);

public static partial class AiDialogs
{
    private static Border Section(string title, params Control[] controls) => new()
    {
        Background = Palette.Panel, CornerRadius = new CornerRadius(5), Padding = new Thickness(10),
        Child = Ui.Column(8, new Control[] { Ui.Label(Locale.T(title), weight: Avalonia.Media.FontWeight.SemiBold) }.Concat(controls).ToArray())
    };
    public static async Task<IReadOnlyList<string>?> LayerTags(Window owner, Layer layer, Settings settings)
    {
        var box = new TextBox { Text = string.Join(", ", layer.Tags.Order()), Width = 390, PlaceholderText = Locale.T("title, product, hero-image") };
        var suggestions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var tag in Composa.Model.LayerTags.Standard.Concat(settings.CustomLayerTags).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var button = Ui.TextButton(tag, () =>
            {
                var values = (box.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (!values.Contains(tag, StringComparer.OrdinalIgnoreCase)) values.Add(tag);
                box.Text = string.Join(", ", values);
                box.CaretIndex = box.Text.Length;
            }, translate: false);
            button.MinWidth = 0;
            button.Margin = new Thickness(0, 0, 5, 5);
            suggestions.Children.Add(button);
        }
        var note = Ui.Label(Locale.T("Tags are optional metadata for templates, scripts and the future Assistant."), Palette.Secondary);
        note.MaxWidth = 390;
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var dialog = new DialogWindow("Layer Tags", Ui.Column(10, box, suggestions, note));
        dialog.Opened += (_, _) => box.Focus();
        if (!await dialog.Ask(owner)) return null;
        var tags = (box.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Composa.Model.LayerTags.Normalize).Where(tag => tag != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var tag in tags.Except(Composa.Model.LayerTags.Standard, StringComparer.OrdinalIgnoreCase))
            if (!settings.CustomLayerTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) settings.CustomLayerTags.Add(tag);
        settings.Save();
        return tags;
    }

    internal static ToggleButton AlignmentToggle(Settings settings)
    {
        var toggle = new ToggleButton { Name = "AiPixelAlignment", Content = Icons.Create(Icons.PixelAlignment, 18), IsChecked = settings.AiAlignMaskedEdits,
            Width = 30, Height = 28, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        toggle.Classes.Add("tool");
        Avalonia.Automation.AutomationProperties.SetName(toggle, "Align pixels");
        Locale.Tip(toggle, "Align pixels · use unchanged image details to correct small shifts before inserting the result.");
        return toggle;
    }

    internal static Button EnhanceButton(TextBox prompt, Settings settings, AiTaskService service, AiTaskKind task, Func<EngineProfile?> engine)
    {
        return EnhanceButton(prompt, settings, service, () => task, engine);
    }

    internal static Button EnhanceButton(TextBox prompt, Settings settings, AiTaskService service, Func<AiTaskKind> operation, Func<EngineProfile?> engine)
    {
        var button = AiGenerationControls.IconAction(Icons.EnhancePrompt, "AiEnhancePrompt", "Enhance prompt", () => { });
        string ProviderHint() => "Enhance prompt · " + AiTaskService.Providers.GetValueOrDefault(settings.EffectiveEnhancerProvider());
        var label = ProviderHint();

        Locale.Tip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        CancellationTokenSource? running = null;
        void RefreshButton() => button.IsEnabled = running != null || prompt.IsEnabled;
        button.DetachedFromVisualTree += (_, _) =>
        {
            if (running is not { } pending) return;
            // A switch between the dock and canvas reattaches this same editor during layout.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(running, pending) && TopLevel.GetTopLevel(button) == null) pending.Cancel();
            });
        };
        prompt.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty || e.Property == InputElement.IsEnabledProperty) RefreshButton(); };
        RefreshButton();
        button.Click += async (_, _) =>
        {
            if (running != null) { running.Cancel(); return; }
            if (string.IsNullOrWhiteSpace(prompt.Text)) { prompt.Focus(); return; }
            if (engine() is not { } selected || prompt.Text is not { Length: > 0 } original) return;
            var task = operation();
            using var request = new CancellationTokenSource();
            running = request;
            var started = System.Diagnostics.Stopwatch.StartNew();
            void Progress()
            {
                var tip = $"Stop waiting · {started.Elapsed:m\\:ss}. A submitted server request may still be billed.";
                Locale.Tip(button, tip);
                Avalonia.Automation.AutomationProperties.SetName(button, tip);
            }
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => Progress();
            AiGenerationControls.ShowBusy(button, true); Progress(); timer.Start(); RefreshButton();
            try
            {
                var enhanced = await service.EnhanceAsync(settings, selected, task, original, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (TopLevel.GetTopLevel(button) != null && prompt.Text == original && engine()?.Id == selected.Id && operation() == task)
                {
                    prompt.SelectAll();
                    prompt.SelectedText = enhanced;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (TopLevel.GetTopLevel(button) is Window window) await Prompts.Alert(window, "Enhance prompt", error.Message);
            }
            finally
            {
                timer.Stop(); running = null; AiGenerationControls.ShowBusy(button, false);
                Locale.Tip(button, ProviderHint());
                Avalonia.Automation.AutomationProperties.SetName(button, ProviderHint());
                RefreshButton();
            }
        };
        return button;
    }

    public static async Task<bool> SettingsDialog(Window owner, Settings settings, AiTaskService service, int page = 0, string? focusField = null)
    {
        var live = service;
        service = live.SettingsPreview();
        using var lifetime = new CancellationTokenSource();
        DialogWindow? settingsDialog = null;
        var showContext = new CheckBox { Content = Locale.T("Show AI context bounds after selection"), IsChecked = settings.AiShowContextBounds };
        var taskDrafts = new Dictionary<string, string>(settings.AiTaskEngineIds);
        var url = new TextBox { Text = settings.ComfyServerUrl, Width = 330 };
        var imageConnection = new ImageConnectionFields(settings, service, () => url.Text ?? "", lifetime.Token);
        var timeout = Ui.Number(settings.ComfyConnectionTimeoutSeconds, 1, 120, _ => { }, 1, "0", 80);
        var keyEnvironment = new TextBox { Text = settings.ComfyApiKeyEnvironment, Width = 330 };
        var sessionKey = new TextBox { PasswordChar = '●', Width = 330, PlaceholderText = Locale.T("Enter a new key to save or replace it") };
        var clearKey = false;
        var clear = Ui.TextButton("Forget saved key", () => { sessionKey.Text = ""; clearKey = true; });
        var keyNote = Ui.Label(Locale.T("Partner Nodes use a Comfy.org API key, not an OpenAI key. Create one at platform.comfy.org. Keys are saved in your user profile for this server. On Windows, Composa uses Credential Manager. The environment variable is also supported. Use HTTPS for servers outside a trusted LAN."), Palette.Secondary);
        keyNote.MaxWidth = 430; keyNote.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var directGenerator = DirectImageGenerator.For(settings, AiTaskKind.GenerateImage);
        var promptProfiles = service.Engines.Profiles.Concat(new[]
        {
            (directGenerator with { Route = ImageGenerationRoute.LocalCodex }).PromptProfile(),
            (directGenerator with { Route = ImageGenerationRoute.DirectApi }).PromptProfile()
        }).ToArray();
        EngineProfile? PromptPack(string id) => promptProfiles.FirstOrDefault(pack => pack.Id == id);
        var promptPack = directGenerator.Route == ImageGenerationRoute.ComfyWorkflow
            ? service.SelectedEngine?.Id ?? promptProfiles[0].Id : directGenerator.Id;
        var instructionDrafts = new Dictionary<string, string>(settings.EnhancerInstructions);
        var instructionTask = AiTaskKind.ImageEdit;
        var instructions = new TextBox { Name = "EnhancerInstructions", AcceptsReturn = true, Height = 104, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = service.InstructionsFor(instructionDrafts, PromptPack(promptPack), instructionTask) };
        var model = new ComboBox { ItemsSource = service.ChatModels.Append(settings.EnhancerModel).Distinct().ToArray(),
            SelectedItem = settings.EnhancerModel, MinWidth = 180 };
        AiGenerationControls.StyleModelField(model, "Prompt enhancer model");
        var tokens = Ui.Number(settings.EnhancerMaxTokens, 16, 16384, _ => { }, 1, "0", 100);
        var effort = new ComboBox { Width = 140 };
        tokens.HorizontalAlignment = HorizontalAlignment.Left;
        var apiUrl = new TextBox { Text = settings.EnhancerApiUrl, MinWidth = 220 };
        var apiModel = new TextBox { Text = settings.EnhancerApiModel, PlaceholderText = Locale.T("Model ID") };
        var selectedProvider = settings.EffectiveEnhancerProvider();
        var serverModels = new Dictionary<string,string>(settings.EnhancerServerModels);
        var remoteCli = AiTaskService.IsRemoteCli(selectedProvider) ? selectedProvider : settings.EnhancerRemoteCli;
        if (!AiTaskService.IsRemoteCli(remoteCli)) remoteCli = "codex";
        var localCodex = new CodexConnectionFields(settings.EnhancerCodexPath, settings.EnhancerCodexModel);
        var serverModel = new TextBox { PlaceholderText = Locale.T("Model ID"), Text = serverModels.GetValueOrDefault(selectedProvider) };
        var serverUrl = new TextBox { Text = settings.EnhancerServerApiUrl };
        var serverHelp = Ui.Label("", Palette.Secondary);
        serverHelp.MaxWidth = 435; serverHelp.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var apiMode = Ui.Combo(new[] { "auto", "chat", "responses" }, settings.EnhancerServerApiMode, Locale.T, _ => { }, 130);
        var detected = new ComboBox { IsVisible = false, MinWidth = 160 };
        AiGenerationControls.StyleModelField(detected, "Detected server model");
        detected.SelectionChanged += (_, _) => { if (detected.SelectedItem is string id) serverModel.Text = id; };
        var discover = Ui.TextButton("Load server models", () => { });
        var discovery = Ui.Label("", Palette.Secondary);
        discovery.TextWrapping = Avalonia.Media.TextWrapping.Wrap; discovery.MaxWidth = 435;
        var discoveryVersion = 0;
        AiTaskService.ServerTextModel[] catalogue = [];
        void UpdateEfforts()
        {
            var current = effort.SelectedItem as string ?? settings.EnhancerEffort;
            var supported = catalogue.FirstOrDefault(m => m.Id == serverModel.Text)?.Efforts;
            string[] fallback = selectedProvider switch
            {
                "kimi" => ["thinking", "no_thinking"],
                "claude" => ["low", "medium", "high", "xhigh", "max"],
                "codex" => ["low", "medium", "high", "xhigh", "max", "ultra"],
                _ => ["none", "minimal", "low", "medium", "high", "xhigh", "max"]
            };
            var options = new[] { "default" }.Concat(supported ?? fallback).Distinct().ToArray();
            effort.ItemsSource = options;
            effort.SelectedItem = options.Contains(current) ? current : "default";
            effort.IsEnabled = options.Length > 1;
        }
        serverModel.TextChanged += (_, _) => UpdateEfforts();
        discover.Click += async (_, _) =>
        {
            var revision = ++discoveryVersion;
            discover.IsEnabled = false; discovery.Text = "Reading the server catalogue…";
            try
            {
                var result = await AiTaskService.DiscoverServerModels(url.Text ?? "", selectedProvider, serverUrl.Text ?? "", lifetime.Token);
                if (revision != discoveryVersion) return;
                catalogue = result;
                var ids = result.Select(m => m.Id).ToArray();
                UpdateEfforts();
                detected.ItemsSource = ids; detected.IsVisible = ids.Length > 0;
                detected.SelectedItem = ids.Contains(serverModel.Text) ? serverModel.Text : null;
                discovery.Text = ids.Length == 0 ? "No models reported. Enter a model ID." : $"{ids.Length} server models";
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (HttpRequestException) { if (revision == discoveryVersion) discovery.Text = Locale.T("The ComfyUI server is unavailable."); }
            catch (Exception error) { if (revision == discoveryVersion) discovery.Text = Locale.Message(error.Message); }
            finally { if (revision == discoveryVersion) discover.IsEnabled = true; }
        };
        Grid EnhancerFields(params (string Label, Control Field)[] rows)
        {
            var form = CanvasDialogs.Form(rows);
            form.ColumnDefinitions[0].Width = new GridLength(138);
            return form;
        }
        Action? showComfyKey = null;
        var chatFields = EnhancerFields(("Text model", model), ("", Ui.TextButton("Manage Comfy.org key…", () => showComfyKey?.Invoke())));
        var enhancerServer = new TextBox { Text = url.Text, Name = "EnhancerComfyServer" };
        enhancerServer.TextChanged += (_, _) => { if (enhancerServer.Text != url.Text) url.Text = enhancerServer.Text; };
        url.TextChanged += (_, _) => { if (enhancerServer.Text != url.Text) enhancerServer.Text = url.Text; };
        var serverAddress = EnhancerFields(("ComfyUI server", enhancerServer));
        var directKey = new TextBox { PasswordChar = '●', PlaceholderText = Locale.T("Enter a key to save or replace it") };
        var forgetDirect = false;
        var clearDirect = Ui.TextButton("Forget saved key", () => { directKey.Text = ""; forgetDirect = true; });
        var apiFields = EnhancerFields(("API base URL", apiUrl), ("Model ID", apiModel), ("Saved API key", directKey), ("", clearDirect));
        Locale.Tip(apiUrl, "Optional bearer key: AVS_COMPATIBLE_API_KEY");
        var serverApiFields = EnhancerFields(("Server API base URL", serverUrl), ("API mode", apiMode));
        var serverFields = Ui.Column(6, serverApiFields, EnhancerFields(("Server model", serverModel)),
            EnhancerFields(("", Ui.Row(6, discover, detected)), ("", discovery), ("", serverHelp)));
        var reasoning = EnhancerFields(("Reasoning effort", effort));
        var budget = new Expander { Header = Locale.T("Response options"), Content = EnhancerFields(("Token limit", tokens)) };
        string[] remoteAdapters = ["codex", "claude", "kimi"];
        var remotePicker = Ui.Combo(remoteAdapters, remoteCli, CliLabel, _ => { }, double.NaN);
        var remoteFields = EnhancerFields(("CLI", remotePicker));
        var remoteHelp = RemoteEnhancerHelp();
        var summary = new ConnectionSummary();
        var textCheck = new ConnectionCheck(lifetime.Token, async token =>
        {
            var draft = new Settings();
            ReadEnhancer(draft);
            var probe = live.SettingsPreview(() => url.Text ?? "");
            probe.ConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
            if (draft.EffectiveEnhancerProvider() == "comfy")
            {
                var key = DraftCredential(ComfyServerAddress.Parse(url.Text ?? ""));
                probe.ApiKey = () => key;
            }
            var direct = !string.IsNullOrWhiteSpace(directKey.Text) ? directKey.Text.Trim()
                : forgetDirect ? Environment.GetEnvironmentVariable("AVS_COMPATIBLE_API_KEY") ?? "" : null;
            await probe.TestEnhancerAsync(draft, token, direct);
            return "The model responded successfully.";
        }, "Send a short prompt to the selected model.");
        void UpdateSummary()
        {
            var text = selectedProvider switch
            {
                "direct" => apiModel.Text, "comfy" => model.SelectedItem as string,
                "local-codex" => localCodex.Model.Text, _ => serverModel.Text
            };
            summary.Update(selectedProvider is "direct" or "local-codex" ? "This computer" : "ComfyUI server", text,
                selectedProvider switch
                {
                    "direct" => "Third-party API key", "comfy" => "Comfy.org API key", "local-codex" => "Local Codex sign-in",
                    "compatible" or "openai" => "API key on server", _ => "CLI sign-in on server"
                });
        }
        apiModel.TextChanged += (_, _) => UpdateSummary();
        localCodex.Model.TextChanged += (_, _) => UpdateSummary();
        serverModel.TextChanged += (_, _) => UpdateSummary();
        model.SelectionChanged += (_, _) => UpdateSummary();
        remotePicker.SelectionChanged += (_, _) =>
        {
            if (remotePicker.SelectedIndex < 0) return;
            serverModels[selectedProvider] = serverModel.Text?.Trim() ?? "";
            selectedProvider = remoteCli = remoteAdapters[remotePicker.SelectedIndex];
            serverModel.Text = serverModels.GetValueOrDefault(selectedProvider);
            ShowProvider();
        };
        void ShowProvider()
        {
            textCheck.Reset();
            apiFields.IsVisible = selectedProvider == "direct";
            chatFields.IsVisible = selectedProvider == "comfy";
            localCodex.View.IsVisible = selectedProvider == "local-codex";
            serverFields.IsVisible = !apiFields.IsVisible && !chatFields.IsVisible && !localCodex.View.IsVisible;
            serverAddress.IsVisible = chatFields.IsVisible || serverFields.IsVisible;
            remoteFields.IsVisible = remoteHelp.IsVisible = AiTaskService.IsRemoteCli(selectedProvider);
            serverApiFields.IsVisible = selectedProvider == "compatible";
            serverHelp.Text = selectedProvider switch
            {
                "compatible" => "Requests originate on the ComfyUI server. Set AVS_COMPATIBLE_API_KEY there if required.",
                "openai" => "Uses OPENAI_API_KEY on the ComfyUI server.",
                _ => "Uses the CLI and its signed-in account on the ComfyUI server."
            };
            catalogue = [];
            UpdateEfforts();
            budget.IsVisible = !AiTaskService.IsRemoteCli(selectedProvider) && !localCodex.View.IsVisible;
            reasoning.IsVisible = !localCodex.View.IsVisible;
            discoveryVersion++; discover.IsEnabled = true; discovery.Text = ""; detected.IsVisible = false;
            UpdateSummary();
        }
        var routes = new List<string> { "direct", "comfy", "remote", "local-codex" };
        if (selectedProvider is "compatible" or "openai") routes.Add(selectedProvider);
        else if (!AiTaskService.Providers.ContainsKey(selectedProvider)) selectedProvider = "comfy";
        var provider = Ui.Combo(routes, AiTaskService.IsRemoteCli(selectedProvider) ? "remote" : selectedProvider, key => Locale.T(key switch
            {
                "direct" => "Third-party API directly", "comfy" => "Via ComfyUI · Comfy credits",
                "remote" => "CLI on the ComfyUI server", "local-codex" => "CLI on this computer",
                _ => AiTaskService.Providers[key]
            }),
            key =>
            {
                serverModels[selectedProvider] = serverModel.Text?.Trim() ?? "";
                selectedProvider = key == "remote" ? remoteCli : key;
                serverModel.Text = serverModels.GetValueOrDefault(selectedProvider);
                ShowProvider();
            }, double.NaN);
        provider.Name = "TextEnhancerRoute";
        ShowProvider();

        void SavePromptDraft()
        {
            if (promptPack.Length > 0) instructionDrafts[AiTaskService.InstructionKey(promptPack, instructionTask)] = instructions.Text ?? "";
        }
        void LoadPromptDraft(EngineProfile? pack)
        {
            promptPack = pack?.Id ?? "";
            instructions.Text = service.InstructionsFor(instructionDrafts, pack, instructionTask);
        }
        var instructionMode = Ui.Combo(new[] { AiTaskKind.ImageEdit, AiTaskKind.GenerateImage }, instructionTask,
            task => task == AiTaskKind.GenerateImage ? "Generate image" : "Edit image", task =>
            {
                SavePromptDraft();
                instructionTask = task;
                LoadPromptDraft(PromptPack(promptPack));
            }, 200);
        var status = Ui.Label(ConnectionLabel(service.ConnectionState), Palette.Secondary);
        status.MaxWidth = 430;
        status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var models = new ComfyModelPicker(settings, service, () => url.Text ?? "");
        Action? refreshOperation = null;
        void RefreshStatus()
        {
            refreshOperation?.Invoke();
            string address;
            try { address = ComfyServerAddress.Parse(url.Text ?? "").ToString(); }
            catch (FormatException) { status.Text = Locale.Message("Enter a complete ComfyUI URL."); if (settingsDialog != null) settingsDialog.CanAccept = false; return; }
            if (settingsDialog != null) settingsDialog.CanAccept = true;
            if (service.ConnectedServerUrl != address || service.ConnectionState != ComfyConnectionState.Connected)
            {
                status.Text = Locale.Message("Connect to load the server’s models.");
                status.Foreground = Palette.Secondary;
                return;
            }
            var info = service.ServerInfo;
            status.Text = Locale.Message($"Connected{(info?.Version == null ? "" : " · ComfyUI " + info.Version)}" +
                (info?.Devices.Count > 0 ? "\n" + string.Join(", ", info.Devices) : ""));
            status.Foreground = Palette.Accent;
        }
        models.ChoicesChanged += RefreshStatus;
        url.TextChanged += (_, _) => { models.Refresh(); RefreshStatus(); };
        var test = Ui.TextButton("Test connection", () => { });
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            status.Text = Locale.Message("Connecting…");
            try
            {
                service.ConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
                await service.TestConnectionAsync(lifetime.Token, overrideUrl: url.Text);
                var selectedChat = model.SelectedItem as string ?? settings.EnhancerModel;
                model.ItemsSource = service.ChatModels.Append(selectedChat).Distinct().ToArray();
                model.SelectedItem = selectedChat;
                models.Refresh();
                RefreshStatus();
            }
            catch (Exception error)
            {
                status.Text = Locale.Message("Error: " + error.Message);
                status.Foreground = Avalonia.Media.Brushes.Orange;
                models.Refresh();
            }
            finally { test.IsEnabled = true; }
        };

        IReadOnlyDictionary<string,string> CurrentFiles()
        {
            try { return models.Choices(url.Text ?? ""); }
            catch (FormatException) { return new Dictionary<string,string>(); }
        }
        AiTaskKind PrimaryTask(EngineProfile pack) => pack.Binding(AiTaskKind.GenerateImage) != null ? AiTaskKind.GenerateImage
            : pack.Tasks.FirstOrDefault()?.Task ?? AiTaskKind.ImageEdit;
        var engineNames = service.Engines.Profiles.Count == 0 ? ["No Engine Packs installed"] : service.Engines.Profiles.Select(p => p.DisplayName).ToArray();
        var selectedName = service.SelectedEngine?.DisplayName ?? engineNames[0];
        var defaultEngineId = service.SelectedEngine?.Id;
        var routeDrafts = new Dictionary<string,string>(settings.AiModelRoutes);
        var changedAssignments = new HashSet<string>();
        var changedDefault = false;
        var assignmentPickers = new Dictionary<AiTaskKind, ComboBox>();
        const string inherit = "Use default model";
        var useDefault = Ui.TextButton("Make default", () => { });
        void DefaultState()
        {
            useDefault.IsEnabled = service.SelectedEngine != null && service.SelectedEngine.Id != defaultEngineId;
            useDefault.Content = useDefault.IsEnabled ? "Make default" : "Default model";
        }
        useDefault.Click += (_, _) =>
        {
            if (service.SelectedEngine is not { } selected) return;
            changedDefault = true;
            var previous = service.Engines.Find(defaultEngineId);
            foreach (var (operation, picker) in assignmentPickers)
            {
                var key = operation.ToString();
                if (selected.Binding(operation) != null && !taskDrafts.ContainsKey(key))
                    picker.SelectedItem = inherit;
                else if (selected.Binding(operation) == null && Equals(picker.SelectedItem, inherit))
                {
                    var fallback = previous?.Binding(operation) != null ? previous : service.Engines.Profiles.First(p => p.Binding(operation) != null);
                    picker.SelectedItem = picker.Items.OfType<ImageModelChoice>().FirstOrDefault(m => m.ModelKey == service.Engines.ModelFor(fallback, operation, CurrentFiles()).ModelKey);
                }
            }
            defaultEngineId = selected.Id;
            DefaultState();
        };
        DefaultState();
        var promptPicker = Ui.Combo(promptProfiles, PromptPack(promptPack)!, pack => pack.DisplayName, pack =>
        {
            SavePromptDraft();
            LoadPromptDraft(pack);
        }, 250);
        var engine = new ComboBox { Width = 260 };
        var routePicker = new ComboBox { Width = 260, Name = "ModelWorkflowRoute" };
        AiGenerationControls.StyleModelField(routePicker, "Workflow");
        routePicker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<EngineProfile>((pack, _) => Ui.Label(pack?.Id ?? ""));
        bool populating = false;
        void RefreshModelList()
        {
            populating = true;
            try
            {
                var filesForServer = CurrentFiles();
                var options = service.Engines.Profiles.Select(pack => service.Engines.ModelFor(pack, PrimaryTask(pack), filesForServer))
                    .GroupBy(model => model.ModelKey)
                    .Select(group => group.FirstOrDefault(model => model.Engine.Id == service.SelectedEngine?.Id) ?? group.First()).ToArray();
                AiGenerationControls.PresentImageModels(engine, options);
                var selected = options.FirstOrDefault(model => model.Engine.Id == service.SelectedEngine?.Id);
                engine.SelectedItem = selected;
                var routes = service.Engines.Profiles.Where(pack => service.Engines.ModelFor(pack, PrimaryTask(pack), filesForServer).ModelKey == selected?.ModelKey).ToArray();
                routePicker.ItemsSource = routes;
                routePicker.SelectedItem = service.SelectedEngine;
                routePicker.IsVisible = routes.Length > 1;
            }
            finally { populating = false; }
        }
        void SelectRoute(EngineProfile? pack)
        {
            if (pack == null || populating) return;
            service.SelectedEngine = pack;
            DefaultState(); RefreshModelList(); models.Refresh(); RefreshStatus();
        }
        engine.SelectionChanged += (_, _) => SelectRoute((engine.SelectedItem as ImageModelChoice)?.Engine);
        routePicker.SelectionChanged += (_, _) =>
        {
            if (populating || routePicker.SelectedItem is not EngineProfile route || engine.SelectedItem is not ImageModelChoice choice) return;
            routeDrafts[choice.ModelKey] = route.Id;
            SelectRoute(route);
        };
        RefreshModelList();
        models.ChoicesChanged += RefreshModelList;
        url.LostFocus += (_, _) => RefreshModelList();
        string ModelTitle(EngineProfile pack, AiTaskKind task)
        {
            if (pack.Id is "direct-codex-image" or "direct-api-image") return pack.DisplayName;
            try { return service.Engines.ModelFor(pack, task, models.Choices(url.Text ?? "")).Name; }
            catch (FormatException) { return pack.ModelName ?? pack.DisplayName; }
        }
        void StylePicker(ComboBox picker, IReadOnlyList<EngineProfile> packs, AiTaskKind? task = null)
        {
            void Refresh() => AiGenerationControls.StyleModelPicker(picker, packs, pack =>
                ModelTitle(pack, task ?? pack.Tasks.FirstOrDefault()?.Task ?? AiTaskKind.GenerateImage));
            Refresh();
            models.ChoicesChanged += Refresh;
            url.LostFocus += (_, _) => Refresh();
        }
        StylePicker(promptPicker, promptProfiles);
        engine.IsEnabled = service.Engines.Profiles.Count > 0;
        var connection = CanvasDialogs.Form(
            ("ComfyUI Server URL", url),
            ("Connection timeout", Ui.Row(6, timeout, Ui.Label(Locale.T("seconds"), Palette.Secondary))),
            ("", test),
            ("Status", status));
        var taskRows = new List<(string, Control)>();
        foreach (var task in Enum.GetValues<AiTaskKind>().Where(task => task is not (AiTaskKind.SelectSubject or AiTaskKind.MatchToScene or AiTaskKind.ObjectSelection)))
        {
            var key = task.ToString();
            var packs = service.Engines.Profiles.Where(pack => pack.Binding(task) != null).ToArray();
            if (packs.Length == 0) continue;
            var assignmentPicker = new ComboBox { Width = 260 };
            bool refreshing = false;
            void FillAssignments()
            {
                refreshing = true;
                try
                {
                    var filesForServer = CurrentFiles();
                    var assigned = taskDrafts.GetValueOrDefault(key) ?? settings.EngineForTask(task);
                    var route = packs.FirstOrDefault(p => p.Id == assigned);
                    var options = service.Engines.ImageModels(task, route, filesForServer, routeDrafts);
                    var modelKey = !changedAssignments.Contains(key) ? settings.AiTaskModelKeys.GetValueOrDefault(key) : null;
                    var savedFiles = settings.ComfyModelsFor(url.Text ?? "");
                    if (route != null && service.Engines.ModelFor(route, task, filesForServer).ModelKey != service.Engines.ModelFor(route, task, savedFiles).ModelKey)
                        modelKey = null;
                    var selected = modelKey != null ? options.FirstOrDefault(m => m.ModelKey == modelKey)
                        : route == null ? null : options.FirstOrDefault(m => m.ModelKey == service.Engines.ModelFor(route, task, filesForServer).ModelKey);
                    string? missing = !string.IsNullOrEmpty(assigned) && selected == null ? "Unavailable · " + (modelKey ?? assigned) : null;
                    assignmentPicker.ItemsSource = new object[] { inherit }.Concat(options).Concat(missing == null ? [] : new[] { missing }).ToArray();
                    assignmentPicker.SelectedItem = string.IsNullOrEmpty(assigned) ? inherit : (object?)selected ?? missing;
                }
                finally { refreshing = false; }
            }
            assignmentPicker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((item, _) =>
                Ui.Label(item is ImageModelChoice model ? model.Name : item?.ToString() ?? ""));
            AiGenerationControls.StyleModelField(assignmentPicker, task.DisplayName() + " model");
            FillAssignments();
            models.ChoicesChanged += FillAssignments;
            url.LostFocus += (_, _) => FillAssignments();
            assignmentPicker.SelectionChanged += (_, _) =>
            {
                if (refreshing) return;
                changedAssignments.Add(key);
                if (Equals(assignmentPicker.SelectedItem, inherit)) taskDrafts[key] = "";
                else if (assignmentPicker.SelectedItem is ImageModelChoice model) taskDrafts[key] = model.Engine.Id;
            };
            assignmentPickers[task] = assignmentPicker;
            taskRows.Add((task.DisplayName(), assignmentPicker));
        }
        var assignments = new Expander { Header = Locale.T("Defaults by operation"), Content = CanvasDialogs.Form(taskRows.ToArray()) };
        var files = new Expander { Header = Locale.T("Model components"), Content = Ui.Column(8, routePicker, models) };
        var auth = CanvasDialogs.Form(("Saved API key", sessionKey), ("", clear), ("Environment variable", keyEnvironment));
        var credentials = new Expander { Header = Locale.T("Comfy credits"), Content = Ui.Column(8, auth, keyNote), IsExpanded = focusField == "key" };
        var advancedPrompt = new Expander { Header = Locale.T("System instructions"), IsExpanded = true, Content = Ui.Column(8, instructionMode, instructions,
            Ui.TextButton("Restore default", () => instructions.Text = service.InstructionsFor(new Dictionary<string, string>(), PromptPack(promptPack), instructionTask))) };
        void ReadEnhancer(Settings target)
        {
            target.EnhancerInstructions = new(instructionDrafts);
            serverModels[selectedProvider] = serverModel.Text?.Trim() ?? "";
            target.EnhancerServerModels = new(serverModels);
            target.EnhancerServerApiMode = apiMode.SelectedItem as string ?? "auto";
            target.EnhancerServerApiUrl = serverUrl.Text?.Trim() ?? "";
            target.EnhancerProvider = selectedProvider;
            target.EnhancerRemoteCli = remoteCli;
            target.EnhancerCodexPath = localCodex.Path.Text?.Trim() ?? "";
            target.EnhancerCodexModel = localCodex.Model.Text?.Trim() ?? "";
            target.EnhancerUseCompatibleApi = selectedProvider == "direct";
            target.EnhancerApiUrl = apiUrl.Text?.Trim() ?? "";
            target.EnhancerApiModel = apiModel.Text?.Trim() ?? "";
            target.EnhancerModel = model.SelectedItem as string ?? settings.EnhancerModel;
            target.EnhancerMaxTokens = (int)(tokens.Value ?? 4096);
            target.EnhancerEffort = effort.SelectedItem as string ?? "default";
        }
        string? DraftCredential(ComfyServerAddress address)
        {
            var typed = sessionKey.Text?.Trim();
            if (!string.IsNullOrEmpty(typed)) return typed;
            var environment = keyEnvironment.Text?.Trim();
            return (!clearKey && Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("Comfy", address.HttpBase)) : null)
                ?? (!clearKey && address.ToString() == live.ConnectedServerUrl ? live.SessionApiKey : null)
                ?? (string.IsNullOrWhiteSpace(environment) ? null : Environment.GetEnvironmentVariable(environment));
        }
        var readiness = Ui.Label("", Palette.Secondary);
        readiness.HorizontalAlignment = HorizontalAlignment.Left;
        readiness.MaxWidth = 490; readiness.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var operationPicker = new ComboBox { Name = "ModelOperation", Width = 260 };
        AiGenerationControls.StyleModelField(operationPicker, "Operation");
        operationPicker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AiTaskKind>((task, _) => Ui.Label(task.DisplayName()));
        var fix = Ui.TextButton("Connect", () => { });
        Action? fixAction = null;
        fix.Click += (_, _) => fixAction?.Invoke();
        var generation = Ui.TextButton("Generation settings…", () => { });
        var generationDraft = settings.CopyPreferences();
        var changedGeneration = new Dictionary<(string Engine, AiTaskKind Task), AiOperationSettings>();
        generation.Click += async (_, _) =>
        {
            if (service.SelectedEngine is not { } pack || operationPicker.SelectedItem is not AiTaskKind task) return;
            var key = (pack.Id, task);
            var current = changedGeneration.GetValueOrDefault(key) ?? settings.OperationFor(pack.Id, task, pack.PaidApi);
            await Advanced(settingsDialog!, generationDraft, service, task, current, updated => changedGeneration[key] = updated, deferSave: true);
        };
        var compact = owner.Bounds.Width < 720;
        Control Page(string title, params Control[] controls)
        {
            var column = Ui.Column(12, controls);
            column.Children.Insert(0, Ui.Label(Locale.T(title), size: 20, weight: Avalonia.Media.FontWeight.SemiBold));
            column.Margin = new Thickness(compact ? 4 : 18, 8, 8, 12);
            return new ScrollViewer { Name = "SettingsPage", Content = column, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }
        TabItem Nav(string title, Control content)
        {
            var tab = new TabItem { Header = title, Content = content, Padding = new Thickness(8, 7) };
            tab.HeaderTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((label, _) =>
                new TextBlock { Text = Locale.T(label ?? ""), FontSize = 13, FontWeight = Avalonia.Media.FontWeight.Medium,
                    MinWidth = compact ? 0 : 130, VerticalAlignment = VerticalAlignment.Center });
            return tab;
        }
        foreach (var section in new[] { assignments, files, credentials, advancedPrompt, budget })
        {
            section.HorizontalAlignment = HorizontalAlignment.Stretch;
            section.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            section.Background = Palette.Panel;
            section.Padding = new Thickness(10, 6);
        }
        var app = Application.Current as App;
        var previousTheme = app?.CurrentTheme ?? settings.Theme;
        var chosenTheme = settings.Theme;
        string previousLanguage = Locale.Language;
        string language = settings.Language;
        var languagePicker = Ui.Combo(new[] { "en", "ru" }, language, code => code == "ru" ? "Русский" : "English", value => { language = value; Locale.Apply(value,owner,settingsDialog); }, 190);
        languagePicker.Name = "EditorLanguagePicker";
        var themePicker = Ui.Combo(Enum.GetValues<EditorTheme>(), chosenTheme, EditorSetup.ThemeLabel,
            theme => { chosenTheme = theme; app?.ApplyTheme(theme); }, 190);
        themePicker.Name = "EditorThemePicker";
        Avalonia.Automation.AutomationProperties.SetName(themePicker, "Interface theme");
        var editor = owner as MainWindow;
        var initialSetup = editor?.ReadEditorSetup() ?? new(settings.Theme, EditorLayoutPreset.Classic, AiPanelPlacement.UnderSelection);
        var chosenLayout = initialSetup.Layout;
        var chosenAiPanel = initialSetup.AiPanel;
        var layoutPicker = Ui.Combo(Enum.GetValues<EditorLayoutPreset>(), chosenLayout, EditorSetup.LayoutLabel, value => chosenLayout = value, 190);
        layoutPicker.Name = "EditorLayoutPicker";
        Avalonia.Automation.AutomationProperties.SetName(layoutPicker, "Workspace layout");
        var aiPanelPicker = Ui.Combo(Enum.GetValues<AiPanelPlacement>(), chosenAiPanel, EditorSetup.AiPanelLabel, value => chosenAiPanel = value, 190);
        aiPanelPicker.Name = "EditorAiPanelPicker";
        Avalonia.Automation.AutomationProperties.SetName(aiPanelPicker, "AI panel placement");
        const string gpuLabel = "Use GPU for local object selection and matting when available";
        var selectionGpu = new CheckBox { Name = "UseSelectionGpu", Content = Locale.T(gpuLabel),
            IsChecked = settings.UseSelectionGpu, IsEnabled = OperatingSystem.IsWindows() };
        const string rasterGpuLabel = "Use GPU for large RGB16 blur filters";
        var rasterGpu = new CheckBox { Name = "UseRasterFilterGpu", Content = Locale.T(rasterGpuLabel),
            IsChecked = settings.UseRasterFilterGpu, IsEnabled = Composa.Rendering.GpuAcceleration.CanEnable };
        const string editorGpuLabel = "Use GPU acceleration for editing";
        var editorGpu = new CheckBox { Name = "UseEditorGpu", Content = Locale.T(editorGpuLabel),
            IsChecked = settings.UseEditorGpu, IsEnabled = Composa.Rendering.GpuAcceleration.CanEnable };
        var devices = new Composa.Vision.OnnxModel[] { Composa.Vision.PromptModels.EfficientSamS.Encoder,
            Composa.Vision.PromptModels.EfficientSamS.Decoder, Composa.Vision.RefinementModels.VitMatte };
        var usedDevices = devices.Select(m => (m.Name, Device: Composa.Vision.ModelRunner.DeviceFor(m)))
            .Where(m => m.Device != null).Select(m => $"{m.Name}: {m.Device}").ToArray();
        if (usedDevices.Length > 0) ToolTip.SetTip(selectionGpu, string.Join(Environment.NewLine, usedDevices));
        var groupedPanels = new CheckBox { Content = Locale.T("Group side panels in tabs"), IsChecked = settings.TabbedPanels };
        var linkedSources = new CheckBox { Content = Locale.T("Automatically refresh linked files"), IsChecked = settings.AutoUpdateLinkedSources };
        var updates = new CheckBox { Content = Locale.T("Check for updates on startup"), IsChecked = settings.CheckForUpdates };
        var workflowDraft = settings.Workflows.Copy();
        service.WorkflowSettings = () => workflowDraft;
        var selectedWorkflow = focusField?.StartsWith("workflow:", StringComparison.Ordinal) == true ? focusField[9..] : null;
        var workflows = new WorkflowSettingsPage(workflowDraft, service.Engines, CurrentFiles, () => service.ServerCapabilities, selectedWorkflow);
        var body = new TabControl
        {
            Width = Math.Clamp(owner.Bounds.Width - 64, 520, 780), Height = Math.Clamp(owner.Bounds.Height - 150, 300, 450),
            Name = "AiSettingsTabs", TabStripPlacement = compact ? Dock.Top : Dock.Left,
            ItemsSource = new[] {
                Nav("Connection", Page("Connection", connection, credentials)),
                Nav("Image models", Page("Image models", EnhancerFields(("Model", Ui.Row(8, engine, useDefault)), ("Operation", operationPicker), ("", Ui.Column(5, readiness, fix)), ("", generation)), assignments, files, showContext)),
                Nav("Prompt enhancer", Page("Prompt enhancer", Ui.Column(5, Ui.Label(Locale.T("Connection method"), Palette.Secondary), provider),
                    summary.View, serverAddress, chatFields, apiFields, remoteFields, serverFields, localCodex.View, remoteHelp,
                    textCheck.View, EnhancerFields(("Image model", promptPicker)), advancedPrompt, reasoning, budget)),
                Nav("Editor", Page("Editor", Ui.Column(5, Ui.Label(Locale.T("Language"), Palette.Secondary), languagePicker),
                    Ui.Column(5, Ui.Label(Locale.T("Interface theme"), Palette.Secondary), themePicker),
                    Ui.Column(5, Ui.Label(Locale.T("Workspace layout"), Palette.Secondary), layoutPicker),
                    Ui.Column(5, Ui.Label(Locale.T("AI panel placement"), Palette.Secondary), aiPanelPicker),
                    groupedPanels, selectionGpu, rasterGpu, editorGpu, linkedSources, updates)),
                Nav("Workflows", Page("Workflows", workflows.Content)),
                Nav("Image generation", Page("Image generation", imageConnection.View))
            }, SelectedIndex = Math.Clamp(page, 0, 5)
        };
        showComfyKey = () => { body.SelectedIndex = 0; credentials.IsExpanded = true; sessionKey.Focus(); };
        bool refreshingOperation = false;
        refreshOperation = () =>
        {
            if (refreshingOperation) return;
            refreshingOperation = true;
            try
            {
                var pack = service.SelectedEngine;
                var selectedModel = engine.SelectedItem as ImageModelChoice;
                var choices = CurrentFiles();
                var operations = pack?.Tasks.Select(b => b.Task).Distinct()
                    .Where(t => t is not (AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject or AiTaskKind.MatchToScene)
                        && service.Engines.ModelFor(pack, t, choices).ModelKey == selectedModel?.ModelKey).ToArray() ?? [];
                if (!operationPicker.Items.OfType<AiTaskKind>().SequenceEqual(operations))
                {
                    var previous = operationPicker.SelectedItem as AiTaskKind?;
                    operationPicker.ItemsSource = operations;
                    operationPicker.SelectedItem = previous is { } p && operations.Contains(p) ? p
                        : operations.Contains(AiTaskKind.GenerativeFill) ? AiTaskKind.GenerativeFill : operations.FirstOrDefault();
                }
                models.SetOperation(operationPicker.SelectedItem as AiTaskKind?);
                readiness.Foreground = Palette.Secondary;
                fix.IsVisible = true;
                fix.Content = Locale.T("Connection…");
                fixAction = () => { body.SelectedIndex = 0; url.Focus(); };
                if (service.ConnectionState != ComfyConnectionState.Connected || service.ConnectedServerUrl != ComfyServerAddress.Parse(url.Text ?? "").ToString())
                { readiness.Text = "Connect to check this operation."; return; }
                if (pack == null || operationPicker.SelectedItem is not AiTaskKind task) { readiness.Text = "No workflow installed."; return; }
                var result = service.OperationCompatibility(pack, task, models.Choices(url.Text ?? ""), settings.OperationFor(pack.Id, task, pack.PaidApi) with { AlignPixels = settings.AiAlignMaskedEdits });
                if (!result.IsCompatible)
                {
                    readiness.Foreground = Avalonia.Media.Brushes.Orange;
                    readiness.Text = string.Join("\n", result.Missing);
                    fix.Content = Locale.T("Model components…");
                    fixAction = () => { files.IsExpanded = true; files.BringIntoView(); };
                }
                else if (pack.PaidApi && string.IsNullOrWhiteSpace(DraftCredential(ComfyServerAddress.Parse(url.Text ?? ""))))
                {
                    readiness.Text = "Workflow ready · Comfy credits key required";
                    fix.Content = Locale.T("Set API key…"); fixAction = () => { body.SelectedIndex = 0; credentials.IsExpanded = true; sessionKey.Focus(); };
                }
                else { readiness.Text = "Ready"; fix.IsVisible = false; }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { readiness.Text = error.Message; }
            finally { refreshingOperation = false; }
        };
        operationPicker.SelectionChanged += (_, _) => refreshOperation();
        sessionKey.TextChanged += (_, _) => refreshOperation();
        files.IsExpanded = focusField == "files";
        var validation = Ui.Label("", Avalonia.Media.Brushes.Orange);
        validation.IsVisible = false; validation.MaxWidth = 520; validation.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var search = new TextBox { Name = "SettingsSearch", PlaceholderText = Locale.T("Search settings…"), MaxLength = 120 };
        var matches = new ListBox { Name = "SettingsMatches", IsVisible = false, MaxHeight = 165 };
        var anchors = new List<(string Title, string Keywords, int Page, Control Target, Expander? Section, Action? Select)>
        {
            ("ComfyUI server", "connection address url сервер подключение", 0, url, null, null),
            ("Connection timeout", "seconds", 0, timeout, null, null),
            ("Comfy credits API key", "ключ токен авторизация", 0, sessionKey, credentials, null),
            ("Defaults by operation", "назначение модель default", 1, assignments, assignments, null),
            ("Workflows", "graph JSON prompt binding граф привязки импорт", 4, workflows.Content, null, null),
            ("Model components", "checkpoint vae encoder файлы граф", 1, files, files, null),
            ("Show AI context bounds", "output selection выделение контекст", 1, showContext, null, null),
            ("Prompt provider", "улучшение промт cli api", 2, provider, null, null),
            ("Text model", "chatgpt luna comfy credits", 2, provider, null, null),
            ("Reasoning effort", "reasoning рассуждение", 2, effort, null, null),
            ("Response options", "token limit токены", 2, tokens, budget, null),
            ("System instructions", "системный промт", 2, instructions, advancedPrompt, null),
            ("Image generation", "api cli подключение генерация изображений", 4, imageConnection.Picker, null, null),
            ("Language", "language russian english русский английский язык", 3, languagePicker, null, null),
            ("Interface theme", "appearance light dark neutral classic blue оформление тема светлая тёмная", 3, themePicker, null, null),
            ("Workspace layout", "classic modern tools panels классическая новая панели раскладка", 3, layoutPicker, null, null),
            ("AI panel placement", "sidebar selection положение ии выделение", 3, aiPanelPicker, null, null),
            (gpuLabel, "GPU CPU SAM ViTMatte DirectML видеокарта выделение", 3, selectionGpu, null, null),
            (rasterGpuLabel, "GPU CPU OpenCL Gaussian RGB16 видеокарта ускорение фильтры", 3, rasterGpu, null, null),
            (editorGpuLabel, "GPU CPU OpenCL rendering brush effects видеокарта ускорение холст кисть эффекты", 3, editorGpu, null, null),
            ("Group side panels in tabs", "панели вкладки", 3, groupedPanels, null, null),
            ("Automatically refresh linked files", "обновление связь", 3, linkedSources, null, null),
            ("Check for updates", "обновления", 3, updates, null, null)
        };
        foreach (var pack in service.Engines.Profiles)
            anchors.Add((ModelTitle(pack, pack.Tasks.FirstOrDefault()?.Task ?? AiTaskKind.GenerateImage), pack.Id + " model модель", 1, engine, null, () => SelectRoute(pack)));
        void OpenMatch()
        {
            if (matches.SelectedItem is not ListBoxItem { Tag: int index }) return;
            var entry = anchors[index];
            search.Text = "";
            body.SelectedIndex = entry.Page;
            if (entry.Section != null) entry.Section.IsExpanded = true;
            entry.Select?.Invoke();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { entry.Target.BringIntoView(); entry.Target.Focus(); });
        }
        search.TextChanged += (_, _) =>
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            matches.Items.Clear();
            matches.IsVisible = words.Length > 0;
            if (!matches.IsVisible) return;
            for (int i = 0; i < anchors.Count; i++)
            {
                var entry = anchors[i];
                if (words.All(word => (Locale.T(entry.Title) + " " + entry.Title + " " + entry.Keywords).Contains(word, StringComparison.OrdinalIgnoreCase)))
                    matches.Items.Add(new ListBoxItem { Content = Locale.T(entry.Title), Tag = i, Padding = new Thickness(8, 4) });
            }
            if (matches.ItemCount == 0) matches.Items.Add(new ListBoxItem { Content = Locale.T("No matching settings"), IsEnabled = false });
            matches.SelectedIndex = 0;
        };
        matches.PointerReleased += (_, _) => OpenMatch();
        matches.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenMatch(); e.Handled = true; } };
        search.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!matches.IsVisible) return;
            if (e.Key == Key.Enter) { OpenMatch(); e.Handled = true; }
            else if (e.Key == Key.Down) { matches.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { search.Text = ""; e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        var settingsBody = new Grid { RowDefinitions = new RowDefinitions("Auto,6,*") };
        settingsBody.Children.Add(search); Grid.SetRow(body, 2); settingsBody.Children.Add(body);
        Grid.SetRow(matches, 2); matches.VerticalAlignment = VerticalAlignment.Top; settingsBody.Children.Add(matches);
        settingsDialog = new DialogWindow("Settings", Ui.Column(5, settingsBody, validation), "Save settings");
        settingsDialog.Opened += (_, _) =>
        {
            if (focusField?.StartsWith("model:", StringComparison.Ordinal) == true && service.Engines.Find(focusField[6..]) is { } target)
            { SelectRoute(target); engine.Focus(); }
            else if (focusField == "key") sessionKey.Focus();
            else if (focusField == "server") url.Focus();
            else if (page == 1) engine.Focus();
            else if (page == 2) provider.Focus();
            else if (page == 4) imageConnection.OperationPicker.Focus();
        };
        settingsDialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            { search.Focus(); search.SelectAll(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        settingsDialog.Closed += (_, _) => lifetime.Cancel();
        settingsDialog.ValidateAccept = () =>
        {
            Control field = url;
            var tab = 0;
            try
            {
                var address = ComfyServerAddress.Parse(url.Text ?? "");
                Uri? directEndpoint = null;
                if (selectedProvider == "direct" || forgetDirect || !string.IsNullOrWhiteSpace(directKey.Text))
                {
                    tab = 2; field = apiUrl;
                    directEndpoint = Assistant.ChatCompletionAssistantProvider.Endpoint(apiUrl.Text ?? "");
                    if (selectedProvider == "direct" && string.IsNullOrWhiteSpace(apiModel.Text))
                    { field = apiModel; throw new FormatException("Enter the API model ID."); }
                }
                if (selectedProvider == "compatible")
                {
                    tab = 2; field = serverUrl;
                    _ = Assistant.ChatCompletionAssistantProvider.Endpoint(serverUrl.Text ?? "");
                }
                var changes = new Dictionary<string, string?>();
                if (Settings.Persist)
                {
                    if (clearKey || !string.IsNullOrWhiteSpace(sessionKey.Text))
                        changes[UserApiKeys.Target("Comfy", address.HttpBase)] = string.IsNullOrWhiteSpace(sessionKey.Text) ? null : sessionKey.Text.Trim();
                    if (forgetDirect || !string.IsNullOrWhiteSpace(directKey.Text))
                        changes[UserApiKeys.Target("Direct", directEndpoint!)] = string.IsNullOrWhiteSpace(directKey.Text) ? null : directKey.Text.Trim();
                }
                tab = 4; field = imageConnection.ApiUrl;
                imageConnection.Validate(out field, changes);
                if (Settings.Persist)
                {
                    var previous = new Dictionary<string, string?>();
                    try
                    {
                        foreach (var (target, value) in changes)
                        {
                            var old = UserApiKeys.Read(target);
                            UserApiKeys.Write(target, value);
                            previous[target] = old;
                        }
                    }
                    catch { foreach (var (target, value) in previous) UserApiKeys.Write(target, value); throw; }
                }
                return true;
            }
            catch (Exception error)
            {
                validation.Text = Locale.Message(error.Message); validation.IsVisible = true;
                if (error is FormatException)
                {
                    body.SelectedIndex = tab;
                    field.Focus();
                    if (field is TextBox input) input.SelectAll();
                }
                return false;
            }
        };
        RefreshStatus();
        bool accepted = false;
        try { accepted = await settingsDialog.Ask(owner); }
        finally { if (!accepted) { if (chosenTheme != previousTheme) app?.ApplyTheme(previousTheme); Locale.Apply(previousLanguage,owner,settingsDialog); } }
        if (!accepted) return false;
        settings.Language = language;
        settings.Theme = chosenTheme;
        settings.ComfyServerUrl = ComfyServerAddress.Parse(url.Text ?? "").ToString();
        settings.ComfyConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
        service.ConnectionTimeoutSeconds = settings.ComfyConnectionTimeoutSeconds;
        service.SelectedEngine = service.Engines.Find(defaultEngineId);
        live.AcceptSettingsPreview(service, settings.ComfyServerUrl);
        settings.ComfyApiKeyEnvironment = keyEnvironment.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(sessionKey.Text)) live.SessionApiKey = sessionKey.Text.Trim();
        else if (clearKey) live.SessionApiKey = null;
        settings.AiEngineId = service.SelectedEngine?.Id;
        // Bind the saved assignment to the actual model and keep the chosen workflow as its preferred route.
        // Opening an unrelated page must not silently replace a model that disappeared from the server.
        var modelFiles = models.Choices(settings.ComfyServerUrl);
        foreach (var (operation, picker) in assignmentPickers)
        {
            var key = operation.ToString();
            var routeId = taskDrafts.GetValueOrDefault(key) ?? settings.EngineForTask(operation);
            var route = service.Engines.Find(string.IsNullOrEmpty(routeId) ? defaultEngineId : routeId);
            if (route?.Binding(operation) == null || routeId is { Length: > 0 } && route.Id != routeId) continue;
            var selectedModel = service.Engines.ModelFor(route, operation, modelFiles);
            var oldModel = service.Engines.ModelFor(route, operation, settings.ComfyModelsFor(settings.ComfyServerUrl));
            var filesChanged = oldModel.ModelKey != selectedModel.ModelKey;
            var oldScope = $"{route.Id}/{operation}";
            if (settings.GenerationPresets.Remove(oldScope, out var legacy))
            {
                var modelScope = $"{oldModel.ModelKey}/{operation}";
                if (!settings.GenerationPresets.TryGetValue(modelScope, out var saved)) settings.GenerationPresets[modelScope] = saved = [];
                foreach (var preset in legacy) saved.TryAdd(preset.Key, preset.Value);
            }
            if (!settings.AiTaskModelKeys.ContainsKey(key) || filesChanged || changedAssignments.Contains(key) || (string.IsNullOrEmpty(routeId) && changedDefault))
                settings.AiTaskModelKeys[key] = selectedModel.ModelKey;
        }
        settings.AiModelRoutes = routeDrafts;
        settings.AiTaskEngineIds = taskDrafts;
        settings.UseSelectionGpu = selectionGpu.IsChecked == true;
        Composa.Vision.ModelRunner.PreferGpu = settings.UseSelectionGpu;
        settings.UseRasterFilterGpu = rasterGpu.IsChecked == true;
        if (settings.UseEditorGpu && editorGpu.IsChecked != true) Composa.Rendering.GpuAcceleration.ReleaseCanvasCache();
        settings.UseEditorGpu = editorGpu.IsChecked == true;
        Composa.Rendering.GpuAcceleration.Configure(settings.UseRasterFilterGpu, settings.UseEditorGpu);
        settings.TabbedPanels = groupedPanels.IsChecked == true;
        settings.AutoUpdateLinkedSources = linkedSources.IsChecked == true;
        settings.CheckForUpdates = updates.IsChecked == true;
        settings.AiShowContextBounds = showContext.IsChecked == true;
        SavePromptDraft();
        ReadEnhancer(settings);
        imageConnection.Save(settings);

        foreach (var (key, value) in changedGeneration)
        {
            settings.SetOperation(key.Engine, key.Task, value);
            if (value.AlignPixels is { } align) settings.AiAlignMaskedEdits = align;
        }
        if (changedGeneration.Count > 0) settings.GenerationPresets = generationDraft.GenerationPresets;
        models.Save();
        if (editor != null && ReferenceEquals(editor.Settings, settings))
            editor.ApplyEditorArrangement(chosenLayout != initialSetup.Layout ? chosenLayout : null,
                chosenAiPanel != initialSetup.AiPanel ? chosenAiPanel : null);
        settings.SaveWithWorkflows(workflowDraft);
        return true;
    }

    public static async Task<bool> Advanced(Window owner, Settings settings, AiTaskService? service = null, AiTaskKind task = AiTaskKind.GenerativeFill, AiOperationSettings? initialProfile = null, Action<AiOperationSettings>? acceptDraft = null, bool deferSave = false)
    {
        var paid = service?.SelectedEngine?.PaidApi == true;
        var profile = initialProfile ?? settings.OperationFor(service?.SelectedEngine?.Id, task, paid);
        var legacyScope = $"{service?.SelectedEngine?.Id}/{task}";
        var scope = service?.SelectedEngine is { } route
            ? $"{service.Engines.ModelFor(route, task, settings.ComfyModelsFor(settings.ComfyServerUrl)).ModelKey}/{task}" : legacyScope;
        var presets = new Dictionary<string, AiOperationSettings>(settings.GenerationPresets.GetValueOrDefault(scope)
            ?? settings.GenerationPresets.GetValueOrDefault(legacyScope) ?? [], StringComparer.OrdinalIgnoreCase);
        profile = profile with { AlignPixels = initialProfile?.AlignPixels ?? settings.AiAlignMaskedEdits };

        var modified = false;
        var editor = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Func<AiOperationSettings> read;
        editor.Content = GenerationFields(profile, service, task, paid, out read);
        var list = new ComboBox { MinWidth = 165, PlaceholderText = Locale.T("Choose preset") };
        AiGenerationControls.StyleModelField(list, "Generation preset");
        var name = new TextBox { Width = 165, MaxLength = 70, PlaceholderText = Locale.T("Preset name") };
        var save = Ui.TextButton("Save preset", () =>
        {
            var key = name.Text?.Trim();
            if (string.IsNullOrEmpty(key)) return;
            presets[key] = read();
            modified = true;
            list.ItemsSource = presets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            list.SelectedItem = key;
        });
        var delete = Ui.TextButton("Delete", () =>
        {
            if (list.SelectedItem is not string key) return;
            presets.Remove(key);
            modified = true;
            list.ItemsSource = presets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            list.SelectedIndex = -1;
            name.Text = "";
        });
        list.ItemsSource = presets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        list.SelectionChanged += (_, _) =>
        {
            delete.IsEnabled = list.SelectedItem is string;
            if (list.SelectedItem is not string key || !presets.TryGetValue(key, out var saved)) return;
            name.Text = key;
            editor.Content = GenerationFields(saved with { AlignPixels = saved.AlignPixels ?? read().AlignPixels }, service, task, paid, out read);
        };
        delete.IsEnabled = save.IsEnabled = false;
        name.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        var library = new Expander { Header = Locale.T("Presets"), HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Ui.Column(6, Ui.Row(8, list, delete), Ui.Row(8, name, save)) };
        var body = new ScrollViewer { Content = Ui.Column(12, editor, library), MaxHeight = Math.Clamp((owner.Owner?.Bounds.Height ?? owner.Bounds.Height) - 160, 300, 650),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (!await new DialogWindow($"{task.DisplayName()} · Generation settings", body).Ask(owner)) return false;
        var updated = read();
        if (modified || scope != legacyScope && settings.GenerationPresets.ContainsKey(legacyScope))
        {
            if (scope != legacyScope) settings.GenerationPresets.Remove(legacyScope);
            modified = true;
            if (presets.Count == 0) settings.GenerationPresets.Remove(scope);
            else settings.GenerationPresets[scope] = presets;
        }
        if (acceptDraft != null) acceptDraft(updated);
        else
        {
            settings.SetOperation(service?.SelectedEngine?.Id, task, updated);
            if (updated.AlignPixels is { } align) settings.AiAlignMaskedEdits = align;
        }
        if (!deferSave && (modified || acceptDraft == null)) settings.Save();
        return true;
    }

    private static Control GenerationFields(AiOperationSettings profile, AiTaskService? service, AiTaskKind task, bool paid, out Func<AiOperationSettings> read)
    {
        if (service?.ActiveWorkflow(task, service.SelectedEngine) is { } workflow)
            return BoundGenerationFields(profile, workflow, out read);
        var align = new CheckBox { Content = Locale.T("Align pixels"), IsChecked = profile.AlignPixels ?? true };
        var mp = ClosestMegapixels(profile.Megapixels); var originalSize = profile.OriginalSize;
        var reference = profile.ReferenceMegapixels;
        var fluxMemory = profile.FluxMemory is "reduced" or "standard" ? profile.FluxMemory : "auto";
        var seed = profile.Seed; var mode = profile.VariantMode;
        var quality = profile.ApiQuality;
        var model = service?.SelectedEngine?.ApiModel ?? "gpt-image-2.5-sunburst";
        var nano=NanoBanana.IsModel(model);
        var qwen=service?.SelectedEngine?.Id==QwenImageWorkflow.EngineId;
        var sequential=nano || model == PartnerNodes.Seedream || service?.SelectedEngine?.Id == Flux3Workflow.EngineId || qwen || task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground;
        if(sequential) mode=AiVariantMode.List;
        var qualities = PartnerPricing.Choices(service?.ServerCapabilities, model, nano ? "resolution" : "quality");
        if (qualities.Count == 0) qualities = nano ? ["1K","2K","4K"] : ["low", "medium", "high", "xhigh", "max"];
        if (!qualities.Contains(quality)) quality = qualities[0];
        var sizes = new[] { "Original size" }.Concat(AiDimensions.MegapixelOptions.Select(AiDimensions.Label)).ToArray();
        (string, Control)[] fields = [
            ("Image size", Ui.Combo(sizes, originalSize ? sizes[0] : AiDimensions.Label(mp), value => value,
                value => { originalSize = value == sizes[0]; if (!originalSize) mp = AiDimensions.MegapixelOptions.First(option => AiDimensions.Label(option) == value); }, 150)),
            ("Reference images", Ui.Combo(sizes, reference is { } r ? AiDimensions.Label(ClosestMegapixels(r)) : sizes[0], value => value,
                value => reference = value == sizes[0] ? null : AiDimensions.MegapixelOptions.First(option => AiDimensions.Label(option) == value), 150)),
            ("Execution", Ui.Combo(sequential ? new[] { AiVariantMode.List } : new[] { AiVariantMode.List, AiVariantMode.Batch }, sequential ? AiVariantMode.List : mode,
                value => paid ? value == AiVariantMode.List ? "List · separate API requests" : "Batch · one API request"
                    : value == AiVariantMode.List ? "List · lower VRAM" : "Batch · faster, more VRAM", value => mode = value, 250)),
            ("Memory use", Ui.Combo(new[] { "auto", "reduced", "standard" }, fluxMemory,
                value => value == "auto" ? "Auto" : value == "reduced" ? "Lower VRAM" : "Standard", value => fluxMemory = value, 150)),
            ("Seed", Ui.Row(6, Ui.Number(seed, -1, long.MaxValue, value => seed = (long)value, 1, "0", 150), Ui.Label(Locale.T("-1 = random"))))];
        var form = CanvasDialogs.Form(fields.Where(field => field.Item1 != "Memory use" || service?.SelectedEngine?.Id == "flux2-klein-intel-xpu")
            .Where(field => !paid || field.Item1 != "Seed").ToArray());
        var note = Ui.Label(task == AiTaskKind.GenerateImage
            ? "Generate a new image using the prompt and optional references. Image size preserves proportions. List runs variants separately; Batch groups them. Paid variants are billed individually."
            : paid
            ? "Context, edge matching and the insertion mask are automatic. Selection softness and saved edge settings do not control generation. The image model receives a crop and references; Composa restores placement and applies its own mask once. Each variant is billed; Undo does not refund charges."
            : "Context, conditioning overlap and the insertion edge are automatic. Selection softness and saved edge settings do not control generation. Local color matching uses unchanged surroundings and fades inside the edit. List runs variants separately; Batch needs more memory. Undo restores the original.", Palette.Secondary);
        note.MaxWidth = 470; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        if (service?.SelectedEngine?.Id == "flux2-klein-intel-xpu")
            note.Text += " Auto uses Lower VRAM on Intel: the text encoder runs on CPU and VAE works in tiles. This may take longer; Standard keeps normal server placement.";
        var extra = new StackPanel { Spacing = 9 };
        var scene = profile.Scene.Normalized();
        if (task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight || task == AiTaskKind.ChangeBackground && !paid)
            extra.Children.Add(SceneAdvanced(task, scene, paid, value => scene = value, qwen));
        var lorasEnabled = profile.LorasEnabled;
        var loras = profile.Loras.Take(3).ToList(); while (loras.Count < 3) loras.Add(new());
        if (paid && model != PartnerNodes.Seedream)
            extra.Children.Add(CanvasDialogs.Form((nano ? "Resolution" : "GPT quality", Ui.Combo(qualities, quality, value => value, value => quality = value, 150))));
        else if (service?.SelectedEngine?.Lora.Supported == true)
        {
            var rows = new StackPanel { Spacing = 9, IsEnabled = lorasEnabled };
            extra.Children.Add(Ui.Check("Enable LoRAs", lorasEnabled, value => { lorasEnabled = value; rows.IsEnabled = value; }));
            var names = service.ServerCapabilities?.ModelChoices.GetValueOrDefault("LoraLoaderModelOnly.lora_name") ?? [];
            for (var i = 0; i < 3; i++)
            {
                var index = i; var draft = loras[index];
                var options = new[] { "None" }.Concat(names.Order(StringComparer.OrdinalIgnoreCase)).Concat(string.IsNullOrWhiteSpace(draft.Name) || names.Contains(draft.Name) ? [] : new[] { draft.Name }).ToArray();
                var combo = Ui.Combo(options, string.IsNullOrWhiteSpace(draft.Name) ? "None" : draft.Name, value => value,
                    value => loras[index] = loras[index] with { Name = value == "None" ? "" : value }, 255);
                combo.IsEnabled = service.ConnectionState == ComfyConnectionState.Connected && names.Count > 0;
                var toggle = Ui.Check("", draft.Enabled, value => loras[index] = loras[index] with { Enabled = value }); Locale.Tip(toggle, "Enable this LoRA");
                var strength = Ui.SliderField("", Math.Clamp(draft.Strength, 0, 3), 0, 3, value => loras[index] = loras[index] with { Strength = value }, 0.01, "0.00", 100, reset: 1);
                loras[index] = loras[index] with { Strength = Math.Clamp(draft.Strength, 0, 3) };
                rows.Children.Add(Ui.Column(3, Ui.Label($"LoRA {i + 1}", Palette.Secondary), Ui.Row(7, toggle, combo, strength)));
            }
            extra.Children.Add(rows);
            if (names.Count == 0) extra.Children.Add(Ui.Label(Locale.T("Connect / refresh models to read the server's LoRA list."), Palette.Secondary));
        }
        if (task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight)
            note.Text = "Lighting preserves the source silhouette. A separate shadow layer uses darkening generated near the subject's support. Undo restores the original.";
        else if (task == AiTaskKind.ChangeBackground)
            note.Text = "Two passes create the scene and remove the temporary generated subject. Your original subject stays on a separate layer. Paid models bill both passes.";
        if(qwen) note.Text += " Qwen 2.1 Turbo uses its fixed 8-step schedule at full denoise, automatic KV cache and sequential variants. Technical padding to multiples of 32 is removed on insertion.";
        if (task != AiTaskKind.GenerateImage) extra.Children.Add(align);
        read = () => profile with
        {
            AlignPixels = align.IsChecked == true,
            Scene = scene.Normalized(),
            Megapixels = mp, OriginalSize = originalSize, ReferenceMegapixels = reference, FluxMemory = fluxMemory,
            Seed = seed, VariantMode = mode, ApiQuality = quality,
            LorasEnabled = lorasEnabled, Loras = !paid && service?.SelectedEngine?.Lora.Supported == true
                ? loras.Where(lora => lora.Name.Length > 0).ToList() : profile.Loras
        };
        return Ui.Column(12, form, extra, note);
    }

    public static async Task<string?> ApiKey(Window owner, CancellationToken cancellationToken, string? serverUrl = null)
    {
        var key = new TextBox { Width = 390, PasswordChar = '●', PlaceholderText = Locale.T("Comfy.org API key"), Name = "ComfyApiKeyEntry" };
        var note = Ui.Label(Locale.T("Composa saves your Comfy.org key in your user profile for this server. Manage it in AI Settings → Connection → Comfy credits."), Palette.Secondary);
        note.MaxWidth = 390; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var dialog = new DialogWindow("Comfy.org API key", Ui.Column(10, note, key), "Continue");
        dialog.CanAccept = false;
        key.TextChanged += (_, _) => dialog.CanAccept = !string.IsNullOrWhiteSpace(key.Text);
        dialog.Opened += (_, _) => key.Focus();
        using var registration = cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
        if (!await dialog.Ask(owner)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var value = key.Text?.Trim();
        if (Settings.Persist && serverUrl != null && !string.IsNullOrEmpty(value))
            UserApiKeys.Write(UserApiKeys.Target("Comfy", ComfyServerAddress.Parse(serverUrl).HttpBase), value);
        return value;
    }

    public static async Task<bool> Upscale(Window owner, Settings settings, int width, int height, bool selection, EngineProfile? engine = null, AiTaskService? service = null, string? directModel = null)
    {
        var profile = settings.OperationFor(engine?.Id, AiTaskKind.Upscale, engine?.PaidApi == true);
        var factor = profile.UpscaleFactor == 4 ? 4 : 2;
        var dimensions = Ui.Label("", Palette.Secondary);
        void Refresh() => dimensions.Text = $"{width * (long)factor} × {height * (long)factor} px" + (selection ? " · fitted back into your selection" : " · resizes the canvas");
        var scale = Ui.Combo(new[] { 2, 4 }, factor, value => "×" + value, value => { factor = value; Refresh(); }, 100);
        Refresh();
        var note = Ui.Label("", Palette.Secondary);
        void EngineNote() => note.Text = engine?.Id.StartsWith("seedvr2", StringComparison.Ordinal) == true
            ? "Official ComfyUI SeedVR2 workflow: the whole image in one run, one Euler/simple step. Only VAE uses the official 512 px tiles with 128 px overlap. ComfyUI manages memory; no extra Composa image tiles. Select its models in ComfyUI Settings."
            : "Inference is tiled. A ×4 model still computes its native scale for a ×2 result; the input is not reduced, so source details are preserved. For lowest memory use a native ×2 model in ComfyUI Settings.";
        EngineNote();
        var models = service == null ? [] : UpscaleModels.Choices(service);
        var savedModels = settings.ComfyModelsFor(settings.ComfyServerUrl);
        var selected = models.FirstOrDefault(choice => choice.Engine.Id == engine?.Id && choice.Model == savedModels.GetValueOrDefault(choice.Slot.Key))
            ?? models.FirstOrDefault(choice => choice.Engine.Id == engine?.Id && choice.Model == choice.Slot.Default) ?? models.FirstOrDefault();
        if (selected != null) { engine = selected.Engine; EngineNote(); }
        Control picker = directModel != null ? Ui.Label(directModel) : selected == null ? Ui.Label(Locale.T("Refresh Models on ComfyUI to read available upscale models.")) : Ui.Combo(models.ToArray(), selected,
            choice => choice.ToString(), choice => { selected = choice; engine = choice.Engine; EngineNote(); }, 430);
        if (picker is ComboBox upscaleModels) AiGenerationControls.StyleModelField(upscaleModels, "Upscale model");
        if (directModel == null) Locale.Tip(picker, note);
        note.MaxWidth = 450; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var upscaleDialog = new DialogWindow("AI Upscale", Ui.Column(12, CanvasDialogs.Form(("Model", picker), ("Scale", scale)), dimensions), "Upscale") { CanAccept = selected != null || directModel != null };
        if (!await upscaleDialog.Ask(owner)) return false;
        if (selected != null)
        {
            var choices = new Dictionary<string, string>(savedModels, StringComparer.Ordinal) { [selected.Slot.Key] = selected.Model };
            settings.SetComfyModels(settings.ComfyServerUrl, choices);
        }
        if (engine != null && directModel == null)
        {
            settings.AiTaskEngineIds[nameof(AiTaskKind.Upscale)] = engine.Id;
            if (service != null) settings.AiTaskModelKeys[nameof(AiTaskKind.Upscale)] =
                service.Engines.ModelFor(engine, AiTaskKind.Upscale, settings.ComfyModelsFor(settings.ComfyServerUrl)).ModelKey;
        }
        profile = settings.OperationFor(engine?.Id, AiTaskKind.Upscale, engine?.PaidApi == true);
        settings.SetOperation(engine?.Id, AiTaskKind.Upscale, profile with { UpscaleFactor = factor }); settings.Save(); return true;
    }

    public static async Task<AiPromptResult?> Prompt(Window owner, AiTaskKind task, Settings settings, int documentWidth, int documentHeight, string initialPrompt = "", AiTaskService? service = null, int referenceCount = 0, bool hasSelection = false, AiReferenceEditor? references = null,
        SKRectI? sourceCanvas = null, SKRectI? selectionBounds = null, string? preferredEngine = null, EngineProfile? directEngine = null)
    {
        var previousEngine = service?.SelectedEngine;
        if (service != null) service.SelectedEngine = directEngine ?? service.EngineFor(task, preferredEngine);
        var profile = settings.OperationFor(service?.SelectedEngine?.Id, task, service?.SelectedEngine?.PaidApi == true);
        var scene=profile.Scene.Normalized();
        var lighting=Ui.Combo(Enum.GetValues<RelightPreset>(),scene.Preset,SceneEditSettings.Name,value=>scene=scene with {Preset=value},300);
        var scope=Ui.Combo(Enum.GetValues<RelightScope>(),scene.Scope,value=>value==RelightScope.Subject ? "Subject" : "Whole visible image",value=>scene=scene with {Scope=value},300);
        var profileDrafts = new Dictionary<string, AiOperationSettings>();
        var rememberedPrompt = task switch { AiTaskKind.GenerateImage => settings.GenerateImagePrompt, AiTaskKind.ImageEdit => settings.ImageEditPrompt, _ => "" };
        var prompt = AiGenerationControls.PromptField("Describe the result…");
        prompt.Text = string.IsNullOrEmpty(initialPrompt) ? rememberedPrompt : initialPrompt;
        var originalSize = profile.OriginalSize; var mp = ClosestMegapixels(profile.Megapixels);
        var variants = 1; var quality = profile.ApiQuality;
        var expansionMode = hasSelection ? AiExpansionMode.MaskedRegion : profile.ExpansionMode;
        var regionSide = profile.ExpansionMinimumSide; var wholeSide = profile.WholeExpansionMinimumSide;
        var width = documentWidth; var height = documentHeight;
        var sizeError = Ui.Label("", Palette.Secondary);
        sizeError.MaxWidth = 430;
        sizeError.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var cost = Ui.Label("", Palette.Secondary);
        var sizeOptions = new[] { "Original size" }.Concat(AiDimensions.MegapixelOptions.Select(AiDimensions.Label)).ToArray();
        DialogWindow? dialog = null;
        var alignment = AlignmentToggle(settings);
        var qualityHost = new StackPanel();
        var qualityOptions = new[] { "low", "medium", "high", "xhigh", "max" };
        var qualityCombo = new ComboBox { ItemsSource = qualityOptions, SelectedItem = qualityOptions.Contains(quality) ? quality : "low", Width = 150 };
        AiGenerationControls.StyleModelField(qualityCombo, "Quality");
        qualityHost.Children.Add(Ui.Row(8, Ui.Label(Locale.T("Quality"), Palette.Secondary), qualityCombo));
        var size = new ComboBox { Width = 115, ItemsSource = sizeOptions, SelectedItem = originalSize ? sizeOptions[0] : AiDimensions.Label(mp) };
        var expandSize = new ComboBox { Width = 245 };
        Button? addReference = null;
        var more = AiGenerationControls.IconAction(Icons.MoreActions, "AiMore", "Image size and quality", () => { });
        var actions = Ui.Row(3, more);
        var imageSize = Ui.Row(8, Ui.Label(Locale.T("Image size"), Palette.Secondary), size);
        more.Flyout = new Flyout { Content = Ui.Column(7, imageSize, qualityHost), Placement = PlacementMode.BottomEdgeAlignedRight };
        AiGenerationControls.StyleModelField(size, "Image size");
        qualityCombo.SelectionChanged += (_, _) =>
        {
            if (qualityCombo.SelectedItem is string value) { quality = value; Refresh(); }
        };
        size.SelectionChanged += (_, _) =>
        {
            if (size.SelectedItem is not string value) return;
            originalSize = value == sizeOptions[0];
            if (!originalSize) mp = AiDimensions.MegapixelOptions.First(option => AiDimensions.Label(option) == value);
            Refresh();
        };
        void RefreshExpandChoices()
        {
            var choices = expansionMode == AiExpansionMode.WholeImage ? new[] { 0 }.Concat(AiDimensions.ExpansionSides).ToArray() : AiDimensions.ExpansionSides;
            expandSize.ItemsSource = choices.Select(value => value == 0 ? "Original expanded size" : value + " px · longest side").ToArray();
            var selected = expansionMode == AiExpansionMode.WholeImage ? wholeSide : regionSide;
            expandSize.SelectedIndex = Array.IndexOf(choices, selected);
            if (expandSize.SelectedIndex < 0) expandSize.SelectedIndex = Array.IndexOf(choices, 1024);
        }
        expandSize.SelectionChanged += (_, _) =>
        {
            var choices = expansionMode == AiExpansionMode.WholeImage ? new[] { 0 }.Concat(AiDimensions.ExpansionSides).ToArray() : AiDimensions.ExpansionSides;
            if (expandSize.SelectedIndex < 0 || expandSize.SelectedIndex >= choices.Length) return;
            if (expansionMode == AiExpansionMode.WholeImage) wholeSide = choices[expandSize.SelectedIndex]; else regionSide = choices[expandSize.SelectedIndex];
            Refresh();
        };
        var mode = Ui.Combo(new[] { AiExpansionMode.MaskedRegion, AiExpansionMode.WholeImage }, expansionMode,
            value => value == AiExpansionMode.MaskedRegion ? "Empty area only · preserve image" : "Regenerate whole expanded image",
            value => { expansionMode = value; RefreshExpandChoices(); Refresh(); }, 300);
        var variantButtons = AiGenerationControls.QuickRuns(value => { variants = value; dialog!.SubmitGeneration(); });
        variantButtons.IsVisible = AiGenerationControls.SupportsVariants(task);
        AiOperationSettings CaptureProfile() => profile with
        {
            Scene=scene.Normalized(), AlignPixels = alignment.IsChecked == true,
            OriginalSize = originalSize, Megapixels = mp, ApiQuality = quality,
            ExpansionMode = task == AiTaskKind.GenerativeExpand ? expansionMode : profile.ExpansionMode,
            ExpansionMinimumSide = regionSide, WholeExpansionMinimumSide = wholeSide
        };
        void LoadProfile(AiOperationSettings next)
        {
            scene=next.Scene.Normalized(); lighting.SelectedIndex=(int)scene.Preset; scope.SelectedIndex=(int)scene.Scope;
            profile = next; originalSize = next.OriginalSize; mp = ClosestMegapixels(next.Megapixels);
            quality = next.ApiQuality;
            expansionMode = hasSelection ? AiExpansionMode.MaskedRegion : next.ExpansionMode;
            regionSide = next.ExpansionMinimumSide; wholeSide = next.WholeExpansionMinimumSide;
            qualityOptions = NanoBanana.IsModel(service?.SelectedEngine?.ApiModel) ? ["1K", "2K", "4K"] : ["low", "medium", "high", "xhigh", "max"];
            if (!qualityOptions.Contains(quality)) quality = qualityOptions[0];
            qualityCombo.ItemsSource = qualityOptions;
            qualityCombo.SelectedItem = quality;
            size.SelectedItem = originalSize ? sizeOptions[0] : AiDimensions.Label(mp);
            mode.SelectedIndex = expansionMode == AiExpansionMode.WholeImage ? 1 : 0;
            RefreshExpandChoices(); Refresh();
        }
        var advanced = AiGenerationControls.IconAction(Icons.Gear, "AiAdvanced", "Generation settings", () => { });
        advanced.Click += async (_, _) =>
        {
            await Advanced(dialog!, settings, service, task, CaptureProfile(), updated =>
            {
                LoadProfile(updated);
                if (updated.AlignPixels is { } align) alignment.IsChecked = align;
            });
        };
        var grip = AiGenerationControls.DragHandle();
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) dialog?.BeginMoveDrag(e);
        };
        var controls = Ui.Row(2, grip);
        if (references != null)
        {
            addReference = AiGenerationControls.IconAction(Icons.Image, "AiAddReference", "Add reference image", () => _ = references.Pick());
            controls.Children.Add(addReference);
        }
        if (service != null && task != AiTaskKind.GenerativeExpand)
            controls.Children.Add(EnhanceButton(prompt, settings, service, task, () => service.SelectedEngine));
        controls.Children.Add(alignment);
        if (directEngine != null) controls.Children.Add(Ui.Label(directEngine.DisplayName, Palette.Secondary));
        else controls.Children.Add(advanced);
        if (directEngine == null && service != null && service.Engines.Profiles.Where(pack => pack.Binding(task==AiTaskKind.MatchToScene ? AiTaskKind.Harmonize : task) != null).ToArray() is { Length: > 0 } packs)
        {
            var chosen = service.SelectedEngine;
            var choices = service.Engines.ImageModels(task == AiTaskKind.MatchToScene ? AiTaskKind.Harmonize : task,
                chosen, settings.ComfyModelsFor(settings.ComfyServerUrl), settings.AiModelRoutes);
            var picker = new ComboBox { Width = AiGenerationControls.ModelPickerWidth, IsEnabled = !settings.Workflows.Assignments.ContainsKey(task) };
            AiGenerationControls.PresentImageModels(picker, choices);
            picker.SelectedItem = choices.FirstOrDefault(m => m.Engine.Id == chosen?.Id) ?? choices.FirstOrDefault();
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not ImageModelChoice model) return;
                profileDrafts[service.SelectedEngine?.Id ?? "default"] = CaptureProfile();
                service.SelectedEngine = model.Engine;
                LoadProfile(profileDrafts.GetValueOrDefault(model.Engine.Id) ?? settings.OperationFor(model.Engine.Id, task, model.Engine.PaidApi));
            };
            controls.Children.Insert(1, picker);
        }
        var resolution = hasSelection ? CanvasDialogs.Form(("Generation size", expandSize))
            : CanvasDialogs.Form(("Expand mode", mode), ("Generation size", expandSize));
        void Refresh()
        {
            var toneOnly=task==AiTaskKind.MatchToScene && scene.MatchMode==SceneMatchMode.ToneAndColor;
            var paid = service?.SelectedEngine?.PaidApi == true && service.ActiveWorkflow(task, service.SelectedEngine) == null;
            qualityHost.IsVisible = paid && service?.SelectedEngine?.ApiModel != PartnerNodes.Seedream && !toneOnly;
            prompt.IsEnabled=!toneOnly;
            alignment.IsVisible = !toneOnly && task != AiTaskKind.GenerateImage;
            var nano=NanoBanana.IsModel(service?.SelectedEngine?.ApiModel);
            imageSize.IsVisible = !toneOnly && task != AiTaskKind.GenerativeExpand;
            more.IsVisible = imageSize.IsVisible || qualityHost.IsVisible;
            if (references != null)
            {
                references.View.IsVisible = references.Count > 0;
                if (addReference != null) addReference.IsVisible = references.Count == 0;
            }
            qualityOptions=nano ? ["1K","2K","4K"] : ["low","medium","high","xhigh","max"];
            if(!qualityOptions.Contains(quality)) quality=qualityOptions[0];
            if(!qualityCombo.Items.Cast<string>().SequenceEqual(qualityOptions)) qualityCombo.ItemsSource=qualityOptions;
            if(!Equals(qualityCombo.SelectedItem,quality)) qualityCombo.SelectedItem=quality;
            try
            {
                (width, height) = task == AiTaskKind.GenerativeExpand
                    ? AiDimensions.FromMaximumSide(expansionMode == AiExpansionMode.WholeImage ? wholeSide : regionSide, documentWidth, documentHeight)
                    : originalSize ? AiDimensions.OriginalGenerationSize(documentWidth, documentHeight) : AiDimensions.FromMegapixels(mp, documentWidth, documentHeight);
                if (task == AiTaskKind.GenerativeExpand && (expansionMode == AiExpansionMode.WholeImage ? wholeSide : regionSide) == 0)
                    (width, height) = AiDimensions.OriginalGenerationSize(width, height);
                var api = service?.SelectedEngine?.ApiModel == PartnerNodes.Seedream ? PartnerNodes.SeedreamSize(width,height) : paid && !nano ? PartnerImageSize.Plan(width, height,
                    (task == AiTaskKind.GenerativeExpand ? (expansionMode == AiExpansionMode.WholeImage ? wholeSide : regionSide) == 0 : originalSize)
                        ? Composa.Model.DocumentLimits.MinimumGenerationPixels : PartnerImageSize.MinimumPixels) : (width, height);
                var description = $"{width} × {height} px";
                if (sourceCanvas is { } canvas && selectionBounds is { IsEmpty: false } selection &&
                    task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.GenerativeExpand)
                    description = SelectionOutput.Describe(canvas, selection, service?.SelectedEngine,
                        task == AiTaskKind.GenerativeExpand ? regionSide == 0 : originalSize, width, height, quality);
                else if (nano) description += $"\nNano Banana: {quality} · {NanoBanana.Aspect(width, height)}";
                else if (paid && api != (width, height)) description += $"\nModel request  {api.Item1} × {api.Item2} px";
                Locale.Tip(task == AiTaskKind.GenerativeExpand ? expandSize : size, description);
                sizeError.IsVisible = false;
                if (dialog != null) dialog.CanAccept = true;
            }
            catch (Exception error)
            {
                sizeError.Text = error.Message;
                sizeError.IsVisible = true;
                if (dialog != null) dialog.CanAccept = false;
            }
            var sourceCount = task == AiTaskKind.GenerateImage ? 0 : 1;
            var count = sourceCount + (references?.Count ?? referenceCount);
            var estimate=paid?PartnerPricing.Estimate(service?.ServerCapabilities,service!.SelectedEngine!.ApiModel!,quality,"Custom",count,variants):null;
            if(task==AiTaskKind.ChangeBackground && estimate!=null)
            {
                var cleanupEstimate=PartnerPricing.Estimate(service?.ServerCapabilities,service!.SelectedEngine!.ApiModel!,quality,"Custom",1,variants);
                estimate=cleanupEstimate==null?null:new(estimate.MinimumUsd+cleanupEstimate.MinimumUsd,estimate.MaximumUsd+cleanupEstimate.MaximumUsd);
            }
            cost.IsVisible = paid && !toneOnly;
            cost.Text = paid ? estimate?.Label ?? "Paid API · estimate unavailable" : "";
        }
        prompt.IsVisible = task != AiTaskKind.GenerativeExpand;
        var content = Ui.Column(AiGenerationControls.EditorSpacing, prompt, new AiPromptToolbar(controls, actions));
        content.Width = Math.Clamp(owner.Bounds.Width > 0 ? owner.Bounds.Width - 32 : 532, 285, 532);
        if(task==AiTaskKind.Relight) content.Children.Add(Section("Lighting",CanvasDialogs.Form(("Lighting preset",lighting),("Target",scope))));
        if(task==AiTaskKind.ChangeBackground) prompt.PlaceholderText=Locale.T("Describe the background, surface and lighting");
        if (references != null) content.Children.Add(references.View);
        if (task == AiTaskKind.GenerativeExpand) content.Children.Add(resolution);
        content.Children.Add(cost);
        content.Children.Add(sizeError);
        var body = new ScrollViewer { Content = content, MaxHeight = Math.Clamp(owner.Bounds.Height - 170, 300, 650), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        dialog = new DialogWindow(task.DisplayName(), body, "Run"); dialog.PlaceGenerationActions(actions, variantButtons);
        if (references != null)
        {
            references.Owner = dialog;
            if (owner is MainWindow mainWindow) mainWindow.EnableAiReferenceDrop(dialog);
            references.Changed += Refresh;
            dialog.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
            {
                if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                { e.Handled = true; await references.Paste(dialog.FocusManager?.GetFocusedElement() as TextBox); }
            }, RoutingStrategies.Tunnel);
        }
        RefreshExpandChoices(); Refresh(); dialog.Opened += (_, _) => { if (prompt.IsVisible) prompt.Focus(); };
        bool accepted;
        EngineProfile? chosenEngine = null;
        try { accepted = await dialog.Ask(owner); chosenEngine = service?.SelectedEngine; }
        finally
        {
            if (references != null) { references.Changed -= Refresh; references.Owner = null; }
            if (service != null) service.SelectedEngine = previousEngine;
        }
        if (task == AiTaskKind.GenerateImage) { settings.GenerateImagePrompt = prompt.Text ?? ""; settings.Save(); }
        if (task == AiTaskKind.ImageEdit) { settings.ImageEditPrompt = prompt.Text ?? ""; settings.Save(); }
        if (!accepted) return null;
        if (alignment.IsVisible) settings.AiAlignMaskedEdits = alignment.IsChecked == true;
        settings.SetOperation(chosenEngine?.Id, task, CaptureProfile());
        settings.Save();
        return new(task == AiTaskKind.GenerativeExpand ? AiPromptDefaults.Expand : prompt.Text ?? "", width, height, profile.Seed, chosenEngine?.Id, variants);
    }

    private static double ClosestMegapixels(double value) => AiDimensions.MegapixelOptions.MinBy(option => Math.Abs(option - value));

    private static string ConnectionLabel(ComfyConnectionState state) => state switch
    {
        ComfyConnectionState.Connecting => "Connecting…",
        ComfyConnectionState.Connected => "Connected",
        ComfyConnectionState.Error => "Error",
        _ => "Disconnected"
    };
}
