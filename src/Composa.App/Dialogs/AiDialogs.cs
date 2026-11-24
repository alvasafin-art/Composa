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
using static Composa.App.Dialogs.StudioSettingsLayout;

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
        var promptSettings = new MenuItem { Header = Locale.T("Prompt enhancer") };
        promptSettings.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(button) is MainWindow window)
                await window.ShowAiSettings(2, operation: operation());
        };
        button.ContextMenu = new ContextMenu { Items = { promptSettings } };
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

    public static async Task<bool> SettingsDialog(Window owner, Settings settings, AiTaskService service, int page = 0,
        string? focusField = null, AiTaskKind? initialOperation = null, bool firstRun = false, Action? firstRunBack = null)
    {
        var live = service;
        service = live.SettingsPreview();
        using var lifetime = new CancellationTokenSource();
        DialogWindow? settingsDialog = null;
        var showContext = new CheckBox { Content = Locale.T("Show AI context bounds after selection"), IsChecked = settings.AiShowContextBounds };
        var taskDrafts = new Dictionary<string, string>(settings.AiTaskEngineIds);
        var url = new TextBox { Text = settings.ComfyServerUrl };
        var imageConnection = new ImageConnectionFields(settings, service, url, lifetime.Token, firstRun);
        var timeout = Ui.Number(settings.ComfyConnectionTimeoutSeconds, 1, 120, _ => { }, 1, "0", double.NaN);
        timeout.HorizontalAlignment = HorizontalAlignment.Stretch;
        var keyEnvironment = new TextBox { Text = settings.ComfyApiKeyEnvironment };
        var sessionKey = new TextBox { PasswordChar = '●', PlaceholderText = Locale.T("Enter a new key to save or replace it") };
        var clearKey = false;
        var clear = Ui.TextButton("Forget saved key", () => { sessionKey.Text = ""; clearKey = true; });
        var keyNote = Ui.Label(Locale.T("Partner Nodes use a Comfy.org API key, not an OpenAI key. Create one at platform.comfy.org. Keys are saved in your user profile for this server. On Windows, Composa uses Credential Manager. The environment variable is also supported. Use HTTPS for servers outside a trusted LAN."), Palette.Secondary);
        keyNote.HorizontalAlignment = HorizontalAlignment.Left;
        keyNote.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var directGenerator = DirectImageGenerator.For(settings, initialOperation ?? AiTaskKind.GenerateImage);
        EngineProfile[] PromptProfiles() => service.Engines.Profiles.Concat(imageConnection.PromptProfiles()).ToArray();
        var operationEngine = initialOperation is { } openingTask ? settings.EngineForTask(openingTask) : null;
        var promptPack = directGenerator.Route == ImageGenerationRoute.ComfyWorkflow
            ? service.Engines.Profiles.FirstOrDefault(pack => pack.Id == operationEngine) ?? service.SelectedEngine
            : directGenerator.PromptProfile();
        promptPack ??= PromptProfiles().FirstOrDefault();
        var instructionDrafts = new Dictionary<string, string>(settings.EnhancerInstructions);
        var instructions = new TextBox { Name = "EnhancerInstructions", AcceptsReturn = true, Height = 260, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = service.InstructionsFor(instructionDrafts, promptPack, AiTaskKind.GenerateImage) };
        var otherInstructions = new TextBox { Name = "EnhancerEditInstructions", AcceptsReturn = true, Height = 260, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = service.InstructionsFor(instructionDrafts, promptPack, AiTaskKind.ImageEdit) };
        Action? refreshInstructions = null;
        var model = new ComboBox { ItemsSource = service.ChatModels.Append(settings.EnhancerModel).Distinct().ToArray(),
            SelectedItem = settings.EnhancerModel, MinWidth = 180 };
        AiGenerationControls.StyleModelField(model, "Prompt enhancer model");
        var tokens = Ui.Number(settings.EnhancerMaxTokens, 16, 16384, _ => { }, 1, "0", double.NaN);
        var effort = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var preferredEffort = settings.EnhancerEffort;
        bool updatingEffort = false;
        effort.SelectionChanged += (_, _) =>
        {
            if (!updatingEffort && effort.SelectedItem is string selected) preferredEffort = selected;
        };
        tokens.HorizontalAlignment = HorizontalAlignment.Stretch;
        var apiUrl = new TextBox { Text = settings.EnhancerApiUrl };
        var apiModel = new TextBox { Text = settings.EnhancerApiModel, PlaceholderText = Locale.T("Model ID") };
        var apiName = new TextBox { Text = settings.EnhancerApiDisplayName, PlaceholderText = Locale.T("Direct text API") };
        var selectedProvider = settings.EffectiveEnhancerProvider();
        var serverModels = new Dictionary<string,string>(settings.EnhancerServerModels);
        var remoteCli = AiTaskService.IsRemoteCli(selectedProvider) ? selectedProvider : settings.EnhancerRemoteCli;
        if (!AiTaskService.IsRemoteCli(remoteCli)) remoteCli = "codex";
        var localCodex = new CodexConnectionFields(settings.EnhancerCodexPath, settings.EnhancerCodexModel, showModel: false, firstRun: firstRun);
        var serverModel = new TextBox { PlaceholderText = Locale.T("Model ID"), Text = serverModels.GetValueOrDefault(selectedProvider) };
        var serverUrl = new TextBox { Text = settings.EnhancerServerApiUrl };
        var serverHelp = Ui.Label("", Palette.Secondary);
        serverHelp.MaxWidth = 435; serverHelp.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var apiMode = Ui.Combo(new[] { "auto", "chat", "responses" }, settings.EnhancerServerApiMode, Locale.T, _ => { }, 130);
        var textModelField = new Border();
        var savedLocalModel = SetupInput(new ComboBox { PlaceholderText = Locale.T("Choose a model") });
        AiTaskService.ServerTextModel[] catalogue = [];
        var comfyEfforts = service.ChatReasoningEfforts;
        string[]? directEfforts = null;
        var detected = new ComboBox { IsVisible = false, MinWidth = 160 };
        AiGenerationControls.StyleModelField(detected, "Detected server model");
        var listedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        detected.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((id, _) =>
        {
            var title = listedNames.GetValueOrDefault(id ?? "", id ?? "");
            return firstRun ? Ui.Label(title == id ? title : $"{title} · {id}", size: 11) : Caption(title);
        });
        detected.SelectionBoxItemTemplate = detected.ItemTemplate;
        detected.SelectionChanged += (_, _) =>
        {
            if (detected.SelectedItem is not string id) return;
            if (selectedProvider == "local-codex") localCodex.Model.Text = id;
            else if (selectedProvider == "direct") apiModel.Text = id;
            else serverModel.Text = id;
            UpdateEfforts();
        };
        void UpdateEfforts()
        {
            var current = preferredEffort;
            var id = selectedProvider switch
            {
                "local-codex" => localCodex.Model.Text, "direct" => apiModel.Text,
                "comfy" => model.SelectedItem as string, _ => serverModel.Text
            };
            var supported = selectedProvider switch
            {
                "comfy" => comfyEfforts, "direct" => directEfforts, _ => catalogue.FirstOrDefault(m => m.Id == id)?.Efforts
            };
            var options = new[] { "default" }.Concat(supported ?? []).Distinct().ToArray();
            if (supported == null && current != "default" && catalogue.Length == 0)
                options = [.. options, current];
            updatingEffort = true;
            effort.ItemsSource = options;
            effort.SelectedItem = options.Contains(current) ? current : "default";
            effort.IsEnabled = options.Length > 1;
            updatingEffort = false;
        }
        serverModel.TextChanged += (_, _) => UpdateEfforts();
        void PresentTextCatalog(AiTaskService.ServerTextModel[] models, string? current, string? preferred)
        {
            catalogue = models;
            var ids = models.Select(entry => entry.Id).ToArray();
            var selected = ids.Contains(current) ? current : ids.Contains(preferred) ? preferred : null;
            detected.ItemsSource = ids;
            detected.PlaceholderText = Locale.T("Choose a model");
            detected.SelectedItem = selected;
            detected.IsVisible = true;
            textModelField.Child = detected;
            UpdateEfforts();
        }
        Grid EnhancerFields(params (string Label, Control Field)[] rows)
        {
            var form = CanvasDialogs.Form(rows);
            form.ColumnDefinitions[0].Width = new GridLength(138);
            return form;
        }
        Action? showComfyKey = null;
        var chatFields = Ui.Row(5, Ui.TextButton("Manage Comfy.org key…", () => showComfyKey?.Invoke()));
        var enhancerServer = new TextBox { Text = url.Text, Name = "EnhancerComfyServer" };
        enhancerServer.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty && enhancerServer.Text != url.Text) url.Text = enhancerServer.Text;
        };
        url.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty && enhancerServer.Text != url.Text) enhancerServer.Text = url.Text;
        };
        var serverAddress = firstRun ? SetupColumns(SetupField("ComfyUI server", SetupInput(enhancerServer)))
            : Columns("*,*", Field("ComfyUI server", enhancerServer));
        var directKey = new TextBox { Name = "TextApiKey", PasswordChar = '●', PlaceholderText = Locale.T("Enter a key to save or replace it") };
        var forgetDirect = false;
        var clearDirect = Ui.TextButton("Forget saved key", () => { directKey.Text = ""; forgetDirect = true; });
        var textCatalog = new ConnectionModelCatalog(settings.EnhancerApiCatalogs, apiUrl, directKey, apiModel, () =>
        {
            var endpoint = Assistant.ChatCompletionAssistantProvider.Endpoint(apiUrl.Text ?? "");
            return (endpoint, new Uri(endpoint.AbsoluteUri[..^"/chat/completions".Length] + "/models"));
        }, endpoint => !string.IsNullOrWhiteSpace(directKey.Text) ? directKey.Text.Trim()
            : ((!forgetDirect && Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("Direct", endpoint)) : null)
                ?? Environment.GetEnvironmentVariable("AVS_COMPATIBLE_API_KEY")), lifetime.Token, autoProbe: !firstRun);
        clearDirect.Click += (_, _) => { textCatalog.InvalidateCheck(); textCatalog.ScheduleCheck(); };
        void UpdateDirectEfforts()
        {
            directEfforts = textCatalog.ReasoningFor(apiModel.Text ?? "");
            UpdateEfforts();
        }
        apiModel.TextChanged += (_, _) => UpdateDirectEfforts();
        textCatalog.CheckChanged += UpdateDirectEfforts;
        var apiFields = firstRun ? Ui.Column(12, SetupColumns(
            SetupField("API base URL", SetupInput(apiUrl)), SetupField("API key", SetupInput(directKey))), clearDirect)
            : ApiConnectionForm(apiName, new TextBox { Text = Locale.T("OpenAI chat completions"), IsReadOnly = true },
                apiUrl, directKey, clearDirect, textCatalog.View);
        Locale.Tip(apiUrl, "Optional bearer key: AVS_COMPATIBLE_API_KEY");
        var serverApiFields = EnhancerFields(("Server API base URL", serverUrl), ("API mode", apiMode));
        var serverFields = Ui.Column(6, serverApiFields, serverHelp);
        var reasoning = firstRun ? null : SetupField("Reasoning effort", SetupInput(effort));
        var budget = SetupField("Token limit", SetupInput(tokens));
        string[] remoteAdapters = ["codex", "claude", "kimi"];
        var remotePicker = Ui.Combo(remoteAdapters, remoteCli, CliLabel, _ => { }, double.NaN);
        Control remoteFields = firstRun ? new Border() : Field("CLI", remotePicker);
        var setupKind = new Border();
        var setupKindLabel = SetupLabel("CLI");
        var localCliPicker = SetupInput(Ui.Combo(new[] { "Codex CLI" }, "Codex CLI", value => value, _ => { }, double.NaN));
        var remoteHelp = RemoteEnhancerHelp();
        var operationTextModel = Caption("");
        var textConnectionCheck = new ConnectionCheck(lifetime.Token, CheckTextConnection, "", showHelperWarning: true,
            buttonText: firstRun ? "Get available models" : "Check connection", inlineAction: firstRun, automatic: !firstRun);
        void PresentDirectCatalog()
        {
            if (selectedProvider != "direct") return;
            var items = textCatalog.Models;
            listedNames.Clear();
            foreach (var item in items) listedNames[item.Id] = item.Label;
            var actual = (apiModel.Text ?? "").Trim();
            PresentTextCatalog(items.Select(item => new AiTaskService.ServerTextModel(item.Id, textCatalog.ReasoningFor(item.Id)))
                .Append(new(actual, textCatalog.ReasoningFor(actual))).Where(item => item.Id.Length > 0).DistinctBy(item => item.Id).ToArray(),
                actual, null);
        }
        if (!firstRun)
        {
            textCatalog.CheckChanged += () => { if (textCatalog.Connected == true) PresentDirectCatalog(); };
            textCatalog.Changed += () => { if (textCatalog.CanFetch && textCatalog.Models.Count > 0) PresentDirectCatalog(); };
        }
        async Task<string> CheckTextConnection(CancellationToken token)
        {
            listedNames.Clear();
            if (selectedProvider == "local-codex")
            {
                var reply = await LocalCodex.DiscoverModelsAsync(localCodex.Path.Text ?? "", token);
                token.ThrowIfCancellationRequested();
                foreach (var item in reply) listedNames[item.Id] = item.Title;
                PresentTextCatalog(reply.Select(value => new AiTaskService.ServerTextModel(value.Id, value.Efforts)).ToArray(),
                    localCodex.Model.Text, reply.FirstOrDefault(item => item.IsDefault)?.Id);
                return reply.Count == 0 ? "The CLI reported no models." : "Codex CLI is signed in. Models loaded.";
            }
            if (selectedProvider == "direct")
            {
                await textCatalog.FetchAsync(token);
                token.ThrowIfCancellationRequested();
                PresentDirectCatalog();
                return textCatalog.Models.Count == 0 ? "The API reported no models." : "The API responded. Models are available below.";
            }
            var adapter = selectedProvider;
            var address = url.Text ?? "";
            var probe = live.SettingsPreview(() => address);
            probe.ConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
            try { await probe.TestConnectionAsync(token); }
            catch (Exception error) when (AiTaskService.IsAuthorizationError(error))
            { throw new InvalidOperationException("ComfyUI refused authorization.", error); }
            catch (InvalidOperationException error) when (!token.IsCancellationRequested)
            { throw new InvalidOperationException("The ComfyUI server is unavailable.", error); }
            token.ThrowIfCancellationRequested();
            if (adapter == "comfy")
            {
                comfyEfforts = probe.ChatReasoningEfforts;
                var current = model.SelectedItem as string;
                model.ItemsSource = probe.ChatModels;
                model.SelectedItem = probe.ChatModels.Contains(current) ? current : null;
                model.PlaceholderText = Locale.T("Choose a model");
                return probe.ChatModels.Count == 0 ? "The server reported no models." : "Server responded. Models loaded.";
            }
            if (probe.ServerCapabilities?.NodeTypes.Contains("AVSPromptHelper") != true)
                throw new AiTaskService.PromptHelperMissingException();
            var remote = await AiTaskService.DiscoverServerModels(address, adapter, serverUrl.Text ?? "", token);
            token.ThrowIfCancellationRequested();
            PresentTextCatalog(remote, serverModel.Text, null);
            return remote.Length == 0 ? "The server reported no models." : "Server responded. Models loaded.";
        }
        foreach (var input in new[] { url, directKey, apiUrl, serverUrl, localCodex.Path, sessionKey, keyEnvironment })
            input.TextChanged += (_, _) => textConnectionCheck.Reset();
        model.SelectionChanged += (_, _) => UpdateEfforts();
        localCodex.Model.TextChanged += (_, _) => UpdateEfforts();
        void UpdateSummary()
        {
            var text = selectedProvider switch
            {
                "direct" => apiModel.Text, "comfy" => model.SelectedItem as string,
                "local-codex" => localCodex.Model.Text, _ => serverModel.Text
            };
            operationTextModel.Text = string.IsNullOrWhiteSpace(text) ? Locale.T("Choose a model") : text;
        }
        apiModel.TextChanged += (_, _) => UpdateSummary();
        localCodex.Model.TextChanged += (_, _) => UpdateSummary();
        serverModel.TextChanged += (_, _) => UpdateSummary();
        model.SelectionChanged += (_, _) => UpdateSummary();
        void SelectRemoteAdapter(string adapter)
        {
            remoteCli = adapter;
            if (!AiTaskService.IsRemoteCli(selectedProvider) || selectedProvider == adapter) return;
            serverModels[selectedProvider] = serverModel.Text?.Trim() ?? "";
            selectedProvider = adapter;
            serverModel.Text = serverModels.GetValueOrDefault(selectedProvider);
            ShowProvider();
        }
        remotePicker.SelectionChanged += (_, _) =>
        {
            if (remotePicker.SelectedIndex >= 0) SelectRemoteAdapter(remoteAdapters[remotePicker.SelectedIndex]);
        };
        void ShowProvider()
        {
            textConnectionCheck.Reset();
            apiFields.IsVisible = selectedProvider == "direct";
            chatFields.IsVisible = selectedProvider == "comfy";
            localCodex.View.IsVisible = selectedProvider == "local-codex";
            serverFields.IsVisible = !apiFields.IsVisible && !chatFields.IsVisible && !localCodex.View.IsVisible;
            serverAddress.IsVisible = chatFields.IsVisible || serverFields.IsVisible;
            remoteHelp.IsVisible = AiTaskService.IsRemoteCli(selectedProvider);
            remoteFields.IsVisible = !firstRun && remoteHelp.IsVisible;
            if (firstRun)
            {
                var cli = AiTaskService.IsRemoteCli(selectedProvider) || selectedProvider == "local-codex";
                setupKindLabel.Text = Locale.T(cli ? "CLI" : selectedProvider == "comfy" ? "Connection"
                    : selectedProvider == "direct" ? "Display name" : "API protocol");
                Avalonia.Automation.AutomationProperties.SetName(setupKind, setupKindLabel.Text);
                setupKind.Child = AiTaskService.IsRemoteCli(selectedProvider) ? remotePicker : selectedProvider == "local-codex"
                    ? localCliPicker : selectedProvider == "direct" ? apiName : SetupInput(new TextBox
                {
                    IsReadOnly = true, Text = Locale.T(selectedProvider == "comfy" ? "Comfy credits" : AiTaskService.Providers[selectedProvider])
                });
            }
            serverApiFields.IsVisible = selectedProvider == "compatible";
            serverHelp.Text = selectedProvider switch
            {
                "compatible" => "Requests originate on the ComfyUI server. Set AVS_COMPATIBLE_API_KEY there if required.",
                "openai" => "Uses OPENAI_API_KEY on the ComfyUI server.",
                _ => "Uses the CLI and its signed-in account on the ComfyUI server."
            };
            catalogue = [];
            UpdateEfforts();
            budget.IsEnabled = !localCodex.View.IsVisible;
            textModelField.Child = selectedProvider switch
            {
                "comfy" => model, "direct" => apiModel, "local-codex" => firstRun ? savedLocalModel : localCodex.Model, _ => serverModel
            };
            if (firstRun && selectedProvider == "local-codex")
            {
                var savedId = localCodex.Model.Text;
                savedLocalModel.ItemsSource = string.IsNullOrWhiteSpace(savedId) ? Array.Empty<string>() : new[] { savedId };
                savedLocalModel.SelectedItem = savedId;
            }
            listedNames.Clear();
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
        SetupInput(provider);
        SetupInput(remotePicker);
        SetupInput(detected);
        SetupInput(localCodex.Model);
        SetupInput(serverModel);
        SetupInput(apiModel);
        SetupInput(apiName);
        SetupInput(model);
        SetupInput(effort);
        effort.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) =>
            new TextBlock { Text = Locale.T(value switch
            {
                null or "default" => "Default", "none" => "No reasoning", "minimal" => "Minimal reasoning", "low" => "Low reasoning",
                "medium" => "Medium reasoning", "high" => "High reasoning", "xhigh" => "Extra high reasoning", _ => value
            }) });
        effort.SelectionBoxItemTemplate = effort.ItemTemplate;
        if (firstRun) Locale.Tip(textConnectionCheck.ActionButton, "Check connection");
        ShowProvider();

        void SavePromptDraft()
        {
            if (promptPack == null) return;
            Save(AiTaskKind.GenerateImage, instructions.Text ?? "");
            Save(AiTaskKind.ImageEdit, otherInstructions.Text ?? "");
            void Save(AiTaskKind task, string text)
            {
                var key = AiTaskService.InstructionKey(AiTaskService.InstructionScope(promptPack), task);
                var fallback = new Dictionary<string, string>(instructionDrafts);
                fallback.Remove(key);
                if (text == service.InstructionsFor(fallback, promptPack, task)) instructionDrafts.Remove(key);
                else instructionDrafts[key] = text;
            }
        }
        void LoadPromptDraft(EngineProfile? pack)
        {
            promptPack = pack;
            instructions.Text = service.InstructionsFor(instructionDrafts, pack, AiTaskKind.GenerateImage);
            otherInstructions.Text = service.InstructionsFor(instructionDrafts, pack, AiTaskKind.ImageEdit);
            refreshInstructions?.Invoke();
        }
        var status = Ui.Label(ConnectionLabel(service.ConnectionState), Palette.Secondary);
        status.HorizontalAlignment = HorizontalAlignment.Left;
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
        var comfyCheck = new ConnectionCheck(lifetime.Token, async token =>
        {
            var address = ComfyServerAddress.Parse(url.Text ?? "").ToString();
            var probe = service.SettingsPreview(() => address);
            probe.ConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
            try { await probe.TestConnectionAsync(token); }
            catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
            {
                service.AcceptSettingsPreview(probe, address);
                models.Refresh();
                RefreshStatus();
                throw;
            }
            token.ThrowIfCancellationRequested();
            service.AcceptSettingsPreview(probe, address);
            var selectedChat = model.SelectedItem as string ?? settings.EnhancerModel;
            model.ItemsSource = service.ChatModels.Append(selectedChat).Distinct().ToArray();
            model.SelectedItem = selectedChat;
            comfyEfforts = service.ChatReasoningEfforts;
            UpdateEfforts();
            models.Refresh();
            RefreshStatus();
            return "Connected";
        }, "", buttonText: "Test connection", automatic: !firstRun);
        url.TextChanged += (_, _) => comfyCheck.Reset();
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
        var promptPicker = new ComboBox
        {
            Name = "PromptInstructionModel",
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<EngineProfile>((pack, _) =>
                Caption(ModelTitle(pack!, pack!.Tasks.FirstOrDefault()?.Task ?? AiTaskKind.GenerateImage)))
        };
        var promptModelList = Ui.Column(2);
        var promptModelTitle = Heading(promptPack?.DisplayName ?? "Choose a model", 15);
        AiGenerationControls.StyleModelField(promptPicker, "Image model");
        void RefreshPromptProfiles(EngineProfile? preferred)
        {
            var profiles = PromptProfiles();
            var scope = AiTaskService.InstructionScope(preferred);
            var match = profiles.FirstOrDefault(pack => AiTaskService.InstructionScope(pack) == scope);
            if (match == null && preferred != null) profiles = [.. profiles, preferred];
            promptPicker.ItemsSource = profiles;
            promptPicker.SelectedItem = match ?? preferred ?? profiles.FirstOrDefault();
            promptModelList.Children.Clear();
            foreach (var pack in profiles)
            {
                var title = ModelTitle(pack, pack.Tasks.FirstOrDefault()?.Task ?? AiTaskKind.GenerateImage);
                var item = RowButton(Ui.Column(2, Heading(title, 11),
                    Caption(pack.Id.StartsWith("direct", StringComparison.Ordinal) ? pack.DisplayName :
                        pack.PaidApi ? "Comfy credits" : "ComfyUI server")), () => RefreshPromptProfiles(pack));
                if (Equals(promptPicker.SelectedItem, pack)) item.Background = Palette.SettingsSelection;
                promptModelList.Children.Add(item);
            }
        }
        RefreshPromptProfiles(promptPack);
        promptPicker.SelectionChanged += (_, _) =>
        {
            if (promptPicker.SelectedItem is not EngineProfile pack) return;
            SavePromptDraft();
            LoadPromptDraft(pack);
        };
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
        engine.IsEnabled = service.Engines.Profiles.Count > 0;
        var connection = Ui.Column(8,
            Columns("*,146", SetupField("ComfyUI Server URL", SetupInput(url)), SetupField("Connection timeout", SetupInput(timeout))),
            comfyCheck.View, status);
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
        }
        var files = Ui.Column(8, routePicker, models);
        var auth = Ui.Column(8, Columns("*,*", SetupField("Saved API key", SetupInput(sessionKey)),
            SetupField("Environment variable", SetupInput(keyEnvironment))), clear);
        var credentials = new Expander { Header = Locale.T("Comfy credits"), Content = Ui.Column(8, auth, keyNote), IsExpanded = focusField == "key" };
        var instructionFields = SetupColumns(SetupField("Instructions for image generation", instructions),
            SetupField("Instructions for image editing", otherInstructions));
        refreshInstructions = () => promptModelTitle.Text = promptPack == null
            ? Locale.T("Choose a model") : ModelTitle(promptPack, AiTaskKind.GenerateImage);
        refreshInstructions();
        var restoreInstructions = Ui.TextButton("Reset instructions for this model", () =>
        {
            var defaults = new Dictionary<string, string>();
            instructions.Text = service.InstructionsFor(defaults, promptPack, AiTaskKind.GenerateImage);
            otherInstructions.Text = service.InstructionsFor(defaults, promptPack, AiTaskKind.ImageEdit);
        });
        restoreInstructions.HorizontalAlignment = HorizontalAlignment.Left;
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
            target.EnhancerApiDisplayName = apiName.Text?.Trim() ?? "";
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
        var operationPicker = new ComboBox { Name = "ModelOperation", HorizontalAlignment = HorizontalAlignment.Stretch };
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
        Control Page(string title, params Control[] controls)
        {
            var column = Ui.Column(16, controls);
            if (title.Length > 0) column.Children.Insert(0, Heading(title, 25));
            column.Margin = new Thickness(34, 25, 34, 20);
            column.MaxWidth = 1158;
            return Scroll(column);
        }
        Control PageWithAction(string title, Button action, params Control[] controls) =>
            Page("", [Columns("*,Auto", Heading(title, 25), action), .. controls]);
        foreach (var section in new[] { credentials })
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
        const string rememberPromptsLabel = "Remember prompts for each AI operation";
        var rememberPrompts = new CheckBox { Name = "RememberAiPrompts", Content = Locale.T(rememberPromptsLabel), IsChecked = settings.RememberAiPrompts };
        Locale.Tip(rememberPrompts, "Prompts stay in memory until the editor closes. Turning this off clears the drafts.");
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
        Action<int>? openRelated = null;
        Action<EngineProfile?, AiTaskKind?>? openPrompt = null;
        var directPromptHost = new Border { Child = firstRun ? null : apiFields };
        var textConnectionHost = new Border();
        var cliPromptHost = new Border { Child = firstRun ? null : localCodex.View };
        directPromptHost.IsVisible = apiFields.IsVisible;
        cliPromptHost.IsVisible = localCodex.View.IsVisible;
        apiFields.PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.IsVisibleProperty) directPromptHost.IsVisible = apiFields.IsVisible;
        };
        localCodex.View.PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.IsVisibleProperty) cliPromptHost.IsVisible = localCodex.View.IsVisible;
        };
        var promptConfiguration = Ui.Column(firstRun ? 12 : 10,
            firstRun ? SetupColumns(SetupField("Method", provider), SetupField("CLI", setupKind, caption: setupKindLabel))
                : Columns("1.2*,1.3*,1*,.75*", SetupField("Connection method", provider), SetupField("Language model", textModelField), reasoning!, budget));
        var promptDetails = Ui.Column(firstRun ? 12 : 10, serverAddress, chatFields, firstRun ? apiFields : directPromptHost,
            remoteFields, serverFields, firstRun ? localCodex.View : cliPromptHost, remoteHelp);
        if (firstRun) promptConfiguration.Children.Insert(1, SetupColumns(
            SetupField("Model", textModelField, textConnectionCheck.ActionButton), SetupField("Reasoning level", effort)));
        var disclosure = Disclosure("Connection details", firstRun ? new Border() : promptDetails, out var promptConnectionDetails);
        promptConfiguration.Children.Add(firstRun ? promptDetails : disclosure);
        var promptInstructions = Ui.Column(12, promptModelTitle, instructionFields, restoreInstructions);
        var promptList = Card(promptModelList, 14);
        promptList.VerticalAlignment = VerticalAlignment.Top;
        var refreshTextModels = Ui.TextButton("Refresh models", textConnectionCheck.Start);
        var promptLight = new Border();
        void RefreshPromptLight() => promptLight.Child = selectedProvider == "direct"
            ? ConnectionLight(textCatalog.CheckMessage, textCatalog.Connected) : textConnectionCheck.Light();
        textCatalog.CheckChanged += RefreshPromptLight;
        textConnectionCheck.Changed += RefreshPromptLight;
        RefreshPromptLight();
        var promptPage = firstRun ? new ContentControl() : PageWithAction("Prompt enhancer", refreshTextModels,
            Card(Ui.Column(12, Columns("*,19", Heading("Language model", 15), promptLight), promptConfiguration, textConnectionCheck.View)),
            MasterDetail(promptList, Card(promptInstructions), 250));
        var serverPanel = Card(Ui.Column(9, Heading("ComfyUI server"), connection, credentials));
        var connectionLights = new Dictionary<int, List<Border>>();
        Control ConnectionPage(int index, Control details)
        {
            var list = Ui.Column(3);
            foreach (var (target, label, type) in new[]
            {
                (5, "API", "Language model and image operations"),
                (11, "CLI", "This computer or ComfyUI server"),
                (0, "ComfyUI", "Workflows and Comfy credits")
            })
            {
                var light = new Border();
                if (!connectionLights.TryGetValue(target, out var slots)) connectionLights[target] = slots = [];
                slots.Add(light);
                var row = RowButton(Columns("*,19", Ui.Column(2, Heading(label, 11), Caption(type)), light),
                    () => openRelated?.Invoke(target));
                if (index == target) row.Background = Palette.SettingsSelection;
                list.Children.Add(row);
            }
            var master = Card(list, 6);
            master.VerticalAlignment = VerticalAlignment.Top;
            return Page("Connections", MasterDetail(master, details, 250));
        }
        void RefreshConnectionLights(int index, Func<Border> light)
        {
            if (connectionLights.TryGetValue(index, out var slots))
                foreach (var slot in slots) slot.Child = light();
        }
        var serverConnectionPage = firstRun ? new ContentControl() : ConnectionPage(0, serverPanel);
        var editorPage = Page("Editor",
            Card(Columns("*,*", Field("Language", languagePicker), Field("Interface theme", themePicker))),
            Card(Columns("*,*", Field("Workspace layout", layoutPicker), Field("AI panel placement", aiPanelPicker))),
            Card(Ui.Column(5, rememberPrompts, showContext, groupedPanels, linkedSources, updates)));
        var accelerated = new ToggleSwitch { IsChecked = settings.UseEditorGpu || settings.UseRasterFilterGpu || settings.UseSelectionGpu };
        accelerated.IsCheckedChanged += (_, _) =>
        {
            foreach (var toggle in new[] { selectionGpu, rasterGpu, editorGpu })
                if (toggle.IsEnabled) toggle.IsChecked = accelerated.IsChecked == true;
        };
        var actualGpu = Composa.Rendering.GpuAcceleration.EditorDevice ?? Composa.Rendering.GpuAcceleration.FilterDevice;
        var deviceRows = Ui.Column(0, InsetRow(Columns("*,130", Caption("Module"), Caption("Device")), Palette.SettingsSidebar));
        var sam = Composa.Vision.ModelRunner.DeviceFor(devices[0]) ?? Composa.Vision.ModelRunner.DeviceFor(devices[1]);
        DeviceRow("EfficientSAM S", sam, null);
        DeviceRow("ViTMatte Small", Composa.Vision.ModelRunner.DeviceFor(devices[2]), null);
        DeviceRow("SkiaSharp / OpenCL", Composa.Rendering.GpuAcceleration.EditorDevice == null ? "CPU" : "GPU", actualGpu);
        DeviceRow("Image filters", Composa.Rendering.GpuAcceleration.FilterDevice == null ? "CPU" : "GPU",
            Composa.Rendering.GpuAcceleration.FilterDevice);
        void DeviceRow(string name, string? device, string? description)
        {
            var value = Caption(device ?? "Not loaded");
            value.Foreground = device == "CPU" ? Palette.SettingsWarningText : device == null ? Palette.Secondary : Palette.SettingsSuccessText;
            if (description != null) Locale.Tip(value, description);
            deviceRows.Children.Add(InsetRow(Columns("*,130", Caption(name), value), vertical: 13));
        }
        var gpuOptions = Ui.Column(6, selectionGpu, editorGpu, rasterGpu);
        gpuOptions.IsVisible = false;
        var changeGpu = Ui.TextButton("Change", () => gpuOptions.IsVisible = !gpuOptions.IsVisible);
        var performancePage = Page("Performance",
            Card(Ui.Column(12, Columns("*,Auto", Heading("Hardware acceleration", 14), accelerated),
                Columns("*,Auto", Heading(actualGpu ?? "Local computation", 12), Caption(actualGpu == null ? "CPU" : "GPU"))), 16),
            Ui.Column(8, Heading("Devices", 13), Card(deviceRows, 0)),
            Ui.Column(8, Heading("Exceptions", 13), Card(Ui.Column(8,
                Columns("*,Auto", Ui.Column(2, Heading("Large RGB16 blur filters", 12),
                    Caption(settings.UseRasterFilterGpu ? "Use GPU when available" : "Always use CPU")), changeGpu), gpuOptions), 13)));
        var modelMaster = Ui.Column(2);
        var modelUsage = Ui.Column(2);
        var modelWorkflows = Ui.Column(6);
        var modelParameters = Ui.Column(7);
        var modelHeading = Heading("", 15);
        var modelStatusHost = new Border();
        var workflowInspector = Ui.Column(7, Columns("*,Auto", Field("Operation", operationPicker), useDefault),
            modelParameters, Ui.Row(8, readiness, fix), generation);
        workflowInspector.IsVisible = false;
        string? openModelWorkflow = null;
        var workflowModelDetail = Card(Ui.Column(12, Columns("*,Auto", modelHeading, modelStatusHost),
            files, modelUsage, modelWorkflows, workflowInspector));
        var modelDetail = new ContentControl { Content = workflowModelDetail, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        EngineProfile? directModelSelection = null;
        var addWorkflow = Ui.TextButton("Add workflow…", () => { });
        addWorkflow.HorizontalAlignment = HorizontalAlignment.Right;
        var modelList = Card(modelMaster, 6);
        modelList.VerticalAlignment = VerticalAlignment.Top;
        var modelPage = PageWithAction("Models and workflows", addWorkflow, MasterDetail(modelList, modelDetail, 260));
        var promptTable = Ui.Column(0);
        var operationTable = Ui.Column(1);
        var operationDetail = Ui.Column(11);
        var detailHeader = Heading("", 25);
        var activeOperation = initialOperation ?? AiTaskKind.GenerativeExpand;
        AiTaskKind? expandedOperation = null;
        var pendingComfyConnections = new Dictionary<AiTaskKind, bool>();
        bool refreshQueued = false;
        var checkAll = Ui.TextButton("Check all", () =>
        {
            comfyCheck.Start();
            if (selectedProvider != "comfy") textConnectionCheck.Start();
        });
        var operationsPage = PageWithAction("Operations", checkAll,
            Ui.Column(8, Heading("Prompt enhancer", 13), Card(promptTable, 0)),
            Ui.Column(8, Heading("Image operations", 13), Card(operationTable, 0)));
        var detailBreadcrumb = Caption("");
        var restoreOperation = Ui.TextButton("Reset this operation’s route", () => { });
        var closeToEditor = Ui.TextButton("Back to editor", () => settingsDialog?.Close(false));
        closeToEditor.Classes.Add("flat");
        var operationPage = Page("", Ui.Column(4, detailBreadcrumb,
            Columns("*,Auto", detailHeader, Ui.Row(8, closeToEditor, restoreOperation))), operationDetail);
        var apiTextLight = new Border();
        var apiImageLight = new Border();
        Control ConnectionCard(string title, Control content, Border light) =>
            Card(Ui.Column(12, Columns("*,19", Heading(title, 14), light), content));
        var imagePage = firstRun ? new ContentControl() : ConnectionPage(5, Ui.Column(16,
            ConnectionCard("Language model", textConnectionHost, apiTextLight),
            ConnectionCard("Image operations", imageConnection.View, apiImageLight)));
        var cliLocation = SetupInput(Ui.Combo(new[] { "local", "remote" }, remoteHelp.IsVisible ? "remote" : "local",
            value => Locale.T(value == "local" ? "This computer" : "ComfyUI server"), _ => { }, double.NaN));
        var cliTextLight = new Border();
        var cliImageLight = new Border();
        var cliLocalText = new Border();
        var cliRemoteText = Ui.Column(10);
        var cliLocalImage = Ui.Column(10, imageConnection.CliSettings);
        var cliRemoteImage = Caption("The installed AVS Prompt Helper supports language models only.");
        var cliAdapter = SetupInput(Ui.Combo(remoteAdapters, remoteCli, CliLabel, _ => { }, double.NaN));
        var cliModels = settings.EnhancerCliModels.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        var discoveredCliModels = new Dictionary<string, string[]>();
        string CliProvider() => CliIsRemote() ? remoteAdapters[Math.Max(0, cliAdapter.SelectedIndex)] : "local-codex";
        var cliModel = SetupInput(new ComboBox { PlaceholderText = Locale.T("Choose a model") });
        var cliModelId = SetupInput(new TextBox { PlaceholderText = Locale.T("Model ID") });
        bool fillingCliModels = false;
        void RefreshCliModels()
        {
            var adapter = CliProvider();
            var current = adapter == "local-codex" ? localCodex.Model.Text :
                adapter == selectedProvider ? serverModel.Text : serverModels.GetValueOrDefault(adapter);
            fillingCliModels = true;
            cliModel.ItemsSource = (discoveredCliModels.GetValueOrDefault(adapter) ?? []).Concat(cliModels.GetValueOrDefault(adapter) ?? [])
                .Append(current ?? "").Where(id => id.Length > 0).Distinct().ToArray();
            cliModel.SelectedItem = current;
            fillingCliModels = false;
        }
        cliModel.SelectionChanged += (_, _) =>
        {
            if (fillingCliModels || cliModel.SelectedItem is not string id) return;
            var adapter = CliProvider();
            if (adapter == "local-codex") localCodex.Model.Text = id;
            else if (adapter == selectedProvider) serverModel.Text = id;
            else serverModels[adapter] = id;
        };
        var addCliModel = Ui.TextButton("Add model", () =>
        {
            var id = cliModelId.Text?.Trim();
            if (string.IsNullOrEmpty(id)) return;
            var adapter = CliProvider();
            if (!cliModels.TryGetValue(adapter, out var ids)) cliModels[adapter] = ids = [];
            if (!ids.Contains(id)) ids.Add(id);
            RefreshCliModels();
            cliModel.SelectedItem = id;
            cliModelId.Text = "";
        });
        cliRemoteText.Children.Add(Columns("*,*", SetupField("CLI", cliAdapter),
            SetupField("ComfyUI server", SetupInput(new TextBox { IsReadOnly = true, [!TextBox.TextProperty] = url[!TextBox.TextProperty] }))));
        var cliCheck = new ConnectionCheck(lifetime.Token, CheckCliConnection, "", showHelperWarning: true, automatic: true);
        var cliConnectionPage = firstRun ? new ContentControl() : ConnectionPage(11, Ui.Column(16,
            Card(Columns("*,*", SetupField("Run CLI on", cliLocation))),
            ConnectionCard("Language model", Ui.Column(12, cliLocalText, cliRemoteText, SetupField("Selected model", cliModel),
                Columns("*,Auto", cliModelId, addCliModel), cliCheck.View), cliTextLight),
            ConnectionCard("Image operations", Ui.Column(12, cliLocalImage, cliRemoteImage), cliImageLight)));
        bool CliIsRemote() => cliLocation.SelectedIndex == 1;
        async Task<string> CheckCliConnection(CancellationToken token)
        {
            if (!CliIsRemote())
            {
                var matches = await LocalCodex.DiscoverModelsAsync(localCodex.Path.Text ?? "", token);
                token.ThrowIfCancellationRequested();
                discoveredCliModels["local-codex"] = matches.Select(item => item.Id).ToArray();
                if (selectedProvider == "local-codex")
                {
                    listedNames.Clear();
                    foreach (var item in matches) listedNames[item.Id] = item.Title;
                    PresentTextCatalog(matches.Select(item => new AiTaskService.ServerTextModel(item.Id, item.Efforts)).ToArray(),
                        localCodex.Model.Text, matches.FirstOrDefault(item => item.IsDefault)?.Id);
                }
                RefreshCliModels();
                return matches.Count == 0 ? "The CLI reported no models." : "Codex CLI is signed in. Models loaded.";
            }
            var adapter = CliProvider();
            var address = url.Text ?? "";
            var preview = live.SettingsPreview(() => address);
            preview.ConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
            await preview.TestConnectionAsync(token);
            token.ThrowIfCancellationRequested();
            if (preview.ServerCapabilities?.NodeTypes.Contains("AVSPromptHelper") != true)
                throw new AiTaskService.PromptHelperMissingException();
            var modelsOnServer = await AiTaskService.DiscoverServerModels(address, adapter, "", token);
            token.ThrowIfCancellationRequested();
            discoveredCliModels[adapter] = modelsOnServer.Select(item => item.Id).ToArray();
            if (selectedProvider == adapter) PresentTextCatalog(modelsOnServer, serverModel.Text, null);
            RefreshCliModels();
            return modelsOnServer.Length == 0 ? "The server reported no models." : "Server responded. Models loaded.";
        }
        var setupServer = new Expander { Header = Locale.T("ComfyUI server"), Content = firstRun ? serverPanel : null,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var setupTextIndicator = new Border();
        Control setupPage = new ContentControl();
        if (firstRun)
        {
            void RefreshSetupServer() => setupServer.IsVisible = selectedProvider is not ("direct" or "local-codex")
                || imageConnection.RouteFor(AiTaskKind.GenerateImage) == ImageGenerationRoute.ComfyWorkflow;
            provider.SelectionChanged += (_, _) => RefreshSetupServer();
            imageConnection.RouteChanged += RefreshSetupServer;
            RefreshSetupServer();
            textConnectionCheck.Changed += () => setupTextIndicator.Child = CheckStatus(textConnectionCheck);
            var title = Heading("Connections", 25);
            title.MinHeight = 38;
            title.VerticalAlignment = VerticalAlignment.Center;
            var content = Ui.Column(14, title,
                SetupSection("Prompt enhancer", Ui.Column(12, promptConfiguration, textConnectionCheck.View), setupTextIndicator),
                SetupSection("Image operations", Ui.Column(12, imageConnection.SetupView, setupServer), imageConnection.SetupIndicator));
            content.Margin = new Thickness(34, 20, 34, 20);
            content.MaxWidth = 1090;
            setupPage = Scroll(content);
        }
        var workflowPage = Page("Workflows", Card(workflows.Content));
        var body = new StudioSettingsPages(
            [serverConnectionPage, modelPage, promptPage, editorPage, workflowPage, imagePage, operationsPage, performancePage, operationPage, setupPage, new ContentControl(), cliConnectionPage],
            firstRun ? 9 : page == 10 ? 5 : page == 11 ? 11 : Math.Clamp(page, 0, 8));
        void ScheduleComfyCheck() => comfyCheck.Schedule(() => body.SelectedIndex == 0 && !url.IsFocused && !string.IsNullOrWhiteSpace(url.Text));
        void ScheduleTextCheck()
        {
            if (firstRun || body.SelectedIndex != 2) return;
            if (selectedProvider == "direct")
            {
                if (textCatalog.Models.Count > 0) PresentDirectCatalog();
                if (textCatalog.Connected != true) textCatalog.ScheduleCheck();
            }
            else textConnectionCheck.Schedule(() => body.SelectedIndex == 2 && (!url.IsFocused || selectedProvider == "local-codex"));
        }
        void RefreshCliLocation()
        {
            var remote = CliIsRemote();
            cliLocalText.IsVisible = cliLocalImage.IsVisible = !remote;
            cliRemoteText.IsVisible = cliRemoteImage.IsVisible = remote;
            if (remote) imageConnection.CliCheck.Reset();
            if (body.SelectedIndex == 11)
            {
                cliCheck.Schedule(() => body.SelectedIndex == 11 && (!remote || !url.IsFocused));
                if (!remote) imageConnection.CliCheck.Schedule(() => body.SelectedIndex == 11 && !CliIsRemote());
            }
            RefreshCliModels();
            RefreshCliLights();
        }
        void RefreshCliLights()
        {
            cliTextLight.Child = cliCheck.Light();
            cliImageLight.Child = CliIsRemote()
                ? ConnectionLight("The installed AVS Prompt Helper supports language models only.", null)
                : imageConnection.CliCheck.Light();
            RefreshConnectionLights(11, cliCheck.Light);
        }
        void RefreshApiLights()
        {
            apiTextLight.Child = ConnectionLight(textCatalog.CheckMessage, textCatalog.Connected);
            apiImageLight.Child = ConnectionLight(imageConnection.ApiCheckMessage, imageConnection.ApiConnected);
            bool? combined = textCatalog.Connected == false || imageConnection.ApiConnected == false ? false :
                textCatalog.Connected == true || imageConnection.ApiConnected == true ? true : null;
            RefreshConnectionLights(5, () => ConnectionLight("API", combined));
        }
        if (!firstRun)
        {
            textCatalog.EnableMonitoring(() => body.SelectedIndex == 5 || body.SelectedIndex == 2 && selectedProvider == "direct");
            imageConnection.EnableApiMonitoring(() => body.SelectedIndex == 5);
            textCatalog.CheckChanged += RefreshApiLights;
            imageConnection.ApiCheckChanged += RefreshApiLights;
            comfyCheck.Changed += () =>
            {
                status.IsVisible = !comfyCheck.Failed;
                RefreshConnectionLights(0, comfyCheck.Light);
            };
            cliCheck.Changed += RefreshCliLights;
            imageConnection.CliCheck.Changed += RefreshCliLights;
            cliLocation.SelectionChanged += (_, _) => { cliCheck.Reset(); RefreshCliLocation(); };
            cliAdapter.SelectionChanged += (_, _) =>
            {
                if (cliAdapter.SelectedIndex < 0) return;
                remotePicker.SelectedIndex = cliAdapter.SelectedIndex;
                SelectRemoteAdapter(remoteAdapters[cliAdapter.SelectedIndex]);
                cliCheck.Reset();
                RefreshCliLocation();
            };
            remotePicker.SelectionChanged += (_, _) => { if (remotePicker.SelectedIndex >= 0) cliAdapter.SelectedIndex = remotePicker.SelectedIndex; };
            provider.SelectionChanged += (_, _) => ScheduleTextCheck();
            remotePicker.SelectionChanged += (_, _) => ScheduleTextCheck();
            foreach (var input in new[] { url, sessionKey, keyEnvironment, serverUrl, localCodex.Path })
                input.TextChanged += (_, _) => ScheduleTextCheck();
            localCodex.Path.TextChanged += (_, _) => { cliCheck.Reset(); if (body.SelectedIndex == 11) RefreshCliLocation(); };
            url.TextChanged += (_, _) =>
            {
                if (!url.IsFocused) ScheduleComfyCheck();
                if (CliIsRemote()) cliCheck.Reset();
            };
            url.LostFocus += (_, _) => { ScheduleComfyCheck(); ScheduleTextCheck(); };
            timeout.ValueChanged += (_, _) => { comfyCheck.Reset(); ScheduleComfyCheck(); };
            RefreshApiLights();
            RefreshCliLights();
            RefreshConnectionLights(0, comfyCheck.Light);
        }
        showComfyKey = () =>
        {
            if (firstRun) setupServer.IsExpanded = true;
            else openRelated?.Invoke(0);
            credentials.IsExpanded = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { sessionKey.BringIntoView(); sessionKey.Focus(); });
        };
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
                    readiness.Foreground = Palette.SettingsWarningText;
                    readiness.Text = Locale.T("Workflow needs attention") + " · " + result.Missing.Count;
                    ToolTip.SetTip(readiness, string.Join("\n", result.Missing));
                    fix.Content = Locale.T("Model components…");
                    fixAction = () => files.BringIntoView();
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
        var validation = Ui.Label("", Avalonia.Media.Brushes.Orange);
        validation.IsVisible = false; validation.MaxWidth = 520; validation.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var search = new TextBox { Name = "SettingsSearch", PlaceholderText = Locale.T("Search settings…"), MaxLength = 120,
            MinHeight = 34, Margin = new Thickness(0, 12, 0, 9) };
        var matches = new ListBox { Name = "SettingsMatches", IsVisible = false, MaxHeight = 165 };
        var anchors = new List<(string Title, string Keywords, int Page, Control Target, Expander? Section, Action? Select)>
        {
            ("ComfyUI server", "connection address url сервер подключение", 0, url, null, null),
            ("Connection timeout", "seconds", 0, timeout, null, null),
            ("Comfy credits API key", "ключ токен авторизация", 0, sessionKey, credentials, null),
            ("Operations", "назначение модель default", 6, operationTable, null, null),
            ("Workflows", "graph JSON prompt binding граф привязки импорт", 4, workflows.Content, null, null),
            ("Model components", "checkpoint vae encoder файлы граф", 1, files, null, null),
            ("Show AI context bounds", "output selection выделение контекст", 1, showContext, null, null),
            ("Prompt provider", "улучшение промт cli api", 2, provider, null, null),
            ("Text model", "chatgpt luna comfy credits", 2, provider, null, null),
            ("Direct text API", "connection endpoint key catalog URL каталог прямое подключение", 5, apiUrl, null, null),
            ("Reasoning effort", "reasoning рассуждение", 2, effort, null, null),
            ("Response options", "token limit токены", 2, tokens, null, null),
            ("System instructions", "системный промт", 2, instructions, null, null),
            ("Image generation", "api cli подключение генерация изображений", 5, imageConnection.ApiUrl, null, null),
            ("Language", "language russian english русский английский язык", 3, languagePicker, null, null),
            ("Interface theme", "appearance light dark neutral classic blue оформление тема светлая тёмная", 3, themePicker, null, null),
            ("Workspace layout", "classic modern tools panels классическая новая панели раскладка", 3, layoutPicker, null, null),
            ("AI panel placement", "sidebar selection положение ии выделение", 3, aiPanelPicker, null, null),
            (rememberPromptsLabel, "remember save drafts prompt промпт промт черновики сохранение", 3, rememberPrompts, null, null),
            (gpuLabel, "GPU CPU SAM ViTMatte DirectML видеокарта выделение", 7, selectionGpu, null, null),
            (rasterGpuLabel, "GPU CPU OpenCL Gaussian RGB16 видеокарта ускорение фильтры", 7, rasterGpu, null, null),
            (editorGpuLabel, "GPU CPU OpenCL rendering brush effects видеокарта ускорение холст кисть эффекты", 7, editorGpu, null, null),
            ("Group side panels in tabs", "панели вкладки", 3, groupedPanels, null, null),
            ("Automatically refresh linked files", "обновление связь", 3, linkedSources, null, null),
            ("Check for updates", "обновления", 3, updates, null, null)
        };
        foreach (var pack in service.Engines.Profiles)
            anchors.Add((ModelTitle(pack, pack.Tasks.FirstOrDefault()?.Task ?? AiTaskKind.GenerateImage), pack.Id + " model модель", 1, engine, null, () => SelectRoute(pack)));
        var history = new Stack<(int Page, AiTaskKind Operation)>();
        void OpenMatch()
        {
            if (matches.SelectedItem is not ListBoxItem { Tag: int index }) return;
            var entry = anchors[index];
            search.Text = "";
            Navigate(entry.Page, false);
            if (entry.Section != null) entry.Section.IsExpanded = true;
            if (entry.Target == selectionGpu || entry.Target == editorGpu || entry.Target == rasterGpu) gpuOptions.IsVisible = true;
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
        var scope = Caption("");
        var back = Ui.TextButton("‹ Back", () => { });
        back.IsVisible = false;
        void Navigate(int index, bool preserveContext, AiTaskKind? operation = null)
        {
            if (firstRun || index == body.SelectedIndex) return;
            if (preserveContext) history.Push((body.SelectedIndex, activeOperation));
            else history.Clear();
            if (operation != null) activeOperation = operation.Value;
            body.SelectedIndex = index;
        }
        openRelated = index => Navigate(index, true);
        addWorkflow.Click += async (_, _) =>
        {
            workflows.Open(null, null);
            Navigate(4, true);
            await workflows.AddWorkflow();
        };
        back.Click += (_, _) =>
        {
            if (!history.TryPop(out var route)) return;
            activeOperation = route.Operation;
            body.SelectedIndex = route.Page;
        };
        openPrompt = (pack, task) =>
        {
            RefreshPromptProfiles(pack ?? promptPack);
            Navigate(2, true);
            if (task is { } operation) (operation == AiTaskKind.GenerateImage ? instructions : otherInstructions).Focus();
        };

        EngineProfile? EngineForOperation(AiTaskKind task)
        {
            if ((taskDrafts.GetValueOrDefault(task.ToString()) ?? settings.EngineForTask(task)) is { Length: > 0 } id &&
                service.Engines.Profiles.FirstOrDefault(pack => pack.Id == id && pack.Binding(task) != null) is { } assigned)
                return assigned;
            var preferred = service.Engines.Find(defaultEngineId);
            return preferred?.Binding(task) != null ? preferred : service.Engines.Profiles.FirstOrDefault(pack => pack.Binding(task) != null);
        }

        string OperationRoute(AiTaskKind task)
        {
            var route = imageConnection.RouteFor(task);
            if (route == ImageGenerationRoute.BuiltIn) return Locale.T("Built-in");
            if (route != ImageGenerationRoute.ComfyWorkflow) return imageConnection.ModelLabel(task);
            var pack = EngineForOperation(task);
            if (workflowDraft.Assignments.TryGetValue(task, out var assigned) && workflowDraft.Workflows.TryGetValue(assigned, out var workflow))
                return workflow.Name;
            return pack == null ? Locale.T("No workflow installed.") : ModelTitle(pack, task);
        }
        void ShowOperation(AiTaskKind task)
        {
            Navigate(8, true, task);
        }
        void OpenWorkflow(EngineProfile? pack, AiTaskKind task)
        {
            var id = pack == null ? workflowDraft.Assignments.GetValueOrDefault(task)
                : WorkflowIdForModel(pack, task) ?? WorkflowPreferences.FactoryId(pack.Id, task);
            workflows.Open(id, task);
            Navigate(4, true);
        }
        string? WorkflowIdForModel(EngineProfile pack, AiTaskKind operation)
        {
            if (EngineForOperation(operation)?.Id == pack.Id && imageConnection.RouteFor(operation) == ImageGenerationRoute.ComfyWorkflow)
                return workflowDraft.ActiveId(operation, pack.Id);
            var factory = WorkflowPreferences.FactoryId(pack.Id, operation);
            return workflowDraft.Workflows.ContainsKey(factory) ? factory : null;
        }
        void RenderOperations()
        {
            promptTable.Children.Clear();
            operationTable.Children.Clear();
            const string columns = "1.15*,1.7*,116,24";
            foreach (var table in new[] { promptTable, operationTable })
                table.Children.Add(InsetRow(Columns(columns, Caption(table == promptTable ? "Model" : "Operation"),
                    Caption("Model and execution route"), Caption(table == promptTable ? "" : "Configuration"), Caption("")), Palette.SettingsSidebar, 7));
            var textMethod = selectedProvider switch
            {
                "direct" => "API directly", "local-codex" => "CLI on this computer",
                "comfy" => "Comfy credits", _ => "CLI on the ComfyUI server"
            };
            var check = textConnectionCheck;
            promptTable.Children.Add(TableRow("Language model", operationTextModel.Text + " · " + Locale.T(textMethod), null,
                Status(check.StatusText, check.Passed, check.Failed), () => openPrompt?.Invoke(null, null)));
            foreach (var task in DirectImageGenerator.Operations)
            {
                var route = imageConnection.RouteFor(task);
                var pack = EngineForOperation(task);
                var method = route == ImageGenerationRoute.ComfyWorkflow && pack?.PaidApi == true && !workflowDraft.Assignments.ContainsKey(task)
                    ? "Comfy credits" : DirectImageGenerator.RouteLabel(route);
                var custom = taskDrafts.ContainsKey(task.ToString()) || workflowDraft.Assignments.ContainsKey(task)
                    || route != new Settings().ImageRouteFor(task) || changedGeneration.Keys.Any(key => key.Task == task);
                var serverResponded = ServerMatchesDraft() && route == ImageGenerationRoute.ComfyWorkflow;
                var ready = route == ImageGenerationRoute.BuiltIn || serverResponded && IsWorkflowReady(task, pack);
                var isExpanded = expandedOperation == task;
                operationTable.Children.Add(TableRow(task.DisplayName(), OperationRoute(task) + " · " + Locale.T(method), custom,
                    Status(ready ? "Ready" : serverResponded ? "Workflow needs attention" : "Connection has not been checked",
                        ready, serverResponded && !ready), () => { expandedOperation = isExpanded ? null : task; RenderOperations(); }, isExpanded));
                if (!isExpanded) continue;
                var open = Ui.TextButton("Run parameters ›", () => ShowOperation(task));
                open.HorizontalAlignment = HorizontalAlignment.Right;
                open.Classes.Add("flat");
                var hint = route == ImageGenerationRoute.DirectApi && imageConnection.CatalogSize > 0
                    ? string.Format(Locale.T("{0} models available in this connection"), imageConnection.CatalogSize)
                    : route == ImageGenerationRoute.BuiltIn ? Locale.T("Built-in") : "";
                operationTable.Children.Add(InsetRow(Ui.Column(8,
                    Columns("*,*,1.25*", Field("Connection method", imageConnection.RouteField(task)),
                        Field("Connection", OperationConnectionPicker(task, route, pack)),
                        Field(route == ImageGenerationRoute.DirectApi ? "Model from API catalog" : "Image model",
                            OperationModelPicker(task, route, pack))),
                    Columns("*,Auto", Caption(hint), open)), Palette.PanelRaised, 13));
            }
            Control TableRow(string label, string route, bool? custom, Control statusIcon, Action open, bool expanded = false) => new Border
            {
                BorderBrush = Palette.SettingsLine, BorderThickness = new Thickness(0, 1, 0, 0),
                Background = expanded ? Palette.PanelRaised : null,
                Child = RowButton(Columns(columns, Heading(label, 12), Caption(route),
                    Caption(custom == null ? "" : custom.Value ? "Custom" : "Default"), statusIcon), open)
            };
        }

        Control OperationConnectionPicker(AiTaskKind task, ImageGenerationRoute route, EngineProfile? pack)
        {
            if (route != ImageGenerationRoute.ComfyWorkflow) return imageConnection.ConnectionField(task, false);
            var local = service.Engines.Profiles.Any(item => !item.PaidApi && item.Binding(task) != null);
            var credits = pack?.PaidApi == true;
            try
            {
                var address = ComfyServerAddress.Parse(url.Text ?? "");
                credits |= service.Engines.Profiles.Any(item => item.PaidApi && item.Binding(task) != null) &&
                    !string.IsNullOrWhiteSpace(DraftCredential(address));
            }
            catch (FormatException) { local = credits = false; }
            var options = new[] { false, true }.Where(paid => paid ? credits : local).ToArray();
            var picker = Ui.Combo(options, pendingComfyConnections.GetValueOrDefault(task, pack?.PaidApi == true),
                paid => Locale.T(paid ? "Comfy credits" : "ComfyUI server"), paid =>
                {
                    pendingComfyConnections[task] = paid;
                    RenderOperations();
                }, double.NaN);
            picker.PlaceholderText = Locale.T("No configured connection");
            return picker;
        }

        Control OperationModelPicker(AiTaskKind task, ImageGenerationRoute route, EngineProfile? pack)
        {
            if (route == ImageGenerationRoute.DirectApi && task is not (AiTaskKind.Upscale or AiTaskKind.ObjectSelection))
                return imageConnection.ApiModelField(task);
            if (route == ImageGenerationRoute.ComfyWorkflow && assignmentPickers.TryGetValue(task, out var source))
            {
                var paid = pendingComfyConnections.GetValueOrDefault(task, pack?.PaidApi == true);
                var options = source.Items.OfType<object>().Where(item => item is ImageModelChoice model
                    ? model.Engine.PaidApi == paid : pack?.PaidApi == paid).ToArray();
                var picker = new ComboBox { ItemsSource = options, SelectedItem = options.Contains(source.SelectedItem) ? source.SelectedItem : null,
                    ItemTemplate = source.ItemTemplate, PlaceholderText = Locale.T("Choose a model") };
                AiGenerationControls.StyleModelField(picker, "Image model");
                picker.SelectionChanged += (_, _) =>
                {
                    source.SelectedItem = picker.SelectedItem;
                    if (picker.SelectedItem != null) pendingComfyConnections.Remove(task);
                    QueueRefresh();
                };
                return picker;
            }
            return new TextBox { Text = OperationRoute(task), IsReadOnly = true };
        }
        bool ServerMatchesDraft()
        {
            try
            {
                return service.ConnectionState == ComfyConnectionState.Connected &&
                    service.ConnectedServerUrl == ComfyServerAddress.Parse(url.Text ?? "").ToString();
            }
            catch (FormatException) { return false; }
        }
        bool IsWorkflowReady(AiTaskKind task, EngineProfile? pack)
        {
            if (pack == null) return false;
            try
            {
                return service.OperationCompatibility(pack, task, CurrentFiles(), settings.OperationFor(pack.Id, task, pack.PaidApi)).IsCompatible
                    && (!pack.PaidApi || !string.IsNullOrWhiteSpace(DraftCredential(ComfyServerAddress.Parse(url.Text ?? ""))));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or KeyNotFoundException or FormatException or System.Text.Json.JsonException)
            { return false; }
        }
        restoreOperation.Click += (_, _) =>
        {
            var task = activeOperation;
            pendingComfyConnections.Remove(task);
            if (assignmentPickers.TryGetValue(task, out var picker)) picker.SelectedItem = inherit;
            workflowDraft.Assignments.Remove(task);
            imageConnection.ResetModel(task);
            imageConnection.SetRoute(task, new Settings().ImageRouteFor(task));
            QueueRefresh();
        };
        void RenderOperation()
        {
            var task = activeOperation;
            var pack = EngineForOperation(task);
            var route = imageConnection.RouteFor(task);
            detailHeader.Text = Locale.T(task.DisplayName());
            detailBreadcrumb.Text = Locale.T("Operations") + " / " + detailHeader.Text;
            operationDetail.Children.Clear();
            var links = new WrapPanel { ItemSpacing = 7, LineSpacing = 6 };
            var connectionColumn = Ui.Column(3, Caption("Connection"), Heading(route == ImageGenerationRoute.ComfyWorkflow
                ? pack?.PaidApi == true ? "Comfy credits" : "ComfyUI server" : DirectImageGenerator.RouteLabel(route), 13),
                Caption(route == ImageGenerationRoute.ComfyWorkflow ? url.Text ?? "" :
                    route == ImageGenerationRoute.DirectApi ? imageConnection.ModelFor(task).ApiAddress : "This computer"));
            if (route != ImageGenerationRoute.BuiltIn)
                connectionColumn.Children.Add(Ui.TextButton("Open connection ›", () =>
                {
                    Navigate(ConnectionPageFor(route), true);
                }));
            var modelName = pack != null && route == ImageGenerationRoute.ComfyWorkflow ? ModelTitle(pack, task)
                : route == ImageGenerationRoute.BuiltIn ? "Built-in" : imageConnection.ModelFor(task).Model;
            var modelColumn = Ui.Column(3, Caption("Model"), Heading(modelName, 13));
            SavedWorkflow? assignedWorkflow = null;
            System.Text.Json.Nodes.JsonObject? graph = null;
            string? workflowTitle = null;
            if (route == ImageGenerationRoute.ComfyWorkflow && pack != null)
            {
                try
                {
                    var workflow = EngineCatalog.WorkflowFor(pack, task);
                    assignedWorkflow = service.ActiveWorkflow(task, pack);
                    graph = assignedWorkflow?.Graph ?? service.Engines.ReadWorkflow(pack, workflow);
                    workflowTitle = assignedWorkflow?.Name ?? Path.GetFileName(workflow.File);
                    modelColumn.Children.Add(Caption(service.Engines.ModelFor(pack, task, CurrentFiles()).ModelKey));
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or UnauthorizedAccessException)
                { workflowTitle = Locale.Message(error.Message); }
            }
            var workflowColumn = Ui.Column(3, Caption("Workflow"), Heading(workflowTitle ?? "No workflow", 13));
            if (graph != null)
                workflowColumn.Children.Add(Caption(string.Format(Locale.T("{0} nodes · {1}"), graph.Count, Locale.T(assignedWorkflow == null ? "Factory" : "Custom"))));
            if (route == ImageGenerationRoute.ComfyWorkflow && pack != null)
            {
                modelColumn.Children.Add(Ui.TextButton("Open model ›", () =>
                {
                    directModelSelection = null;
                    SelectRoute(pack);
                    operationPicker.SelectedItem = task;
                    Navigate(1, true);
                }));
                if (WorkflowPreferences.Operations.Contains(task))
                    workflowColumn.Children.Add(Ui.TextButton("Open workflow ›", () => OpenWorkflow(pack, task)));
                links.Children.Add(Ui.TextButton("Generation settings…", async () =>
                {
                    SelectRoute(pack);
                    var key = (pack.Id, task);
                    await Advanced(settingsDialog!, generationDraft, service, task,
                        changedGeneration.GetValueOrDefault(key) ?? settings.OperationFor(pack.Id, task, pack.PaidApi),
                        value => changedGeneration[key] = value, deferSave: true);
                    QueueRefresh();
                }));
            }
            if (route == ImageGenerationRoute.DirectApi || route == ImageGenerationRoute.LocalCodex)
                modelColumn.Children.Add(Ui.TextButton("Open model ›", () =>
                {
                    directModelSelection = imageConnection.ModelFor(task).PromptProfile();
                    Navigate(1, true);
                }));
            if (route != ImageGenerationRoute.BuiltIn && task is not (AiTaskKind.Upscale or AiTaskKind.GenerativeExpand or AiTaskKind.ObjectSelection))
            {
                links.Children.Add(Ui.TextButton("Prompt enhancer ›", () => openPrompt(route == ImageGenerationRoute.ComfyWorkflow ? pack :
                    imageConnection.ModelFor(task).PromptProfile(), task)));
            }
            operationDetail.Children.Add(Card(Columns("*,*,*", modelColumn, connectionColumn, workflowColumn), 15));
            var routeLabel = DirectImageGenerator.RouteLabel(route);
            operationDetail.Children.Add(Columns("*,Auto", Heading("Run parameters", 13), Caption(routeLabel)));
            var direct = DirectImageGenerator.IsDirect(route);
            var engine = direct ? imageConnection.ModelFor(task).PromptProfile() : pack;
            var profileKey = (engine?.Id ?? "default", task);
            var profile = changedGeneration.GetValueOrDefault(profileKey) ?? settings.OperationFor(engine?.Id, task, engine?.PaidApi == true);
            void UpdateProfile(Func<AiOperationSettings, AiOperationSettings> change)
            {
                profile = change(profile);
                changedGeneration[profileKey] = profile;
            }
            string? GraphValue(string source, string input)
            {
                if (assignedWorkflow?.Bindings.GetValueOrDefault(source) is { } target)
                    return graph?[target.NodeId]?["inputs"]?[target.Input]?.ToString();
                return graph?.Select(node => node.Value?["inputs"]?[input]).FirstOrDefault(value => value != null)?.ToString();
            }
            var stepsFromGraph = GraphValue("steps", "steps");
            var samplerFromGraph = GraphValue("sampler", "sampler_name");
            Control stepsField = assignedWorkflow?.Bindings.ContainsKey("steps") == true
                ? Ui.Number(profile.WorkflowSteps ?? (int.TryParse(stepsFromGraph, out var count) ? count : 4),
                    1, int.MaxValue, value => UpdateProfile(old => old with { WorkflowSteps = (int)value }), width: double.NaN)
                : ReadOnly(stepsFromGraph);
            Control samplerField = ReadOnly(samplerFromGraph);
            if (assignedWorkflow?.Bindings.ContainsKey("sampler") == true)
            {
                var sampler = new TextBox { Text = profile.WorkflowSampler ?? "", PlaceholderText = samplerFromGraph };
                sampler.TextChanged += (_, _) => UpdateProfile(old => old with { WorkflowSampler = string.IsNullOrWhiteSpace(sampler.Text) ? null : sampler.Text.Trim() });
                samplerField = sampler;
            }
            var seed = new TextBox { Text = profile.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            seed.IsReadOnly = route == ImageGenerationRoute.BuiltIn || direct || assignedWorkflow != null && !assignedWorkflow.Bindings.ContainsKey("seed");
            seed.TextChanged += (_, _) =>
            {
                if (long.TryParse(seed.Text, out var value) && value >= -1) UpdateProfile(old => old with { Seed = value });
            };
            Control size;
            if (task == AiTaskKind.GenerativeExpand)
            {
                var whole = profile.ExpansionMode == AiExpansionMode.WholeImage;
                var sides = whole ? new[] { 0 }.Concat(AiDimensions.ExpansionSides) : AiDimensions.ExpansionSides;
                size = Ui.Combo(sides, whole ? profile.WholeExpansionMinimumSide : profile.ExpansionMinimumSide,
                    value => value == 0 ? Locale.T("Original size") : string.Format(Locale.T("{0} px · longest side"), value),
                    value => UpdateProfile(old => whole ? old with { WholeExpansionMinimumSide = value } : old with { ExpansionMinimumSide = value }), double.NaN);
            }
            else
            {
                var sizes = AiDimensions.MegapixelOptions.Prepend(0);
                size = Ui.Combo(sizes, profile.OriginalSize ? 0 : ClosestMegapixels(profile.Megapixels),
                    value => value == 0 ? Locale.T("Original size") : AiDimensions.Label(value),
                    value => UpdateProfile(old => old with { OriginalSize = value == 0, Megapixels = value == 0 ? old.Megapixels : value }), double.NaN);
            }
            var variants = Ui.Combo(Enum.GetValues<AiVariantMode>(), profile.VariantMode, value => value.ToString(),
                value => UpdateProfile(old => old with { VariantMode = value }), double.NaN);
            variants.IsEnabled = assignedWorkflow == null && !direct && AiGenerationControls.SupportsVariants(task);
            var align = new CheckBox { IsChecked = profile.AlignPixels ?? settings.AiAlignMaskedEdits,
                IsEnabled = !direct && route != ImageGenerationRoute.BuiltIn && task is not AiTaskKind.GenerateImage };
            align.IsCheckedChanged += (_, _) => UpdateProfile(old => old with { AlignPixels = align.IsChecked == true });
            operationDetail.Children.Add(Card(Ui.Column(2,
                Pair("Image size", size, "Steps", stepsField), Pair("Seed", seed, "Sampler", samplerField),
                Pair("Variant execution", variants, "Reference resolution", ReadOnly(profile.ReferenceMegapixels is { } resolution ? AiDimensions.Label(resolution) : null)),
                Pair("API quality", ReadOnly(engine?.PaidApi == true ? profile.ApiQuality : null), "Align pixels", align)), 13));
            var operationStatus = Caption(ServerMatchesDraft() ? "Server responded." : "Connection has not been checked");
            var check = Ui.TextButton("Check", async () =>
            {
                if (route != ImageGenerationRoute.ComfyWorkflow)
                {
                    Navigate(ConnectionPageFor(route), true);
                    return;
                }
                operationStatus.Text = Locale.T("Connecting…");
                try
                {
                    await service.TestConnectionAsync(lifetime.Token, url.Text);
                    var result = pack == null ? null : service.OperationCompatibility(pack, task, CurrentFiles(), profile);
                    operationStatus.Text = Locale.T(result?.IsCompatible == true ? "Operation ready" : "Workflow needs attention");
                    ToolTip.SetTip(operationStatus, result == null ? null : string.Join("\n", result.Missing));
                }
                catch (Exception error) when (error is not OutOfMemoryException && !lifetime.IsCancellationRequested)
                { operationStatus.Text = Locale.Message(error.Message); }
                catch (OperationCanceledException) { }
            });
            check.IsEnabled = route != ImageGenerationRoute.BuiltIn;
            operationDetail.Children.Add(Card(Columns("*,Auto", operationStatus, check), 12));
            operationDetail.Children.Add(links);

            static TextBox ReadOnly(string? value) => new() { Text = value ?? Locale.T("Not available for this route"), IsReadOnly = true };
            static Control Pair(string a, Control aValue, string b, Control bValue) =>
                InsetRow(Columns("1*,1.45*,1*,1.45*", Caption(a), aValue, Caption(b), bValue), vertical: 6);
        }
        void RenderModels()
        {
            var selected = service.SelectedEngine;
            modelMaster.Children.Clear();
            var choices = engine.Items.OfType<ImageModelChoice>().ToArray();
            foreach (var choice in choices)
            {
                var pack = choice.Engine;
                var option = RowButton(Ui.Column(3, Heading(choice.Name, 12),
                    Caption(pack.PaidApi ? "Comfy credits" : "ComfyUI server")), () =>
                    {
                        if (service.SelectedEngine?.Id != pack.Id) openModelWorkflow = null;
                        directModelSelection = null;
                        SelectRoute(pack);
                        RenderModels();
                    });
                if (pack.Id == selected?.Id && directModelSelection == null) option.Background = Palette.SettingsSelection;
                modelMaster.Children.Add(option);
            }
            var direct = imageConnection.PromptProfiles();
            foreach (var item in direct)
            {
                var name = item.ModelName ?? item.DisplayName;
                var choice = RowButton(Ui.Column(3, Heading(name, 12),
                    Caption(item.Id == "direct-codex-image" ? "CLI on this computer" : "API directly")), () =>
                    {
                        directModelSelection = item;
                        RenderModels();
                    });
                if (directModelSelection is { } modelChoice &&
                    AiTaskService.InstructionScope(modelChoice) == AiTaskService.InstructionScope(item)) choice.Background = Palette.SettingsSelection;
                modelMaster.Children.Add(choice);
            }
            if (directModelSelection is { } directModel)
            {
                var assigned = DirectImageGenerator.Operations.Where(task =>
                {
                    var model = imageConnection.ModelFor(task);
                    return DirectImageGenerator.IsDirect(model.Route) && task is not (AiTaskKind.Upscale or AiTaskKind.ObjectSelection)
                        && AiTaskService.InstructionScope(model.PromptProfile()) == AiTaskService.InstructionScope(directModel);
                }).ToArray();
                var usage = Ui.Column(8, Heading(directModel.ModelName ?? directModel.DisplayName, 15),
                    Caption(directModel.DisplayName), Heading("Used in operations"));
                foreach (var task in assigned)
                    usage.Children.Add(RowButton(Columns("*,18", Caption(task.DisplayName()), Caption("›")), () => ShowOperation(task)));
                if (assigned.Length == 0) usage.Children.Add(Caption("No operations assigned."));
                usage.Children.Add(Ui.TextButton("Prompt instructions for this model and operation", () =>
                    openPrompt?.Invoke(directModel, assigned.FirstOrDefault())));
                modelDetail.Content = Card(usage);
                return;
            }
            if (modelDetail.Content != workflowModelDetail) modelDetail.Content = workflowModelDetail;
            modelUsage.Children.Clear();
            modelWorkflows.Children.Clear();
            modelParameters.Children.Clear();
            if (selected == null) { modelHeading.Text = Locale.T("No Engine Packs installed"); return; }
            var operation = operationPicker.SelectedItem as AiTaskKind?;
            modelHeading.Text = (engine.SelectedItem as ImageModelChoice)?.Name ?? selected.DisplayName;
            modelStatusHost.Child = Status(readiness.Text ?? "Connection has not been checked", readiness.Text == Locale.T("Ready"),
                ServerMatchesDraft() && readiness.Text != Locale.T("Ready"));
            modelUsage.Children.Add(Heading("Used in operations"));
            var assignmentsForModel = DirectImageGenerator.Operations.Where(task =>
                imageConnection.RouteFor(task) == ImageGenerationRoute.ComfyWorkflow &&
                EngineForOperation(task) is { } candidate && candidate.Id == selected.Id).ToArray();
            var usageRows = Ui.Column(0);
            var usageTable = Scroll(usageRows);
            usageTable.MaxHeight = 162;
            modelUsage.Children.Add(usageTable);
            foreach (var task in assignmentsForModel)
                usageRows.Children.Add(InsetRow(RowButton(Columns("*,110,18", Caption(task.DisplayName()),
                    Caption(taskDrafts.GetValueOrDefault(task.ToString()) is { Length: > 0 } ? "Custom" : "Default"),
                    Caption("›")), () => ShowOperation(task)), vertical: 1));
            if (assignmentsForModel.Length == 0) modelUsage.Children.Add(Caption("No operations assigned."));
            var folder = Ui.TextButton("Open folder ›", () =>
            {
                var path = service.Engines.DirectoryOf(selected);
                try { using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { _ = Prompts.Alert(settingsDialog!, "Workflows", path); }
            });
            folder.Classes.Add("flat");
            modelWorkflows.Children.Add(Columns("*,Auto", Heading("Workflows"), folder));
            if (operation is not { } active || selected.Binding(active) == null) return;
            var workflowRows = new Dictionary<string, (string Title, int Nodes, bool Custom, List<AiTaskKind> Tasks)>();
            foreach (var task in operationPicker.Items.OfType<AiTaskKind>())
            {
                try
                {
                    var workflow = EngineCatalog.WorkflowFor(selected, task);
                    var id = WorkflowIdForModel(selected, task);
                    var saved = id == null ? null : workflowDraft.Workflows.GetValueOrDefault(id);
                    var key = id ?? workflow.File;
                    if (!workflowRows.TryGetValue(key, out var item))
                    {
                        var contents = saved?.Graph ?? service.Engines.ReadWorkflow(selected, workflow);
                        workflowRows.Add(key, item = (saved?.Name ?? Path.GetFileName(workflow.File), contents.Count, saved != null, []));
                    }
                    item.Tasks.Add(task);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or UnauthorizedAccessException)
                { modelWorkflows.Children.Add(Caption(error.Message)); }
            }
            foreach (var (key, row) in workflowRows)
            {
                var expanded = key == openModelWorkflow;
                var button = RowButton(Columns("1.3*,*,22",
                    Ui.Column(2, Heading(row.Title, 11), Caption(string.Format(Locale.T("{0} nodes · {1}"), row.Nodes,
                        Locale.T(row.Custom ? "Custom" : "Factory")))),
                    Caption(string.Join(" · ", row.Tasks.Select(task => Locale.T(task.DisplayName())))), Caption(expanded ? "⌄" : "›")), () =>
                {
                    openModelWorkflow = expanded ? null : key;
                    if (!expanded && !row.Tasks.Contains(active)) operationPicker.SelectedItem = row.Tasks[0];
                    RenderModels();
                });
                modelWorkflows.Children.Add(InsetRow(button, expanded ? Palette.PanelRaised : null, vertical: 1));
            }
            workflowInspector.IsVisible = openModelWorkflow != null && workflowRows.ContainsKey(openModelWorkflow);
            if (!workflowInspector.IsVisible) return;
            if (active is not (AiTaskKind.Upscale or AiTaskKind.ObjectSelection))
                modelParameters.Children.Add(Ui.TextButton("Prompt instructions for this model and operation", () => openPrompt(selected, active)));
            try
            {
                var graph = WorkflowIdForModel(selected, active) is { } id ? workflowDraft.Workflows.GetValueOrDefault(id) : null;
                var workflow = EngineCatalog.WorkflowFor(selected, active);
                var contents = graph?.Graph ?? service.Engines.ReadWorkflow(selected, workflow);
                var title = graph?.Name ?? Path.GetFileName(workflow.File);
                var metadata = string.Format(Locale.T("{0} nodes · {1}"), contents.Count, Locale.T(graph == null ? "Factory" : "Custom"));
                Control editorLink = WorkflowPreferences.Operations.Contains(active)
                    ? RowButton(Columns("*,Auto", Heading(title, 12), Caption("›")), () => OpenWorkflow(selected, active))
                    : Heading(title, 12);
                modelWorkflows.Children.Add(Card(Ui.Column(4, editorLink, Caption(metadata)), 8));
                var key = (selected.Id, active);
                var profile = changedGeneration.GetValueOrDefault(key) ?? settings.OperationFor(selected.Id, active, selected.PaidApi);
                if (graph?.Bindings.TryGetValue("steps", out var steps) == true)
                    AddParameter("Steps", steps, profile.WorkflowSteps?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        () => changedGeneration[key] = profile with { WorkflowSteps = null });
                if (graph?.Bindings.TryGetValue("sampler", out var sampler) == true)
                    AddParameter("Sampler", sampler, profile.WorkflowSampler,
                        () => changedGeneration[key] = profile with { WorkflowSampler = null });
                foreach (var (_, node) in contents)
                {
                    if (node?["inputs"] is not System.Text.Json.Nodes.JsonObject inputs) continue;
                    foreach (var (label, name, binding) in new[] { ("Steps", "steps", "steps"),
                        ("Sampler", "sampler_name", "sampler"), ("Scheduler", "scheduler", ""),
                        ("Denoise", "denoise", ""), ("CFG", "cfg", "") })
                    {
                        if (graph?.Bindings.ContainsKey(binding) == true || inputs[name] is not System.Text.Json.Nodes.JsonValue field) continue;
                        modelParameters.Children.Add(Columns("130,*", Caption(label),
                            Caption(field.ToString() + " · " + Locale.T("From workflow"))));
                    }
                }
                void AddParameter(string label, WorkflowTarget target, string? value, Action reset)
                {
                    var graphValue = contents[target.NodeId]?["inputs"]?[target.Input]?.ToString() ?? "";
                    var overridden = value != null && value != graphValue;
                    var origin = overridden ? "Set in Composa" : "From workflow";
                    var item = Columns("*,Auto", Ui.Column(3, Heading(label, 12),
                        Caption((value ?? graphValue) + " · " + Locale.T(origin))));
                    if (overridden)
                    {
                        var restore = Ui.TextButton("Use workflow value", () => { reset(); RenderModels(); });
                        Grid.SetColumn(restore, 1);
                        item.Children.Add(restore);
                    }
                    modelParameters.Children.Add(item);
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or UnauthorizedAccessException)
            {
                modelWorkflows.Children.Add(Caption(error.Message));
            }
        }
        void QueueRefresh()
        {
            if (refreshQueued) return;
            refreshQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                refreshQueued = false;
                if (lifetime.IsCancellationRequested) return;
                RenderOperations();
                if (body.SelectedIndex == 8) RenderOperation();
                if (body.SelectedIndex == 1) RenderModels();
            });
        }
        imageConnection.RouteChanged += QueueRefresh;
        textConnectionCheck.Changed += QueueRefresh;
        workflows.Changed += QueueRefresh;
        models.ChoicesChanged += QueueRefresh;
        operationPicker.SelectionChanged += (_, _) => QueueRefresh();
        engine.SelectionChanged += (_, _) => QueueRefresh();
        useDefault.Click += (_, _) => QueueRefresh();
        generation.Click += (_, _) => QueueRefresh();

        var navigation = Ui.Column(4, Heading("Settings", 21), search, matches, back);
        navigation.Margin = new Thickness(12, 20, 12, 12);
        var destinations = new (int Page, Button Button)[]
        {
            (6, NavButton("Operations", Icons.Layers, 6)),
            (1, NavButton("Models and workflows", Icons.Image, 1)),
            (0, NavButton("Connections", Icons.Link, 0)),
            (3, NavButton("Editor", Icons.Gear, 3)),
            (7, NavButton("Performance", Icons.Gear, 7))
        };
        Button NavButton(string title, Icons.Icon icon, int target) => RowButton(
            Columns("18,*", Icons.Create(icon, 16), Caption(title)), () => Navigate(target, false));
        foreach (var (destination, item) in destinations)
        {
            if (destination == 3)
            {
                var caption = Caption("Composa Studio");
                caption.Margin = new Thickness(11, 13, 0, 0);
                navigation.Children.Add(caption);
            }
            navigation.Children.Add(item);
        }
        var sidebar = new Border
        {
            Width = 218, Background = Palette.SettingsSidebar, BorderBrush = Palette.SettingsLine,
            BorderThickness = new Thickness(0, 0, 1, 0), Child = Scroll(navigation)
        };
        var settingsBody = new DockPanel();
        if (!firstRun)
        {
            DockPanel.SetDock(sidebar, Dock.Left);
            settingsBody.Children.Add(sidebar);
        }
        settingsBody.Children.Add(body);
        void ShowPage()
        {
            back.IsVisible = history.Count > 0;
            validation.IsVisible = false;
            var current = body.SelectedIndex;
            if (!firstRun && current == 5)
            {
                directPromptHost.Child = null;
                apiFields.IsVisible = true;
                textConnectionHost.Child = apiFields;
            }
            else if (textConnectionHost.Child != null)
            {
                textConnectionHost.Child = null;
                apiFields.IsVisible = selectedProvider == "direct";
                directPromptHost.Child = apiFields;
            }
            if (!firstRun && current == 11)
            {
                cliPromptHost.Child = null;
                localCodex.View.IsVisible = true;
                cliLocalText.Child = localCodex.View;
            }
            else if (cliLocalText.Child != null)
            {
                cliLocalText.Child = null;
                localCodex.View.IsVisible = selectedProvider == "local-codex";
                cliPromptHost.Child = localCodex.View;
            }
            foreach (var (destination, button) in destinations)
            {
                var selected = current == destination || destination == 6 && current is 8 or 2 ||
                    destination == 1 && current == 4 || destination == 0 && current is 5 or 11;
                button.Background = selected ? Palette.SettingsSelection : Avalonia.Media.Brushes.Transparent;
            }
            scope.Text = Locale.T(current switch
            {
                4 => "Save settings to apply workflow changes.",
                0 or 5 or 11 => "Connection fields apply to operations using this connection.",
                8 => "Route changes apply to this operation after saving settings.",
                9 => "You can finish setup later in Settings.",
                _ => ""
            });
            if (current == 1) RenderModels();
            if (current == 2) RefreshPromptProfiles(promptPack);
            if (!firstRun)
            {
                if (current == 0 && !comfyCheck.Passed) ScheduleComfyCheck();
                if (current == 2) ScheduleTextCheck();
                if (current == 5)
                {
                    if (textCatalog.Connected != true) textCatalog.ScheduleCheck();
                    if (imageConnection.ApiConnected != true) imageConnection.ScheduleApiCheck();
                }
                if (current == 11) RefreshCliLocation();
            }
            if (current == 6) RenderOperations();
            if (current == 8) RenderOperation();
            if (settingsDialog != null) Locale.Refresh(settingsDialog);
        }
        body.PageChanged += ShowPage;
        settingsDialog = new StudioSettingsWindow(owner, settingsBody, validation, scope, firstRun);
        if (firstRunBack != null)
            settingsDialog.AddSecondaryAction("Back", () => { firstRunBack(); settingsDialog.Close(false); });
        ShowPage();
        settingsDialog.Opened += (_, _) =>
        {
            if (focusField?.StartsWith("model:", StringComparison.Ordinal) == true && service.Engines.Find(focusField[6..]) is { } target)
            { SelectRoute(target); engine.Focus(); }
            else if (focusField == "key") sessionKey.Focus();
            else if (focusField == "server") url.Focus();
            else if (page == 1) engine.Focus();
            else if (page == 2) provider.Focus();
            else if (page == 5) imageConnection.ApiUrl.Focus();
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
                if (selectedProvider == "direct" || forgetDirect || !string.IsNullOrWhiteSpace(directKey.Text) ||
                    apiUrl.Text != settings.EnhancerApiUrl)
                {
                    tab = body.SelectedIndex == 5 ? 5 : 2; field = apiUrl;
                    directEndpoint = Assistant.ChatCompletionAssistantProvider.Endpoint(apiUrl.Text ?? "");
                    if (selectedProvider == "direct" && string.IsNullOrWhiteSpace(apiModel.Text))
                    {
                        tab = 2;
                        field = textModelField.Child ?? apiModel;
                        throw new FormatException("Choose the language model in Prompt enhancer.");
                    }
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
                tab = 5; field = imageConnection.ApiUrl;
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
                if (error is FormatException)
                {
                    if (!firstRun) body.SelectedIndex = tab;
                    if (tab == 2 && field != textModelField.Child) promptConnectionDetails.IsChecked = true;
                    field.Focus();
                    if (field is TextBox input) input.SelectAll();
                }
                validation.Text = Locale.Message(error.Message); validation.IsVisible = true;
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
        settings.RememberAiPrompts = rememberPrompts.IsChecked == true;
        if (!settings.RememberAiPrompts) settings.AiPromptDrafts.Clear();
        SavePromptDraft();
        ReadEnhancer(settings);
        settings.EnhancerApiCatalogs = textCatalog.Save();
        settings.EnhancerCliModels = cliModels;
        imageConnection.Save(settings);

        foreach (var (key, value) in changedGeneration)
        {
            settings.SetOperation(key.Engine, key.Task, value);
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
        var startingPrompt = string.IsNullOrEmpty(initialPrompt) ? settings.AiPromptFor(task) : initialPrompt;
        var prompt = AiGenerationControls.PromptField("Describe the result…");
        prompt.Text = startingPrompt;
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
            var estimate = paid ? PartnerPricing.EstimateForTask(task, service?.ServerCapabilities,
                service!.SelectedEngine!.ApiModel!, quality, count, variants) : null;
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
        var finalPrompt = prompt.Text ?? "";
        if (settings.RememberAiPrompts && prompt.IsVisible && prompt.IsEnabled && (accepted || finalPrompt != startingPrompt))
            settings.RememberAiPrompt(task, finalPrompt);
        if (!accepted) return null;
        if (alignment.IsVisible) settings.AiAlignMaskedEdits = alignment.IsChecked == true;
        settings.SetOperation(chosenEngine?.Id, task, CaptureProfile());
        settings.Save();
        return new(task == AiTaskKind.GenerativeExpand ? AiPromptDefaults.Expand : finalPrompt, width, height, profile.Seed, chosenEngine?.Id, variants);
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
