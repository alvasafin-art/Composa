using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.AI;
using Composa.App.AI;

namespace Composa.App.Dialogs;

public static partial class AiDialogs
{
    private const string PromptHelperRepository = "https://github.com/alvasafin-art/ComfyUI-AVS-Prompt-Helper";

    private static string CliLabel(string cli) => cli switch
    {
        "claude" => "Claude Code CLI",
        "kimi" => "Kimi CLI",
        _ => "Codex CLI"
    };

    private static TextBlock ConnectionNote(string text) => new()
    {
        Text = Locale.T(text), Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap
    };

    private static Expander RemoteEnhancerHelp()
    {
        var open = Ui.TextButton("Open the node on GitHub", () => { });
        var status = ConnectionNote("");
        open.Click += (_, _) =>
        {
            OpenPromptHelper(status);
        };
        return new Expander
        {
            Header = Locale.T("How to connect"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Palette.Panel,
            Content = Ui.Column(7,
                ConnectionNote("1. Install and sign in to the CLI on the ComfyUI computer. For Codex, run codex login as the user who starts ComfyUI."),
                ConnectionNote("2. Download AVS Prompt Helper to ComfyUI/custom_nodes/ComfyUI-AVS-Prompt-Helper."),
                ConnectionNote("3. Install requirements.txt with ComfyUI’s Python and restart ComfyUI. On Windows Portable, run python_embeded\\python.exe -m pip install -r ComfyUI\\custom_nodes\\ComfyUI-AVS-Prompt-Helper\\requirements.txt from the portable folder."),
                ConnectionNote("4. In Composa, enter the server’s LAN address and choose the CLI and model."),
                ConnectionNote("5. Choose Test connection to send a short request."),
                open, status)
        };
    }

    private static void OpenPromptHelper(TextBlock status)
    {
        try { using var process = Process.Start(new ProcessStartInfo(PromptHelperRepository) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { status.Text = PromptHelperRepository; }
    }

    private static Border HelperWarning(Action retry)
    {
        var title = StudioSettingsLayout.Heading("AVS Prompt Helper did not respond", 12);
        title.Foreground = Palette.SettingsWarningText;
        var linkStatus = ConnectionNote("");
        var link = Ui.TextButton("Open AVS Prompt Helper on GitHub ›", () => OpenPromptHelper(linkStatus));
        link.Classes.Add("flat");
        link.Foreground = Palette.Accent;
        var instructions = RemoteEnhancerHelp();
        instructions.IsVisible = false;
        var steps = StudioSettingsLayout.Columns("*,*,*",
            Step("1. Install the node", "In ComfyUI/custom_nodes/ComfyUI-AVS-Prompt-Helper"),
            Step("2. Restart the server", "Install requirements.txt with ComfyUI’s Python."),
            Step("3. Retry the check", "Composa will send a short text request."));
        return new Border
        {
            Name = "AvsPromptHelperWarning",
            IsVisible = false, Background = Palette.SettingsWarning, BorderBrush = Palette.SettingsWarningLine,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(13),
            Child = Ui.Column(8, title,
                ConnectionNote("ComfyUI is reachable. The node is missing on the server. Image generation and assigned workflows can still run. This affects prompt enhancement through this server."),
                steps, link, linkStatus,
                Ui.Row(8, Ui.TextButton("Retry check", retry), Ui.TextButton("Open instructions", () =>
                {
                    instructions.IsVisible = !instructions.IsVisible;
                    instructions.IsExpanded = instructions.IsVisible;
                })), instructions)
        };

        static Control Step(string title, string body) => new Border
        {
            BorderBrush = Palette.SettingsWarningLine, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 7, 0, 0),
            Child = Ui.Column(3, StudioSettingsLayout.Heading(title, 11), StudioSettingsLayout.Caption(body))
        };
    }

    private sealed class CodexConnectionFields
    {
        internal TextBox Path { get; }
        internal TextBox Model { get; }
        internal StackPanel View { get; }

        internal CodexConnectionFields(string path, string model, bool showModel = true, bool firstRun = false)
        {
            Path = new TextBox { Text = path, PlaceholderText = Locale.T("Detect automatically") };
            Model = new TextBox { Text = model, PlaceholderText = Locale.T("Model ID") };
            var status = ConnectionNote("");
            var detectedPath = StudioSettingsLayout.SetupInput(new TextBox { IsReadOnly = true });
            var pathBadge = new Border();
            var executableLabel = StudioSettingsLayout.SetupLabel("Executable");
            void CheckPath()
            {
                var executable = LocalCodex.FindExecutable(Path.Text);
                var found = executable != null;
                status.Text = Locale.T(found
                    ? "Codex CLI found." : "Codex CLI was not found. Install Codex or select its executable.");
                detectedPath.Text = executable ?? Path.Text;
                executableLabel.Text = Locale.T(found && string.IsNullOrWhiteSpace(Path.Text)
                    ? "Executable · detected automatically" : "Executable");
                pathBadge.Child = StudioSettingsLayout.Status(status.Text, found, !found);
                Locale.Tip(detectedPath, detectedPath.Text);
            }
            Path.TextChanged += (_, _) => CheckPath();
            CheckPath();
            var find = Ui.TextButton("Detect automatically", () =>
            {
                if (LocalCodex.FindExecutable() is { } executable)
                {
                    Path.Text = executable;
                    status.Text = Locale.T("Codex CLI found.");
                }
                else status.Text = Locale.T("Codex CLI was not found. Install Codex or select its executable.");
            });
            var choose = firstRun ? StudioSettingsLayout.SetupLink("Change manually", () => { })
                : Ui.TextButton("Choose…", () => { });
            choose.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(choose) is not { } top) return;
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Locale.T("Select the Codex executable"), AllowMultiple = false
                });
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } file)
                {
                    Path.Text = file;
                }
            };
            var cli = Ui.Label("Codex CLI");
            Control identity = showModel ? StudioSettingsLayout.Columns("*,*", StudioSettingsLayout.Field("CLI", cli),
                StudioSettingsLayout.Field("Text model", Model)) : cli;
            if (firstRun)
            {
                var row = StudioSettingsLayout.Columns("19,*,Auto", pathBadge, detectedPath, choose);
                row.ColumnSpacing = 9;
                View = Ui.Column(4, executableLabel, row);
                Avalonia.Automation.AutomationProperties.SetName(detectedPath, Locale.T("Executable"));
            }
            else
                View = Ui.Column(6, identity,
                    StudioSettingsLayout.Field("Executable", StudioSettingsLayout.Columns("*,Auto", Path, Ui.Row(5, find, choose))), status);
        }
    }

    private sealed class ConnectionCheck
    {
        private readonly string hint;
        private readonly TextBlock status;
        private readonly Border? warning;
        private readonly Func<Task> run;
        private CancellationTokenSource? current;
        private int revision;
        private int scheduled;
        private readonly bool inlineAction;
        internal StackPanel View { get; }
        internal Button ActionButton { get; }
        internal bool Passed { get; private set; }
        internal bool Failed { get; private set; }
        internal string StatusText => status.Text ?? "";
        internal Border Light() => StudioSettingsLayout.ConnectionLight(
            StatusText.Length > 0 ? StatusText : "Connection has not been checked", Failed ? false : Passed ? true : null);
        internal event Action? Changed;

        internal ConnectionCheck(CancellationToken lifetime, Func<CancellationToken, Task<string>> check, string hint,
            bool showHelperWarning = false, string? buttonText = null, bool inlineAction = false, bool automatic = false)
        {
            this.hint = hint;
            this.inlineAction = inlineAction || automatic;
            status = ConnectionNote(hint);
            var text = buttonText ?? (showHelperWarning ? "Test prompt" : "Test connection");
            var test = ActionButton = inlineAction ? StudioSettingsLayout.SetupLink(text, () => { }, fontSize: 10)
                : Ui.TextButton(text, () => { });
            var cancel = Ui.TextButton("Cancel", () => current?.Cancel());
            cancel.IsVisible = false;
            View = automatic ? Ui.Column(6, status) : inlineAction ? Ui.Column(6, StudioSettingsLayout.Columns("*,Auto", status, cancel)) : showHelperWarning
                ? Ui.Column(8, StudioSettingsLayout.Columns("*,Auto", status, Ui.Row(6, cancel, test)))
                : Ui.Column(5, Ui.Row(5, test, cancel), status);
            run = Run;
            if (showHelperWarning)
            {
                warning = HelperWarning(() => _ = Run());
                View.Children.Add(warning);
            }
            if (this.inlineAction) View.IsVisible = false;
            test.Click += async (_, _) => await Run();
            async Task Run()
            {
                if (current is { IsCancellationRequested: false } || lifetime.IsCancellationRequested) return;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                current = request;
                var expected = revision;
                test.IsEnabled = false;
                View.IsVisible = true;
                cancel.IsVisible = true;
                if (warning != null) warning.IsVisible = false;
                Passed = Failed = false;
                status.Text = Locale.T(buttonText == null ? "Testing the selected model…" : "Checking connection…");
                Changed?.Invoke();
                try
                {
                    var message = await check(request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    if (expected == revision)
                    {
                        status.Text = Locale.Message(message);
                        Passed = true;
                    }
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                { if (expected == revision && !lifetime.IsCancellationRequested) status.Text = Locale.T("Test cancelled."); }
                catch (AiTaskService.PromptHelperMissingException) when (warning != null)
                {
                    if (expected == revision && !request.IsCancellationRequested)
                    {
                        status.Text = Locale.T("Server responded.");
                        warning.IsVisible = true;
                        Failed = true;
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (expected == revision && !request.IsCancellationRequested)
                    {
                        status.Text = Locale.Message(error.Message);
                        Failed = true;
                    }
                }
                finally
                {
                    if (current == request)
                    {
                        current = null;
                        test.IsEnabled = true;
                        cancel.IsVisible = false;
                        if (this.inlineAction) View.IsVisible = !Passed && !string.IsNullOrEmpty(status.Text);
                        Changed?.Invoke();
                    }
                }
            }
            async Task ScheduleAsync(Func<bool> ready, int ticket)
            {
                try
                {
                    await Task.Delay(650, lifetime);
                    if (ticket == scheduled && ready()) await Run();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
            schedule = ready => _ = ScheduleAsync(ready, ++scheduled);
        }

        private readonly Action<Func<bool>> schedule;
        internal void Schedule(Func<bool> ready) => schedule(ready);
        internal void Start() => _ = run();

        internal void Reset()
        {
            revision++;
            scheduled++;
            current?.Cancel();
            Passed = Failed = false;
            status.Text = Locale.T(hint);
            if (inlineAction) View.IsVisible = false;
            if (warning != null) warning.IsVisible = false;
            Changed?.Invoke();
        }
    }

    private static SettingsStatus? CheckStatus(ConnectionCheck check) => check.Passed || check.Failed
        ? StudioSettingsLayout.Status(check.StatusText, check.Passed, check.Failed) : null;

    internal static int ConnectionPageFor(ImageGenerationRoute route) => route switch
    {
        ImageGenerationRoute.DirectApi => 5,
        ImageGenerationRoute.LocalCodex or ImageGenerationRoute.RemoteCodex => 11,
        _ => 0
    };

    private static string ReasoningLabel(string? value) => Locale.T(value switch
    {
        null or "default" => "Default", "none" => "No reasoning", "minimal" => "Minimal reasoning", "low" => "Low reasoning",
        "medium" => "Medium reasoning", "high" => "High reasoning", "xhigh" => "Extra high reasoning", _ => value
    });

    private sealed class ImageConnectionFields
    {
        private ImageGenerationRoute route;
        private readonly TextBox path, url, key, displayName, codexName;
        private readonly TextBox comfyServer;
        private string codexLanguageModel, codexReasoningEffort;
        private string remoteLanguageModel, remoteReasoningEffort;
        private readonly string upscalePath, segmentationPath, segmentationModel;
        private readonly Dictionary<AiTaskKind, ImageGenerationRoute> routes;
        private readonly Dictionary<AiTaskKind, string> modelOverrides;
        private readonly ConnectionModelCatalog catalog;
        private bool forgetKey;
        internal StackPanel View { get; }
        internal Control ApiUrl => url;
        internal Control CliSettings { get; }
        internal ConnectionCheck CliCheck { get; }
        internal Control RemoteCliSettings { get; }
        internal ConnectionCheck RemoteCliCheck { get; }
        internal Action ResetRemoteCli { get; }
        internal bool? ApiConnected => catalog.Connected;
        internal string ApiCheckMessage => catalog.CheckMessage;
        internal event Action? ApiCheckChanged;
        internal void EnableApiMonitoring(Func<bool> visible) => catalog.EnableMonitoring(visible);
        internal void ScheduleApiCheck() => catalog.ScheduleCheck();
        internal StackPanel SetupView => View;
        internal Border SetupIndicator { get; } = new();
        internal event Action? RouteChanged;
        internal ImageGenerationRoute RouteFor(AiTaskKind task) => routes[task];

        internal void SetRoute(AiTaskKind task, ImageGenerationRoute selected)
        {
            if (routes[task] == selected) return;
            routes[task] = selected;
            RouteChanged?.Invoke();
        }

        internal DirectImageGenerator ModelFor(AiTaskKind task) => Draft() with
        {
            Route = routes[task], Task = task, ApiModel = modelOverrides.GetValueOrDefault(task) ??
                (ImageOperationModels.IsUtility(task) ? "" : catalog.LegacySelection),
            UtilityPath = task == AiTaskKind.Upscale ? upscalePath : segmentationPath,
            UtilityModel = segmentationModel
        };
        internal ImageModelChoice DefaultModel(AiTaskKind task)
        {
            var direct = ModelFor(task);
            var label = direct.Route == ImageGenerationRoute.DirectApi ? catalog.Models.FirstOrDefault(item => item.Id == direct.Model).Label : null;
            return ImageOperationModels.DirectChoice(direct, label ?? "", displayName.Text?.Trim());
        }

        internal IEnumerable<ImageModelChoice> ModelOptions(AiTaskKind task)
        {
            var direct = ModelFor(task) with { Route = ImageGenerationRoute.DirectApi };
            bool enabled;
            try
            {
                enabled = catalog.CanFetch && (!string.IsNullOrWhiteSpace(key.Text) || !forgetKey &&
                    !string.IsNullOrWhiteSpace(DirectImageGenerator.ReadKey(DirectImageGenerator.Endpoint(direct.ApiAddress))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.ComponentModel.Win32Exception)
            { enabled = false; }
            return ImageOperationModels.DirectChoices(direct, catalog.Models, displayName.Text?.Trim(), enabled, catalog.GenerationOnly, catalog.Upscalers);
        }

        internal void SetDefault(AiTaskKind task, ImageModelChoice choice)
        {
            if (choice.Direct is { Route: ImageGenerationRoute.DirectApi } direct)
            {
                if (direct.Model.Length > 0 && task != AiTaskKind.ObjectSelection) modelOverrides[task] = direct.Model;
                else modelOverrides.Remove(task);
            }
            routes[task] = choice.Route;
            RouteChanged?.Invoke();
        }

        internal IReadOnlyList<EngineProfile> PromptProfiles()
        {
            var configured = Draft();
            var direct = catalog.Models.Select(item => item.Id).Concat(modelOverrides.Values).Append(configured.ApiModel)
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)
                .Select(id => (configured with { Route = ImageGenerationRoute.DirectApi, ApiModel = id }).PromptProfile());
            if (LocalCodex.FindExecutable(path.Text) != null || routes.ContainsValue(ImageGenerationRoute.LocalCodex))
                direct = direct.Prepend((configured with { Route = ImageGenerationRoute.LocalCodex }).PromptProfile());
            if ((configured with { Route = ImageGenerationRoute.RemoteCodex }).Availability(AiTaskKind.GenerateImage) == null)
                direct = direct.Prepend((configured with { Route = ImageGenerationRoute.RemoteCodex }).PromptProfile());
            return direct.ToArray();
        }

        internal ImageConnectionFields(Settings settings, AiTaskService service, TextBox server, CancellationToken lifetime, bool firstRun)
        {
            routes = DirectImageGenerator.Operations.ToDictionary(task => task, settings.ImageRouteFor);
            comfyServer = server;
            remoteLanguageModel = settings.ImageRemoteCodexModel;
            remoteReasoningEffort = settings.ImageRemoteCodexEffort;
            modelOverrides = new(settings.ImageOperationApiModels);
            route = routes[AiTaskKind.GenerateImage];
            var codex = new CodexConnectionFields(settings.ImageGenerationCodexPath, "gpt-image-2", showModel: false, firstRun: firstRun);
            path = codex.Path;
            url = new TextBox { Text = settings.ImageGenerationApiUrl, Name = "ImageGenerationApiUrl" };
            var model = new TextBox { Text = settings.ImageGenerationApiModel };
            displayName = new TextBox { Text = settings.ImageApiDisplayName, PlaceholderText = Locale.T("Direct image API") };
            codexName = new TextBox { Text = settings.ImageCodexDisplayName, PlaceholderText = DirectImageGenerator.CodexImageName };
            codexLanguageModel = settings.ImageCodexLanguageModel?.Trim() ?? "";
            codexReasoningEffort = settings.ImageCodexReasoningEffort?.Trim() ?? "default";
            key = new TextBox { Name = "ImageApiKey", PasswordChar = '●', PlaceholderText = Locale.T("Enter a new key to save or replace it") };
            catalog = new ConnectionModelCatalog(settings.ImageApiCatalogs, url, key, model, () =>
            {
                var api = DirectImageApi.Parse(url.Text ?? "");
                return (api.CredentialEndpoint, api.CatalogEndpoint);
            }, endpoint => !string.IsNullOrWhiteSpace(key.Text) ? key.Text.Trim()
                : forgetKey ? Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY") : DirectImageGenerator.ReadKey(endpoint),
                lifetime, images: true, compactSetup: firstRun, autoProbe: !firstRun, savedGenerationOnly: settings.ImageApiGenerationOnly,
                savedUpscalers: settings.ImageApiUpscalers, savedRates: settings.ImageApiRates);
            void ForgetKey()
            {
                forgetKey = true;
                key.Text = "";
                catalog.InvalidateCheck();
                catalog.ScheduleCheck();
            }
            var forget = Ui.TextButton("Forget saved key", ForgetKey);
            if (firstRun)
            {
                var forgetMenu = new MenuItem { Header = Locale.T("Forget saved key") };
                forgetMenu.Click += (_, _) => ForgetKey();
                key.ContextMenu = new ContextMenu { Items = { forgetMenu } };
            }
            upscalePath = settings.UpscaleCliPath?.Trim() ?? "";
            segmentationPath = settings.SegmentationCliPath?.Trim() ?? "";
            segmentationModel = settings.SegmentationCliModel?.Trim() ?? "";
            codexName.TextChanged += (_, _) => RouteChanged?.Invoke();
            var nameField = firstRun ? StudioSettingsLayout.SetupField("Display name", StudioSettingsLayout.SetupInput(displayName)) : null;
            var protocol = new TextBox { IsReadOnly = true };
            var api = firstRun ? Ui.Column(9, StudioSettingsLayout.SetupColumns(
                    StudioSettingsLayout.SetupField("Base URL", StudioSettingsLayout.SetupInput(url)),
                    StudioSettingsLayout.SetupField("API key", StudioSettingsLayout.SetupInput(key))), catalog.View)
                : StudioSettingsLayout.ApiConnectionForm(displayName, protocol, url, key, forget, catalog.View);
            var serverAddress = new TextBox { Text = server.Text, Name = "ImageComfyServer" };
            serverAddress.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.TextProperty && serverAddress.Text != server.Text) server.Text = serverAddress.Text;
            };
            server.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.TextProperty && serverAddress.Text != server.Text) serverAddress.Text = server.Text;
            };
            var workflow = firstRun ? StudioSettingsLayout.SetupColumns(
                    StudioSettingsLayout.SetupField("ComfyUI server", StudioSettingsLayout.SetupInput(serverAddress)))
                : StudioSettingsLayout.Columns("*,*", StudioSettingsLayout.Field("ComfyUI server", serverAddress));
            Control? methodField = null;
            var languageModel = StudioSettingsLayout.SetupInput(new ComboBox
            {
                Name = "ImageCodexLanguageModel", IsEnabled = false, PlaceholderText = Locale.T("Connect Codex CLI to load models.")
            });
            languageModel.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LocalCodex.TextModel>((item, _) =>
                new TextBlock { Text = item == null ? "" : item.Title == item.Id ? item.Id : item.Title + " · " + item.Id,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            languageModel.SelectionBoxItemTemplate = languageModel.ItemTemplate;
            var reasoning = StudioSettingsLayout.SetupInput(new ComboBox { Name = "ImageCodexReasoningEffort", IsEnabled = false });
            reasoning.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((item, _) =>
                new TextBlock { Text = ReasoningLabel(item) });
            reasoning.SelectionBoxItemTemplate = reasoning.ItemTemplate;
            bool refreshingModels = false;

            void PresentReasoning(LocalCodex.TextModel? model)
            {
                var efforts = model?.Efforts ?? [];
                var choices = efforts.Prepend("default").Distinct(StringComparer.Ordinal).ToArray();
                reasoning.ItemsSource = choices;
                reasoning.SelectedItem = choices.Contains(codexReasoningEffort, StringComparer.Ordinal) ? codexReasoningEffort : "default";
                reasoning.IsEnabled = choices.Length > 1;
                if (model != null) codexReasoningEffort = (string)reasoning.SelectedItem!;
            }

            void PresentModels(IReadOnlyList<LocalCodex.TextModel> entries)
            {
                refreshingModels = true;
                var selected = entries.FirstOrDefault(item => item.Id == codexLanguageModel) ??
                    entries.FirstOrDefault(item => item.IsDefault) ?? entries.FirstOrDefault();
                languageModel.ItemsSource = entries;
                languageModel.SelectedItem = selected;
                languageModel.IsEnabled = entries.Count > 0;
                if (selected != null) codexLanguageModel = selected.Id;
                PresentReasoning(selected);
                refreshingModels = false;
            }

            languageModel.SelectionChanged += (_, _) =>
            {
                if (refreshingModels || languageModel.SelectedItem is not LocalCodex.TextModel selected) return;
                codexLanguageModel = selected.Id;
                refreshingModels = true;
                PresentReasoning(selected);
                refreshingModels = false;
            };
            reasoning.SelectionChanged += (_, _) =>
            {
                if (!refreshingModels && reasoning.SelectedItem is string value) codexReasoningEffort = value;
            };
            PresentModels([]);

            var remoteModel = StudioSettingsLayout.SetupInput(new ComboBox
            {
                Name = "RemoteImageCodexLanguageModel", IsEnabled = false,
                PlaceholderText = Locale.T("Connect to load server models.")
            });
            var remoteEffort = StudioSettingsLayout.SetupInput(new ComboBox { Name = "RemoteImageCodexReasoningEffort", IsEnabled = false });
            remoteEffort.ItemTemplate = reasoning.ItemTemplate;
            remoteEffort.SelectionBoxItemTemplate = remoteEffort.ItemTemplate;
            var remoteStatus = ConnectionNote("");
            bool fillingRemote = false;

            void PresentRemoteEffort(RemoteCodexImage.Model? selected)
            {
                var choices = selected == null ? [] : selected.Reasoning.Prepend("default").Distinct().ToArray();
                remoteEffort.ItemsSource = choices;
                remoteEffort.SelectedItem = choices.Contains(remoteReasoningEffort) ? remoteReasoningEffort : choices.FirstOrDefault();
                remoteEffort.IsEnabled = choices.Length > 1;
                if (selected != null) remoteReasoningEffort = (string)remoteEffort.SelectedItem!;
            }
            void PresentRemoteModels(RemoteCodexImage.Model[] entries)
            {
                fillingRemote = true;
                var selected = string.IsNullOrWhiteSpace(remoteLanguageModel) ? entries.FirstOrDefault(item => item.IsDefault)
                    : entries.FirstOrDefault(item => item.Id == remoteLanguageModel);
                remoteModel.ItemsSource = entries;
                remoteModel.SelectedItem = selected;
                remoteModel.IsEnabled = entries.Length > 0;
                if (selected != null) remoteLanguageModel = selected.Id;
                PresentRemoteEffort(selected);
                fillingRemote = false;
            }
            remoteModel.SelectionChanged += (_, _) =>
            {
                if (fillingRemote || remoteModel.SelectedItem is not RemoteCodexImage.Model selected) return;
                remoteLanguageModel = selected.Id;
                fillingRemote = true;
                PresentRemoteEffort(selected);
                fillingRemote = false;
                RouteChanged?.Invoke();
            };
            remoteEffort.SelectionChanged += (_, _) =>
            {
                if (!fillingRemote && remoteEffort.SelectedItem is string effort) remoteReasoningEffort = effort;
            };
            async Task<string> DiscoverRemoteImageModels(CancellationToken token)
            {
                PresentRemoteModels([]);
                var result = await RemoteCodexImage.DiscoverAsync(server.Text ?? "", token);
                token.ThrowIfCancellationRequested();
                PresentRemoteModels(result.Models);
                remoteStatus.Text = Locale.T("Codex CLI · AVS Prompt Helper") + " " + result.Version;
                if (remoteModel.SelectedItem == null)
                    throw new InvalidOperationException("Choose an available ComfyUI server language model in AI Settings → CLI.");
                return "Server CLI is signed in. Image generation is available.";
            }
            RemoteCliCheck = new ConnectionCheck(lifetime, DiscoverRemoteImageModels, "", buttonText: "Check connection");
            RemoteCliSettings = Ui.Column(10, ConnectionNote("Codex CLI · ComfyUI server"), remoteStatus,
                StudioSettingsLayout.SetupColumns(StudioSettingsLayout.SetupField("Language model", remoteModel),
                    StudioSettingsLayout.SetupField("Reasoning effort", remoteEffort)),
                firstRun ? new Border() : RemoteCliCheck.View);
            ResetRemoteCli = () =>
            {
                RemoteCliCheck.Reset();
                PresentRemoteModels([]);
                remoteStatus.Text = "";
            };

            async Task<string> DiscoverImageModels(CancellationToken token)
            {
                var models = await LocalCodex.DiscoverModelsAsync(path.Text ?? "", token, forImageGeneration: true);
                token.ThrowIfCancellationRequested();
                PresentModels(models);
                return models.Count > 0 ? "Codex CLI is signed in. Models loaded." : "The CLI reported no models.";
            }

            var check = new ConnectionCheck(lifetime, async token =>
            {
                if (route == ImageGenerationRoute.ComfyWorkflow)
                {
                    await service.TestConnectionAsync(token, server.Text ?? "");
                    return "Connected";
                }
                if (route == ImageGenerationRoute.LocalCodex)
                    return await DiscoverImageModels(token);
                if (route == ImageGenerationRoute.RemoteCodex)
                    return await DiscoverRemoteImageModels(token);
                var count = await catalog.FetchAsync(token);
                return count > 0 ? "The API responded. Models are available below." : "The API reported no models.";
            }, "", buttonText: "Check connection");
            CliCheck = new ConnectionCheck(lifetime, DiscoverImageModels, "", automatic: true);
            void RefreshIndicator() => SetupIndicator.Child = route == ImageGenerationRoute.DirectApi
                ? catalog.CheckStatus : CheckStatus(check);
            void Refresh()
            {
                check.Reset();
                workflow.IsVisible = route is ImageGenerationRoute.ComfyWorkflow or ImageGenerationRoute.RemoteCodex;
                RemoteCliSettings.IsVisible = !firstRun || route == ImageGenerationRoute.RemoteCodex;
                if (nameField != null) api.IsVisible = nameField.IsVisible = route == ImageGenerationRoute.DirectApi;
                check.View.IsVisible = !firstRun || route != ImageGenerationRoute.DirectApi;
                codex.View.IsVisible = !firstRun || route == ImageGenerationRoute.LocalCodex;
                try { protocol.Text = DirectImageApi.Parse(url.Text ?? "").Protocol; }
                catch (FormatException) { protocol.Text = ""; }
                if (methodField != null) methodField.IsVisible = firstRun || route != ImageGenerationRoute.DirectApi;
                if (firstRun) RefreshIndicator();
            }
            var connectionPicker = Ui.Combo(DirectImageGenerator.Connections, route, value => firstRun && value == ImageGenerationRoute.DirectApi
                ? "Third-party API" : DirectImageGenerator.RouteLabel(value), value =>
            {
                route = value;
                if (firstRun) SetRoute(AiTaskKind.GenerateImage, value);
                Refresh();
            }, double.NaN);
            methodField = firstRun ? StudioSettingsLayout.SetupField("Method", StudioSettingsLayout.SetupInput(connectionPicker))
                : StudioSettingsLayout.Field("Connection method", connectionPicker);
            View = firstRun
                ? Ui.Column(12, StudioSettingsLayout.SetupColumns(methodField, nameField!), workflow, codex.View, RemoteCliSettings, check.View, api)
                : Ui.Column(0, api);
            CliSettings = firstRun ? new Border() : Ui.Column(10, codex.View,
                StudioSettingsLayout.SetupColumns(
                    StudioSettingsLayout.SetupField("Model ID", StudioSettingsLayout.SetupInput(new TextBox { Text = "gpt-image-2", IsReadOnly = true })),
                    StudioSettingsLayout.SetupField("Display name", StudioSettingsLayout.SetupInput(codexName))),
                StudioSettingsLayout.SetupColumns(StudioSettingsLayout.SetupField("Language model", languageModel),
                    StudioSettingsLayout.SetupField("Reasoning effort", reasoning)),
                CliCheck.View);
            path.TextChanged += (_, _) =>
            {
                CliCheck.Reset();
                PresentModels([]);
                CliCheck.Schedule(() => CliSettings.IsEffectivelyVisible && TopLevel.GetTopLevel(CliSettings) != null);
            };
            catalog.Changed += () => RouteChanged?.Invoke();
            catalog.CheckChanged += () => ApiCheckChanged?.Invoke();
            if (firstRun)
            {
                check.Changed += RefreshIndicator;
                catalog.CheckChanged += RefreshIndicator;
            }
            foreach (var input in new[] { model, url, key, path }) input.TextChanged += (_, _) => Refresh();
            server.TextChanged += (_, _) => { check.Reset(); ResetRemoteCli(); };
            forget.Click += (_, _) => Refresh();
            View.AttachedToVisualTree += (_, _) => Refresh();
            Refresh();
        }

        private DirectImageGenerator Draft() => new(route, path.Text?.Trim() ?? "", url.Text?.Trim() ?? "", catalog.LegacySelection)
        { CodexDisplayName = codexName.Text?.Trim() ?? "", CodexLanguageModel = codexLanguageModel,
            CodexReasoningEffort = codexReasoningEffort, ComfyServer = comfyServer.Text?.Trim() ?? "",
            RemoteLanguageModel = remoteLanguageModel, RemoteReasoningEffort = remoteReasoningEffort };

        internal void Validate(out Control field, Dictionary<string, string?> changes)
        {
            field = url;
            var draft = Draft();
            var hasImageApi = routes.Any(item => item.Value == ImageGenerationRoute.DirectApi &&
                (!ImageOperationModels.IsUtility(item.Key) || item.Key == AiTaskKind.Upscale && modelOverrides.ContainsKey(item.Key)));
            if (hasImageApi && draft.ApiAddress.Length > 0 || forgetKey || !string.IsNullOrWhiteSpace(key.Text))
            {
                var endpoint = DirectImageGenerator.Endpoint(draft.ApiAddress);
                CollectKey(UserApiKeys.Target("ImageApi", endpoint), key, forgetKey);
            }
            void CollectKey(string target, TextBox entry, bool forget)
            {
                var value = entry.Text?.Trim();
                if (!forget && string.IsNullOrEmpty(value)) return;
                value = string.IsNullOrEmpty(value) ? null : value;
                changes[target] = value;
            }
        }

        internal void Save(Settings settings)
        {
            var draft = Draft();
            settings.ImageOperationRoutes = new(routes);
            settings.ImageGenerationCodexPath = draft.CodexPath;
            settings.ImageGenerationApiUrl = draft.ApiAddress;
            settings.ImageGenerationApiModel = draft.ApiModel;
            settings.ImageApiDisplayName = displayName.Text?.Trim() ?? "";
            settings.ImageCodexDisplayName = draft.CodexDisplayName;
            settings.ImageCodexLanguageModel = draft.CodexLanguageModel;
            settings.ImageCodexReasoningEffort = draft.CodexReasoningEffort;
            settings.ImageRemoteCodexModel = draft.RemoteLanguageModel;
            settings.ImageRemoteCodexEffort = draft.RemoteReasoningEffort;
            settings.ImageOperationApiModels = new(modelOverrides);
            settings.ImageApiCatalogs = catalog.Save();
            settings.ImageApiGenerationOnly = catalog.SaveImageCapabilities();
            settings.ImageApiUpscalers = catalog.SaveUpscalers();
            settings.ImageApiRates = catalog.SaveRates();
        }
    }
}
