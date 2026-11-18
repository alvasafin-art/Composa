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

public sealed record AiPromptResult(string Prompt, int Width, int Height, long Seed, string? EngineId = null);

public static partial class AiDialogs
{
    private static Border Section(string title, params Control[] controls) => new()
    {
        Background = Palette.Panel, CornerRadius = new CornerRadius(5), Padding = new Thickness(10),
        Child = Ui.Column(8, new Control[] { Ui.Label(title, weight: Avalonia.Media.FontWeight.SemiBold) }.Concat(controls).ToArray())
    };
    public static async Task<IReadOnlyList<string>?> LayerTags(Window owner, Layer layer, Settings settings)
    {
        var box = new TextBox { Text = string.Join(", ", layer.Tags.Order()), Width = 390, PlaceholderText = "title, product, hero-image" };
        var suggestions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var tag in Composa.Model.LayerTags.Standard.Concat(settings.CustomLayerTags).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var button = Ui.TextButton(tag, () =>
            {
                var values = (box.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (!values.Contains(tag, StringComparer.OrdinalIgnoreCase)) values.Add(tag);
                box.Text = string.Join(", ", values);
                box.CaretIndex = box.Text.Length;
            });
            button.MinWidth = 0;
            button.Margin = new Thickness(0, 0, 5, 5);
            suggestions.Children.Add(button);
        }
        var note = Ui.Label("Tags are optional metadata for templates, scripts and the future Assistant.", Palette.Secondary);
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
        Avalonia.Automation.AutomationProperties.SetName(toggle, "Align pixels");
        ToolTip.SetTip(toggle, "Align pixels · use unchanged image details to correct small shifts before inserting the result.");
        return toggle;
    }

    internal static Button EnhanceButton(TextBox prompt, Settings settings, AiTaskService service, AiTaskKind task, Func<EngineProfile?> engine)
    {
        var button = AiGenerationControls.IconAction(Icons.Wand, "AiEnhancePrompt", "Enhance prompt", () => { });
        var wand = button.Content;
        string ProviderHint() => "Enhance prompt · " + AiTaskService.Providers.GetValueOrDefault(settings.EffectiveEnhancerProvider());
        var label = ProviderHint();

        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        CancellationTokenSource? running = null;
        void RefreshButton() => button.IsEnabled = running != null || prompt.IsEnabled;
        button.DetachedFromVisualTree += (_, _) => running?.Cancel();
        prompt.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty || e.Property == InputElement.IsEnabledProperty) RefreshButton(); };
        RefreshButton();
        button.Click += async (_, _) =>
        {
            if (running != null) { running.Cancel(); return; }
            if (string.IsNullOrWhiteSpace(prompt.Text)) { prompt.Focus(); return; }
            if (engine() is not { } selected || prompt.Text is not { Length: > 0 } original) return;
            using var request = new CancellationTokenSource();
            running = request;
            var started = System.Diagnostics.Stopwatch.StartNew();
            void Progress()
            {
                var tip = $"Stop waiting · {started.Elapsed:m\\:ss}. A submitted server request may still be billed.";
                ToolTip.SetTip(button, tip);
                Avalonia.Automation.AutomationProperties.SetName(button, tip);
            }
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => Progress();
            button.Content = "■"; Progress(); timer.Start(); RefreshButton();
            try
            {
                var enhanced = await service.EnhanceAsync(settings, selected, task, original, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (TopLevel.GetTopLevel(button) != null && prompt.Text == original && engine()?.Id == selected.Id)
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
                timer.Stop(); running = null; button.Content = wand;
                ToolTip.SetTip(button, ProviderHint());
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
        var showContext = new CheckBox { Content = "Show AI context bounds after selection", IsChecked = settings.AiShowContextBounds };
        var taskDrafts = new Dictionary<string, string>(settings.AiTaskEngineIds);
        var url = new TextBox { Text = settings.ComfyServerUrl, Width = 330 };
        var timeout = Ui.Number(settings.ComfyConnectionTimeoutSeconds, 1, 120, _ => { }, 1, "0", 80);
        var keyEnvironment = new TextBox { Text = settings.ComfyApiKeyEnvironment, Width = 330 };
        var sessionKey = new TextBox { PasswordChar = '●', Width = 330, PlaceholderText = "Enter a new key to save or replace it" };
        var clearKey = false;
        var clear = Ui.TextButton("Forget saved key", () => { sessionKey.Text = ""; clearKey = true; });
        var keyNote = Ui.Label("Partner Nodes use a Comfy.org API key, not an OpenAI key. Create one at platform.comfy.org. Keys are saved in your user profile for this server. On Windows, Composa uses Credential Manager. The environment variable is also supported. Use HTTPS for servers outside a trusted LAN.", Palette.Secondary);
        keyNote.MaxWidth = 430; keyNote.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var promptDrafts = new Dictionary<string, AiPromptSetting>(settings.AiPackPrompts);
        var promptPack = service.SelectedEngine?.Id ?? "";
        var instructionDrafts = new Dictionary<string, string>(settings.EnhancerInstructions);
        var instructions = new TextBox { AcceptsReturn = true, Height = 104, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = instructionDrafts.GetValueOrDefault(promptPack) ?? service.DefaultInstructions(service.SelectedEngine) };
        var model = new ComboBox { ItemsSource = service.ChatModels.Append(settings.EnhancerModel).Distinct().ToArray(),
            SelectedItem = settings.EnhancerModel, MinWidth = 180 };
        AiGenerationControls.StyleModelField(model, "Prompt enhancer model");
        var tokens = Ui.Number(settings.EnhancerMaxTokens, 16, 16384, _ => { }, 1, "0", 100);
        var effort = Ui.Combo(new[] { "default", "none", "minimal", "low", "medium", "high", "xhigh", "max" }, settings.EnhancerEffort, s => s, _ => { }, 140);
        tokens.HorizontalAlignment = HorizontalAlignment.Left;
        var apiUrl = new TextBox { Text = settings.EnhancerApiUrl, MinWidth = 220 };
        var apiModel = new TextBox { Text = settings.EnhancerApiModel, PlaceholderText = "Model ID" };
        var selectedProvider = settings.EffectiveEnhancerProvider();
        var serverModels = new Dictionary<string,string>(settings.EnhancerServerModels);
        var serverModel = new TextBox { PlaceholderText = "Model ID", Text = serverModels.GetValueOrDefault(selectedProvider) };
        var serverUrl = new TextBox { Text = settings.EnhancerServerApiUrl };
        var serverHelp = Ui.Label("", Palette.Secondary);
        serverHelp.MaxWidth = 435; serverHelp.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var apiMode = Ui.Combo(new[] { "auto", "chat", "responses" }, settings.EnhancerServerApiMode, value => value, _ => { }, 130);
        var detected = new ComboBox { IsVisible = false, MinWidth = 160 };
        AiGenerationControls.StyleModelField(detected, "Detected server model");
        detected.SelectionChanged += (_, _) => { if (detected.SelectedItem is string id) serverModel.Text = id; };
        var discover = Ui.TextButton("Load server models", () => { });
        var discovery = Ui.Label("", Palette.Secondary);
        discovery.TextWrapping = Avalonia.Media.TextWrapping.Wrap; discovery.MaxWidth = 435;
        var discoveryVersion = 0;
        discover.Click += async (_, _) =>
        {
            var revision = ++discoveryVersion;
            discover.IsEnabled = false; discovery.Text = "Reading the server catalogue…";
            try
            {
                var ids = await AiTaskService.DiscoverServerModels(url.Text ?? "", selectedProvider, serverUrl.Text ?? "");
                if (revision != discoveryVersion) return;
                detected.ItemsSource = ids; detected.IsVisible = ids.Length > 0;
                detected.SelectedItem = ids.Contains(serverModel.Text) ? serverModel.Text : null;
                discovery.Text = ids.Length == 0 ? "No models reported. Enter a model ID." : $"{ids.Length} server models";
            }
            catch (Exception error) { if (revision == discoveryVersion) discovery.Text = error.Message; }
            finally { if (revision == discoveryVersion) discover.IsEnabled = true; }
        };
        Grid EnhancerFields(params (string Label, Control Field)[] rows)
        {
            var form = CanvasDialogs.Form(rows);
            form.ColumnDefinitions[0].Width = new GridLength(138);
            return form;
        }
        var chatFields = EnhancerFields(("Text model", model));
        var directKey = new TextBox { PasswordChar = '●', PlaceholderText = "Enter a key to save or replace it" };
        var forgetDirect = false;
        var clearDirect = Ui.TextButton("Forget saved key", () => { directKey.Text = ""; forgetDirect = true; });
        var apiFields = EnhancerFields(("API base URL", apiUrl), ("Model ID", apiModel), ("Saved API key", directKey), ("", clearDirect));
        ToolTip.SetTip(apiUrl, "Optional bearer key: AVS_COMPATIBLE_API_KEY");
        var serverApiFields = EnhancerFields(("Server API base URL", serverUrl), ("API mode", apiMode));
        var serverFields = Ui.Column(6, serverApiFields, EnhancerFields(("Server model", serverModel)),
            Ui.Row(6, discover, detected), discovery, serverHelp);
        var reasoning = EnhancerFields(("Reasoning effort", effort));
        var budget = new Expander { Header = "Response options", Content = Ui.Column(8, reasoning, EnhancerFields(("Token limit", tokens))) };
        void ShowProvider()
        {
            apiFields.IsVisible = selectedProvider == "direct";
            chatFields.IsVisible = selectedProvider == "comfy";
            serverFields.IsVisible = !apiFields.IsVisible && !chatFields.IsVisible;
            serverApiFields.IsVisible = selectedProvider == "compatible";
            serverHelp.Text = selectedProvider switch
            {
                "compatible" => "Requests originate on the ComfyUI server. Set AVS_COMPATIBLE_API_KEY there if required.",
                "openai" => "Uses OPENAI_API_KEY on the ComfyUI server.",
                _ => "Uses the CLI and its signed-in account on the ComfyUI server."
            };
            reasoning.IsVisible = selectedProvider == "comfy";
            budget.IsVisible = selectedProvider is not ("codex" or "claude" or "kimi");
            discoveryVersion++; discover.IsEnabled = true; discovery.Text = ""; detected.IsVisible = false;
        }
        var provider = Ui.Combo(AiTaskService.Providers.Keys, selectedProvider, key => AiTaskService.Providers[key],
            key =>
            {
                serverModels[selectedProvider] = serverModel.Text?.Trim() ?? "";
                selectedProvider = key;
                serverModel.Text = serverModels.GetValueOrDefault(key);
                ShowProvider();
            }, 260);
        ShowProvider();

        var currentPrompt = settings.PromptFor(promptPack);
        var additionalEnabled = new CheckBox { Content = "Include in image edits", IsChecked = currentPrompt.Enabled };
        var additionalPrompt = new TextBox { Name = "AdditionalEditingPrompt", Text = currentPrompt.Text, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 80, IsEnabled = currentPrompt.Enabled };
        void SavePromptDraft()
        {
            if (promptPack.Length == 0) return;
            instructionDrafts[promptPack] = instructions.Text ?? "";
            promptDrafts[promptPack] = new(additionalEnabled.IsChecked == true, additionalPrompt.Text ?? "");
        }
        void LoadPromptDraft(EngineProfile? pack)
        {
            promptPack = pack?.Id ?? "";
            instructions.Text = instructionDrafts.GetValueOrDefault(promptPack) ?? service.DefaultInstructions(pack);
            var value = promptDrafts.GetValueOrDefault(promptPack) ?? settings.PromptFor(promptPack);
            additionalPrompt.Text = value.Text; additionalEnabled.IsChecked = value.Enabled; additionalPrompt.IsEnabled = value.Enabled;
        }
        additionalEnabled.IsCheckedChanged += (_, _) => additionalPrompt.IsEnabled = additionalEnabled.IsChecked == true;
        var resetPrompt = Ui.TextButton("Restore default prompt", () => additionalPrompt.Text = AiPromptDefaults.PreserveAppearance);
        var status = Ui.Label(ConnectionLabel(service.ConnectionState), Palette.Secondary);
        status.MaxWidth = 430;
        status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var models = new ComfyModelPicker(settings, service, () => url.Text ?? "");
        void RefreshStatus()
        {
            string address;
            try { address = ComfyServerAddress.Parse(url.Text ?? "").ToString(); }
            catch (FormatException) { status.Text = "Enter a complete ComfyUI URL."; if (settingsDialog != null) settingsDialog.CanAccept = false; return; }
            if (settingsDialog != null) settingsDialog.CanAccept = true;
            if (service.ConnectedServerUrl != address || service.ConnectionState != ComfyConnectionState.Connected)
            {
                status.Text = "Connect to load the server’s models.";
                status.Foreground = Palette.Secondary;
                return;
            }
            EngineCompatibility? compatibility;
            try { compatibility = service.SelectedEngine == null ? null : service.Compatibility(service.SelectedEngine, models.Choices(address)); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                status.Text = error.Message;
                status.Foreground = Avalonia.Media.Brushes.Orange;
                return;
            }
            var info = service.ServerInfo;
            status.Text = $"Connected{(info?.Version == null ? "" : " · ComfyUI " + info.Version)}" +
                (info?.Devices.Count > 0 ? "\n" + string.Join(", ", info.Devices) : "") +
                (compatibility == null ? "" : "\n" + AiTaskService.CompatibilityMessage(service.SelectedEngine!, compatibility));
            status.Foreground = compatibility is { IsCompatible: false } ? Avalonia.Media.Brushes.Orange : Palette.Accent;
        }
        models.ChoicesChanged += RefreshStatus;
        url.TextChanged += (_, _) => { models.Refresh(); RefreshStatus(); };
        var test = Ui.TextButton("Test connection", () => { });
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            status.Text = "Connecting…";
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
                status.Text = "Error: " + error.Message;
                status.Foreground = Avalonia.Media.Brushes.Orange;
                models.Refresh();
            }
            finally { test.IsEnabled = true; }
        };

        var engineNames = service.Engines.Profiles.Count == 0 ? ["No Engine Packs installed"] : service.Engines.Profiles.Select(p => p.DisplayName).ToArray();
        var selectedName = service.SelectedEngine?.DisplayName ?? engineNames[0];
        var defaultEngineId = service.SelectedEngine?.Id;
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
            var previous = service.Engines.Find(defaultEngineId);
            foreach (var (operation, picker) in assignmentPickers)
            {
                var key = operation.ToString();
                if (selected.Binding(operation) != null && !taskDrafts.ContainsKey(key))
                    picker.SelectedItem = inherit;
                else if (selected.Binding(operation) == null && Equals(picker.SelectedItem, inherit))
                {
                    var fallback = previous?.Binding(operation) != null ? previous : service.Engines.Profiles.First(p => p.Binding(operation) != null);
                    picker.SelectedItem = fallback.DisplayName;
                }
            }
            defaultEngineId = selected.Id;
            DefaultState();
        };
        DefaultState();
        var promptPicker = Ui.Combo(engineNames, selectedName, name => name, name =>
        {
            SavePromptDraft();
            LoadPromptDraft(service.Engines.Profiles.FirstOrDefault(pack => pack.DisplayName == name));
        }, 250);
        var engine = Ui.Combo(engineNames, selectedName, name => name, name =>
        {
            SavePromptDraft();
            service.SelectedEngine = service.Engines.Profiles.FirstOrDefault(p => p.DisplayName == name);
            DefaultState();
            models.Refresh();
            RefreshStatus();
        }, 260);
        string ModelTitle(EngineProfile pack, AiTaskKind task)
        {
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
        StylePicker(engine, service.Engines.Profiles);
        StylePicker(promptPicker, service.Engines.Profiles);
        engine.IsEnabled = service.Engines.Profiles.Count > 0;
        var connection = CanvasDialogs.Form(
            ("ComfyUI Server URL", url),
            ("Connection timeout", Ui.Row(6, timeout, Ui.Label("seconds", Palette.Secondary))),
            ("", test),
            ("Status", status));
        var taskRows = new List<(string, Control)>();
        foreach (var task in Enum.GetValues<AiTaskKind>().Where(task => task is not (AiTaskKind.SelectSubject or AiTaskKind.MatchToScene)))
        {
            var key = task.ToString();
            var packs = service.Engines.Profiles.Where(pack => pack.Binding(task) != null).ToArray();
            if (packs.Length == 0) continue;
            var assigned = taskDrafts.GetValueOrDefault(key) ?? settings.EngineForTask(task);
            var missing = assigned != null && packs.All(pack => pack.Id != assigned) ? "Unavailable: " + assigned : null;
            var names = new[] { inherit }.Concat(packs.Select(pack => pack.DisplayName)).Concat(missing == null ? [] : new[] { missing }).ToArray();
            var selected = packs.FirstOrDefault(pack => pack.Id == assigned)?.DisplayName ?? missing ?? inherit;
            var assignmentPicker = Ui.Combo(names, selected, name => name, name =>
            {
                if (name == inherit) taskDrafts[key] = "";
                else if (packs.FirstOrDefault(pack => pack.DisplayName == name) is { } chosen) taskDrafts[key] = chosen.Id;
            }, 260);
            assignmentPickers[task] = assignmentPicker;
            StylePicker(assignmentPicker, packs, task);
            taskRows.Add((task.DisplayName(), assignmentPicker));
        }
        var assignments = new Expander { Header = "Defaults by operation", Content = CanvasDialogs.Form(taskRows.ToArray()) };
        var files = new Expander { Header = "Workflow and model files", Content = models };
        var auth = CanvasDialogs.Form(("Saved API key", sessionKey), ("", clear), ("Environment variable", keyEnvironment));
        var credentials = new Expander { Header = "Comfy credits", Content = Ui.Column(8, auth, keyNote), IsExpanded = focusField == "key" };
        var advancedPrompt = new Expander { Header = "System instructions", Content = Ui.Column(8, instructions,
            Ui.TextButton("Restore default", () => instructions.Text = service.DefaultInstructions(service.Engines.Find(promptPack)))) };
        var extraPrompt = new Expander { Header = "Additional editing prompt", Content = Ui.Column(8, additionalEnabled, additionalPrompt, resetPrompt) };
        void ReadEnhancer(Settings target)
        {
            target.EnhancerInstructions = instructionDrafts;
            serverModels[selectedProvider] = serverModel.Text?.Trim() ?? "";
            target.EnhancerServerModels = serverModels;
            target.EnhancerServerApiMode = apiMode.SelectedItem as string ?? "auto";
            target.EnhancerServerApiUrl = serverUrl.Text?.Trim() ?? "";
            target.EnhancerProvider = selectedProvider;
            target.EnhancerUseCompatibleApi = selectedProvider == "direct";
            target.EnhancerApiUrl = apiUrl.Text?.Trim() ?? "";
            target.EnhancerApiModel = apiModel.Text?.Trim() ?? "";
            target.EnhancerModel = model.SelectedItem as string ?? settings.EnhancerModel;
            target.EnhancerMaxTokens = (int)(tokens.Value ?? 4096);
            target.EnhancerEffort = effort.SelectedItem as string ?? "default";
        }
        var sample = new TextBox { Text = "Add a small plant beside the window", AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 44 };
        var response = new SelectableTextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
        var tryPrompt = Ui.TextButton("Test prompt", () => { });
        tryPrompt.Click += async (_, _) =>
        {
            if (service.Engines.Find(promptPack) is not { } target || string.IsNullOrWhiteSpace(sample.Text)) return;
            tryPrompt.IsEnabled = false;
            response.IsVisible = true;
            response.Text = "Working…";
            try
            {
                var address = ComfyServerAddress.Parse(url.Text ?? "");
                var server = address.ToString();
                var trial = service.SettingsPreview(() => server);
                var trialSettings = new Settings();
                SavePromptDraft();
                ReadEnhancer(trialSettings);
                trialSettings.EnhancerInstructions = new(instructionDrafts);
                var environment = keyEnvironment.Text?.Trim();
                var typedKey = sessionKey.Text?.Trim();
                trial.ApiKey = () => !string.IsNullOrEmpty(typedKey) ? typedKey :
                    (!clearKey && Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("Comfy", address.HttpBase)) : null)
                    ?? (!clearKey && server == live.ConnectedServerUrl ? live.SessionApiKey : null)
                    ?? (string.IsNullOrWhiteSpace(environment) ? null : Environment.GetEnvironmentVariable(environment));
                var direct = string.IsNullOrWhiteSpace(directKey.Text) ? forgetDirect ? "" : null : directKey.Text.Trim();
                response.Text = await trial.EnhanceAsync(trialSettings, target, AiTaskKind.ImageEdit, sample.Text, lifetime.Token, direct);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { response.Text = error.Message; }
            finally { tryPrompt.IsEnabled = true; }
        };
        var testProvider = new Expander { Header = "Try a prompt", Content = Ui.Column(8, sample, tryPrompt, response) };
        void TestLabel() => tryPrompt.Content = selectedProvider == "comfy" ? "Test with Comfy credits" : "Test prompt";
        provider.SelectionChanged += (_, _) => TestLabel();
        TestLabel();
        var readiness = Ui.Label(status.Text ?? "", Palette.Secondary);
        readiness.HorizontalAlignment = HorizontalAlignment.Left;
        readiness.MaxWidth = 450; readiness.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) readiness.Text = status.Text; };
        var compact = owner.Bounds.Width < 720;
        Control Page(string title, params Control[] controls)
        {
            var column = Ui.Column(12, controls);
            column.Children.Insert(0, Ui.Label(title, size: 20, weight: Avalonia.Media.FontWeight.SemiBold));
            column.Margin = new Thickness(compact ? 4 : 18, 8, 8, 12);
            return new ScrollViewer { Name = "SettingsPage", Content = column, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }
        TabItem Nav(string title, Control content)
        {
            var tab = new TabItem { Header = title, Content = content, Padding = new Thickness(8, 7) };
            tab.HeaderTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((label, _) =>
                new TextBlock { Text = label, FontSize = 13, FontWeight = Avalonia.Media.FontWeight.Medium,
                    MinWidth = compact ? 0 : 130, VerticalAlignment = VerticalAlignment.Center });
            return tab;
        }
        foreach (var section in new[] { assignments, files, credentials, advancedPrompt, extraPrompt, budget, testProvider })
        {
            section.HorizontalAlignment = HorizontalAlignment.Stretch;
            section.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            section.Background = Palette.Panel;
            section.Padding = new Thickness(12, 8);
        }
        var groupedPanels = new CheckBox { Content = "Group side panels in tabs", IsChecked = settings.TabbedPanels };
        var linkedSources = new CheckBox { Content = "Automatically refresh linked files", IsChecked = settings.AutoUpdateLinkedSources };
        var updates = new CheckBox { Content = "Check for updates on startup", IsChecked = settings.CheckForUpdates };
        var body = new TabControl
        {
            Width = Math.Clamp(owner.Bounds.Width - 64, 520, 780), Height = Math.Clamp(owner.Bounds.Height - 150, 300, 450),
            Name = "AiSettingsTabs", TabStripPlacement = compact ? Dock.Top : Dock.Left,
            ItemsSource = new[] {
                Nav("Connection", Page("Connection", connection, credentials)),
                Nav("Image models", Page("Image models", Ui.Row(8, engine, useDefault), readiness, assignments, files, showContext)),
                Nav("Prompt enhancer", Page("Prompt enhancer", EnhancerFields(("Provider", provider)), chatFields, apiFields,
                    serverFields, budget, EnhancerFields(("Image model", promptPicker)), advancedPrompt, extraPrompt, testProvider)),
                Nav("Editor", Page("Editor", groupedPanels, linkedSources, updates))
            }, SelectedIndex = Math.Clamp(page, 0, 3)
        };
        files.IsExpanded = focusField == "files";
        var validation = Ui.Label("", Avalonia.Media.Brushes.Orange);
        validation.IsVisible = false; validation.MaxWidth = 520; validation.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        settingsDialog = new DialogWindow("Settings", Ui.Column(5, body, validation), "Save settings");
        settingsDialog.Opened += (_, _) =>
        {
            if (focusField == "key") sessionKey.Focus();
            else if (focusField == "server") url.Focus();
            else if (page == 1) engine.Focus();
            else if (page == 2) provider.Focus();
        };
        settingsDialog.Closed += (_, _) => lifetime.Cancel();
        settingsDialog.ValidateAccept = () =>
        {
            TextBox field = url;
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
                if (Settings.Persist)
                {
                    var changes = new Dictionary<string, string?>();
                    if (clearKey || !string.IsNullOrWhiteSpace(sessionKey.Text))
                        changes[UserApiKeys.Target("Comfy", address.HttpBase)] = string.IsNullOrWhiteSpace(sessionKey.Text) ? null : sessionKey.Text.Trim();
                    if (forgetDirect || !string.IsNullOrWhiteSpace(directKey.Text))
                        changes[UserApiKeys.Target("Direct", directEndpoint!)] = string.IsNullOrWhiteSpace(directKey.Text) ? null : directKey.Text.Trim();
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
                validation.Text = error.Message; validation.IsVisible = true;
                if (error is FormatException)
                {
                    body.SelectedIndex = tab;
                    field.Focus(); field.SelectAll();
                }
                return false;
            }
        };
        RefreshStatus();
        if (!await settingsDialog.Ask(owner)) return false;
        settings.ComfyServerUrl = ComfyServerAddress.Parse(url.Text ?? "").ToString();
        settings.ComfyConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
        service.ConnectionTimeoutSeconds = settings.ComfyConnectionTimeoutSeconds;
        service.SelectedEngine = service.Engines.Find(defaultEngineId);
        live.AcceptSettingsPreview(service, settings.ComfyServerUrl);
        settings.ComfyApiKeyEnvironment = keyEnvironment.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(sessionKey.Text)) live.SessionApiKey = sessionKey.Text.Trim();
        else if (clearKey) live.SessionApiKey = null;
        settings.AiEngineId = service.SelectedEngine?.Id;
        settings.AiTaskEngineIds = taskDrafts;
        settings.TabbedPanels = groupedPanels.IsChecked == true;
        settings.AutoUpdateLinkedSources = linkedSources.IsChecked == true;
        settings.CheckForUpdates = updates.IsChecked == true;
        settings.AiShowContextBounds = showContext.IsChecked == true;
        SavePromptDraft(); settings.AiPackPrompts = promptDrafts;
        ReadEnhancer(settings);

        models.Save();
        settings.Save();
        return true;
    }

    public static async Task<bool> Advanced(Window owner, Settings settings, AiTaskService? service = null, AiTaskKind task = AiTaskKind.GenerativeFill, AiOperationSettings? initialProfile = null, Action<AiOperationSettings>? acceptDraft = null)
    {
        var paid = service?.SelectedEngine?.PaidApi == true;
        var profile = initialProfile ?? settings.OperationFor(service?.SelectedEngine?.Id, task, paid);
        var scope = $"{service?.SelectedEngine?.Id}/{task}";
        var presets = new Dictionary<string, AiOperationSettings>(settings.GenerationPresets.GetValueOrDefault(scope) ?? [], StringComparer.OrdinalIgnoreCase);
        var modified = false;
        var editor = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Func<AiOperationSettings> read;
        editor.Content = GenerationFields(profile, service, task, paid, out read);
        var list = new ComboBox { MinWidth = 165, PlaceholderText = "Choose preset" };
        AiGenerationControls.StyleModelField(list, "Generation preset");
        var name = new TextBox { Width = 165, MaxLength = 70, PlaceholderText = "Preset name" };
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
            editor.Content = GenerationFields(saved, service, task, paid, out read);
        };
        delete.IsEnabled = save.IsEnabled = false;
        name.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        var library = new Expander { Header = "Presets", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Ui.Column(6, Ui.Row(8, list, delete), Ui.Row(8, name, save)) };
        var body = new ScrollViewer { Content = Ui.Column(12, editor, library), MaxHeight = Math.Clamp((owner.Owner?.Bounds.Height ?? owner.Bounds.Height) - 160, 300, 650),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (!await new DialogWindow($"{task.DisplayName()} · Generation settings", body).Ask(owner)) return false;
        var updated = read();
        if (modified)
        {
            if (presets.Count == 0) settings.GenerationPresets.Remove(scope);
            else settings.GenerationPresets[scope] = presets;
        }
        if (acceptDraft != null) acceptDraft(updated);
        else settings.SetOperation(service?.SelectedEngine?.Id, task, updated);
        if (modified || acceptDraft == null) settings.Save();
        return true;
    }

    private static Control GenerationFields(AiOperationSettings profile, AiTaskService? service, AiTaskKind task, bool paid, out Func<AiOperationSettings> read)
    {
        var mp = ClosestMegapixels(profile.Megapixels); var originalSize = profile.OriginalSize;
        var reference = profile.ReferenceMegapixels;
        var fluxMemory = profile.FluxMemory is "reduced" or "standard" ? profile.FluxMemory : "auto";
        var seed = profile.Seed; var mode = profile.VariantMode;
        var quality = profile.ApiQuality;
        var model = service?.SelectedEngine?.ApiModel ?? "gpt-image-2.5-sunburst";
        var nano=NanoBanana.IsModel(model);
        var qwen=service?.SelectedEngine?.Id==QwenImageWorkflow.EngineId;
        var sequential=nano || qwen || task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground;
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
            ("Seed", Ui.Row(6, Ui.Number(seed, -1, long.MaxValue, value => seed = (long)value, 1, "0", 150), Ui.Label("-1 = random")))];
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
        if (paid)
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
                var toggle = Ui.Check("", draft.Enabled, value => loras[index] = loras[index] with { Enabled = value }); ToolTip.SetTip(toggle, "Enable this LoRA");
                var strength = Ui.SliderField("", Math.Clamp(draft.Strength, 0, 3), 0, 3, value => loras[index] = loras[index] with { Strength = value }, 0.01, "0.00", 100, reset: 1);
                loras[index] = loras[index] with { Strength = Math.Clamp(draft.Strength, 0, 3) };
                rows.Children.Add(Ui.Column(3, Ui.Label($"LoRA {i + 1}", Palette.Secondary), Ui.Row(7, toggle, combo, strength)));
            }
            extra.Children.Add(rows);
            if (names.Count == 0) extra.Children.Add(Ui.Label("Connect / refresh models to read the server's LoRA list.", Palette.Secondary));
        }
        if (task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight)
            note.Text = "Lighting preserves the source silhouette. A separate shadow layer uses darkening generated near the subject's support. Undo restores the original.";
        else if (task == AiTaskKind.ChangeBackground)
            note.Text = "Two passes create the scene and remove the temporary generated subject. Your original subject stays on a separate layer. Paid models bill both passes.";
        if(qwen) note.Text += " Qwen 2.1 Turbo uses its fixed 8-step schedule at full denoise, automatic KV cache and sequential variants. Technical padding to multiples of 32 is removed on insertion.";
        read = () => profile with
        {
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
        var key = new TextBox { Width = 390, PasswordChar = '●', PlaceholderText = "Comfy.org API key", Name = "ComfyApiKeyEntry" };
        var note = Ui.Label("Composa saves your Comfy.org key in your user profile for this server. Manage it in AI Settings → Connection → Comfy credits.", Palette.Secondary);
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

    public static async Task<bool> Upscale(Window owner, Settings settings, int width, int height, bool selection, EngineProfile? engine = null, AiTaskService? service = null)
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
        Control picker = selected == null ? Ui.Label("Refresh Models on ComfyUI to read available upscale models.") : Ui.Combo(models.ToArray(), selected,
            choice => choice.ToString(), choice => { selected = choice; engine = choice.Engine; EngineNote(); }, 430);
        if (picker is ComboBox upscaleModels) AiGenerationControls.StyleModelField(upscaleModels, "Upscale model");
        ToolTip.SetTip(picker, note);
        note.MaxWidth = 450; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var upscaleDialog = new DialogWindow("AI Upscale", Ui.Column(12, CanvasDialogs.Form(("Model", picker), ("Scale", scale)), dimensions), "Upscale") { CanAccept = selected != null };
        if (!await upscaleDialog.Ask(owner)) return false;
        if (selected != null)
        {
            var choices = new Dictionary<string, string>(savedModels, StringComparer.Ordinal) { [selected.Slot.Key] = selected.Model };
            settings.SetComfyModels(settings.ComfyServerUrl, choices);
        }
        if (engine != null) settings.AiTaskEngineIds[nameof(AiTaskKind.Upscale)] = engine.Id;
        profile = settings.OperationFor(engine?.Id, AiTaskKind.Upscale, engine?.PaidApi == true);
        settings.SetOperation(engine?.Id, AiTaskKind.Upscale, profile with { UpscaleFactor = factor }); settings.Save(); return true;
    }

    public static async Task<AiPromptResult?> Prompt(Window owner, AiTaskKind task, Settings settings, int documentWidth, int documentHeight, string initialPrompt = "", AiTaskService? service = null, int referenceCount = 0, bool hasSelection = false, AiReferenceEditor? references = null,
        SKRectI? sourceCanvas = null, SKRectI? selectionBounds = null)
    {
        var previousEngine = service?.SelectedEngine;
        if (service != null) service.SelectedEngine = service.EngineFor(task);
        var profile = settings.OperationFor(service?.SelectedEngine?.Id, task, service?.SelectedEngine?.PaidApi == true);
        var scene=profile.Scene.Normalized();
        var lighting=Ui.Combo(Enum.GetValues<RelightPreset>(),scene.Preset,SceneEditSettings.Name,value=>scene=scene with {Preset=value},300);
        var scope=Ui.Combo(Enum.GetValues<RelightScope>(),scene.Scope,value=>value==RelightScope.Subject ? "Subject" : "Whole visible image",value=>scene=scene with {Scope=value},300);
        var profileDrafts = new Dictionary<string, AiOperationSettings>();
        var rememberedPrompt = task switch { AiTaskKind.GenerateImage => settings.GenerateImagePrompt, AiTaskKind.ImageEdit => settings.ImageEditPrompt, _ => "" };
        var prompt = new TextBox { Text = string.IsNullOrEmpty(initialPrompt) ? rememberedPrompt : initialPrompt,
            AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Width = 430, Height = 100, PlaceholderText = "Describe the result" };
        var originalSize = profile.OriginalSize; var mp = ClosestMegapixels(profile.Megapixels);
        var variants = Math.Clamp(profile.Variants, 1, 3); var quality = profile.ApiQuality;
        var expansionMode = hasSelection ? AiExpansionMode.MaskedRegion : profile.ExpansionMode;
        var regionSide = profile.ExpansionMinimumSide; var wholeSide = profile.WholeExpansionMinimumSide;
        var width = documentWidth; var height = documentHeight;
        var dimensions = Ui.Label("", Palette.Secondary); dimensions.MaxWidth = 430; dimensions.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var note = Ui.Label("", Palette.Secondary); note.MaxWidth = 430; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var cost = Ui.Label("", Palette.Secondary);
        var sizeOptions = new[] { "Original size" }.Concat(AiDimensions.MegapixelOptions.Select(AiDimensions.Label)).ToArray();
        DialogWindow? dialog = null;
        var alignment = AlignmentToggle(settings);
        var qualityHost = new StackPanel();
        var qualityOptions = new[] { "low", "medium", "high", "xhigh", "max" };
        var qualityCombo = new ComboBox { ItemsSource = qualityOptions, SelectedItem = qualityOptions.Contains(quality) ? quality : "low", Width = 150 };
        qualityCombo.SelectionChanged += (_, _) => { if (qualityCombo.SelectedItem is string value) { quality = value; Refresh(); } };
        qualityHost.Children.Add(CanvasDialogs.Form(("Quality / resolution", qualityCombo)));
        var size = Ui.Combo(sizeOptions, originalSize ? sizeOptions[0] : AiDimensions.Label(mp), value => value, value =>
        {
            originalSize = value == sizeOptions[0];
            if (!originalSize) mp = AiDimensions.MegapixelOptions.First(option => AiDimensions.Label(option) == value);
            Refresh();
        }, 150);
        var expandSize = new ComboBox { Width = 245 };
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
        var variantButtons = new AiVariantButtons(variants, value => { variants = value; Refresh(); });
        AiOperationSettings CaptureProfile() => profile with
        {
            Scene=scene.Normalized(),
            OriginalSize = originalSize, Megapixels = mp, Variants = variants, ApiQuality = quality,
            ExpansionMode = task == AiTaskKind.GenerativeExpand ? expansionMode : profile.ExpansionMode,
            ExpansionMinimumSide = regionSide, WholeExpansionMinimumSide = wholeSide
        };
        void LoadProfile(AiOperationSettings next)
        {
            scene=next.Scene.Normalized(); lighting.SelectedIndex=(int)scene.Preset; scope.SelectedIndex=(int)scene.Scope;
            profile = next; originalSize = next.OriginalSize; mp = ClosestMegapixels(next.Megapixels);
            variants = Math.Clamp(next.Variants, 1, 3); quality = next.ApiQuality;
            expansionMode = hasSelection ? AiExpansionMode.MaskedRegion : next.ExpansionMode;
            regionSide = next.ExpansionMinimumSide; wholeSide = next.WholeExpansionMinimumSide;
            qualityOptions = NanoBanana.IsModel(service?.SelectedEngine?.ApiModel) ? ["1K", "2K", "4K"] : ["low", "medium", "high", "xhigh", "max"];
            if (!qualityOptions.Contains(quality)) quality = qualityOptions[0];
            qualityCombo.ItemsSource = qualityOptions;
            qualityCombo.SelectedItem = quality;
            size.SelectedItem = originalSize ? sizeOptions[0] : AiDimensions.Label(mp);
            variantButtons.Count = variants; mode.SelectedIndex = expansionMode == AiExpansionMode.WholeImage ? 1 : 0;
            RefreshExpandChoices(); Refresh();
        }
        var advanced = AiGenerationControls.IconAction(Icons.Gear, "AiAdvanced", "Generation settings", () => { });
        advanced.Click += async (_, _) =>
        {
            await Advanced(dialog!, settings, service, task, CaptureProfile(), LoadProfile);
        };
        var modelHost = new StackPanel();
        var controls = Ui.Row(4);
        if (service != null && task != AiTaskKind.GenerativeExpand)
            controls.Children.Add(EnhanceButton(prompt, settings, service, task, () => service.SelectedEngine));
        controls.Children.Add(alignment);
        controls.Children.Add(advanced);
        if (service != null && service.Engines.Profiles.Where(pack => pack.Binding(task==AiTaskKind.MatchToScene ? AiTaskKind.Harmonize : task) != null).ToArray() is { Length: > 0 } packs)
        {
            var chosen = service.SelectedEngine;
            var choices = service.Engines.ImageModels(task == AiTaskKind.MatchToScene ? AiTaskKind.Harmonize : task,
                chosen, settings.ComfyModelsFor(settings.ComfyServerUrl));
            var picker = new ComboBox { Width = 210 };
            AiGenerationControls.PresentImageModels(picker, choices);
            picker.SelectedItem = choices.FirstOrDefault(m => m.Engine.Id == chosen?.Id) ?? choices.FirstOrDefault();
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not ImageModelChoice model) return;
                profileDrafts[service.SelectedEngine?.Id ?? "default"] = CaptureProfile();
                service.SelectedEngine = model.Engine;
                LoadProfile(profileDrafts.GetValueOrDefault(model.Engine.Id) ?? settings.OperationFor(model.Engine.Id, task, model.Engine.PaidApi));
            };
            modelHost.Children.Add(Ui.Row(10, picker, controls));
        }
        else modelHost.Children.Add(controls);
        var resolution = task == AiTaskKind.GenerativeExpand
            ? hasSelection ? CanvasDialogs.Form(("Generation size", expandSize)) : CanvasDialogs.Form(("Expand mode", mode), ("Generation size", expandSize))
            : CanvasDialogs.Form(("Image size", size));
        void Refresh()
        {
            var toneOnly=task==AiTaskKind.MatchToScene && scene.MatchMode==SceneMatchMode.ToneAndColor;
            var paid = service?.SelectedEngine?.PaidApi == true; qualityHost.IsVisible = paid && !toneOnly;
            prompt.IsEnabled=!toneOnly;
            alignment.IsVisible = !toneOnly && task != AiTaskKind.GenerateImage;
            var nano=NanoBanana.IsModel(service?.SelectedEngine?.ApiModel);
            var qwen=service?.SelectedEngine?.Id==QwenImageWorkflow.EngineId;
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
                var api = paid && !nano ? PartnerImageSize.Plan(width, height,
                    (task == AiTaskKind.GenerativeExpand ? (expansionMode == AiExpansionMode.WholeImage ? wholeSide : regionSide) == 0 : originalSize)
                        ? Composa.Model.DocumentLimits.MinimumGenerationPixels : PartnerImageSize.MinimumPixels) : (width, height);
                dimensions.Text = $"{width} × {height} px · proportions preserved" + (nano ? $"\nNano Banana: {quality} · {NanoBanana.Aspect(width,height)}; technical padding removed on insertion." : paid && api != (width, height) ? $"\nGPT request: {api.Item1} × {api.Item2}; uniform fitting, no stretching." : "");
                if (!paid && (qwen || service?.SelectedEngine?.Id == "flux2-klein-intel-xpu") && sourceCanvas is { } canvas && selectionBounds is { } selection
                    && task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject)
                {
                    var contextBounds = AutomaticAiMask.Geometry(selection,canvas).Bounds;
                    var generated = AiContextGeometry.FluxSize(contextBounds,selection,width,height,originalSize).Padded;
                    if(qwen) generated=((generated.Width+31)/32*32,(generated.Height+31)/32*32);
                    dimensions.Text = $"{(qwen ? "Qwen" : "FLUX")} request including context: {generated.Width} × {generated.Height} px · {generated.Width * (double)generated.Height / 1_000_000:0.##} MP";
                }
                if (dialog != null) dialog.CanAccept = true;
            }
            catch (Exception error) { dimensions.Text = error.Message; if (dialog != null) dialog.CanAccept = false; }
            var sourceCount = task == AiTaskKind.GenerateImage ? 0 : 1;
            var count = sourceCount + (references?.Count ?? referenceCount);
            var estimate=paid?PartnerPricing.Estimate(service?.ServerCapabilities,service!.SelectedEngine!.ApiModel!,quality,"Custom",count,variants):null;
            if(task==AiTaskKind.ChangeBackground && estimate!=null)
            {
                var cleanupEstimate=PartnerPricing.Estimate(service?.ServerCapabilities,service!.SelectedEngine!.ApiModel!,quality,"Custom",1,variants);
                estimate=cleanupEstimate==null?null:new(estimate.MinimumUsd+cleanupEstimate.MinimumUsd,estimate.MaximumUsd+cleanupEstimate.MaximumUsd);
            }
            cost.Text = toneOnly ? "Local Curves correction · no generation or Comfy credits" : paid ? estimate?.Label ?? "Paid API · estimate unavailable" : "Local generation · no Comfy credits";
            note.Text = nano ? "Nano Banana 2 · Gemini 3.1: 1K / 2K / 4K. Variants run sequentially and are billed individually. Undo does not refund credits."
                : paid ? "GPT: 1:3–3:1, up to 3840 px / 8.29 MP. Quality affects detail, time and price. Undo does not refund credits."
                : task == AiTaskKind.ChangeBackground ? "Your original subject stays on a separate layer. Paid models bill two passes."
                : task is AiTaskKind.Harmonize or AiTaskKind.MatchToScene ? "Match the subject to the scene. Shadow and lighting controls are in Advanced."
                : task == AiTaskKind.Relight ? "Relight the subject or whole image. Fine controls are in Advanced."
                : task == AiTaskKind.GenerativeExpand ? hasSelection
                    ? "Fill only the selection with surrounding context. Existing canvas size is preserved; the expansion instruction is automatic."
                    : "Empty-area mode sends a soft mask plus context and preserves existing pixels. Whole-image mode may redraw everything. The generated patch is fitted back without changing the requested canvas size."
                : hasSelection ? "Original size keeps source pixels. For FLUX and Qwen, MP sets the total request area including surrounding context. Seed and execution mode are in Advanced."
                    : "Original size follows the canvas; MP scales its area while keeping proportions. Seed and execution mode are in Advanced.";
            ToolTip.SetTip(note, paid ? "Explicit Custom dimensions, multiples of 16; never Auto or aspect stretching. Generation uses the workflow model settings." : null);
            if(toneOnly) {dimensions.Text="Editable Match Light and Match Color curves";note.Text="Matches tone and neutral color balance locally. It does not generate lighting or shadows.";}
        }
        prompt.IsVisible = task != AiTaskKind.GenerativeExpand;
        var promptSection = prompt; promptSection.IsVisible = prompt.IsVisible;
        var content = Ui.Column(10, promptSection, modelHost);
        if(task==AiTaskKind.Relight) content.Children.Insert(1,Section("Lighting",CanvasDialogs.Form(("Lighting preset",lighting),("Target",scope))));
        if(task==AiTaskKind.ChangeBackground) prompt.PlaceholderText="Describe the background, surface and lighting";
        if (references != null) content.Children.Add(references.View);
        content.Children.Add(Section("Output", resolution, dimensions, qualityHost, cost));
        ToolTip.SetTip(dimensions, note);
        var body = new ScrollViewer { Content = content, MaxHeight = Math.Clamp(owner.Bounds.Height - 170, 300, 650), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        dialog = new DialogWindow(task.DisplayName(), body, "Run"); dialog.UseGenerationVariants(variantButtons);
        if (references != null)
        {
            references.Owner = dialog;
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
        return new(task == AiTaskKind.GenerativeExpand ? AiPromptDefaults.Expand : prompt.Text ?? "", width, height, profile.Seed, chosenEngine?.Id);
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
