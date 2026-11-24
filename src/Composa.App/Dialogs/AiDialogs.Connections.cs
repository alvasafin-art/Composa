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

        internal CodexConnectionFields(string path, string model, bool showModel = true)
        {
            Path = new TextBox { Text = path, PlaceholderText = Locale.T("Detect automatically") };
            Model = new TextBox { Text = model, PlaceholderText = Locale.T("Model ID") };
            var status = ConnectionNote("");
            void CheckPath() => status.Text = Locale.T(LocalCodex.FindExecutable(Path.Text) != null
                ? "Codex CLI found." : "Codex CLI was not found. Install Codex or select its executable.");
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
            var choose = Ui.TextButton("Choose…", () => { });
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
        internal StackPanel View { get; }
        internal bool Passed { get; private set; }
        internal bool Failed { get; private set; }
        internal string StatusText => status.Text ?? "";
        internal event Action? Changed;

        internal ConnectionCheck(CancellationToken lifetime, Func<CancellationToken, Task<string>> check, string hint,
            bool showHelperWarning = false, string? buttonText = null)
        {
            this.hint = hint;
            status = ConnectionNote(hint);
            var test = Ui.TextButton(buttonText ?? (showHelperWarning ? "Test prompt" : "Test connection"), () => { });
            var cancel = Ui.TextButton("Cancel", () => current?.Cancel());
            cancel.IsVisible = false;
            View = showHelperWarning
                ? Ui.Column(8, StudioSettingsLayout.Columns("*,Auto", status, Ui.Row(6, cancel, test)))
                : Ui.Column(5, Ui.Row(5, test, cancel), status);
            run = Run;
            if (showHelperWarning)
            {
                warning = HelperWarning(() => _ = Run());
                View.Children.Add(warning);
            }
            test.Click += async (_, _) => await Run();
            async Task Run()
            {
                if (current != null) return;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                current = request;
                var expected = revision;
                test.IsEnabled = false;
                cancel.IsVisible = true;
                if (warning != null) warning.IsVisible = false;
                Passed = Failed = false;
                status.Text = Locale.T("Testing the selected model…");
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
                    current = null;
                    test.IsEnabled = true;
                    cancel.IsVisible = false;
                    Changed?.Invoke();
                }
            }
        }

        internal void Start() => _ = run();

        internal void Reset()
        {
            revision++;
            current?.Cancel();
            Passed = Failed = false;
            status.Text = Locale.T(hint);
            if (warning != null) warning.IsVisible = false;
            Changed?.Invoke();
        }
    }

    private sealed class ImageConnectionFields
    {
        private ImageGenerationRoute route;
        private readonly TextBox path, url, key, displayName;
        private readonly TextBox upscalePath, segmentationPath, segmentationModel, polzaKey, falKey;
        private readonly Dictionary<AiTaskKind, ImageGenerationRoute> routes;
        private readonly Dictionary<AiTaskKind, string> modelOverrides;
        private readonly ConnectionModelCatalog catalog;
        private readonly Dictionary<AiTaskKind, ComboBox> operationPickers = [];
        private bool forgetKey;
        private bool forgetPolza, forgetFal;
        internal StackPanel View { get; }
        internal Control Picker { get; }
        internal Control OperationPicker { get; }
        internal Control ApiUrl => url;
        internal StackPanel SetupView => View;
        internal event Action? RouteChanged;
        internal ImageGenerationRoute RouteFor(AiTaskKind task) => routes[task];

        internal Control RouteField(AiTaskKind task)
        {
            var source = operationPickers[task];
            var choices = task == AiTaskKind.ObjectSelection
                ? new[] { ImageGenerationRoute.BuiltIn }.Concat(DirectImageGenerator.Connections).ToArray()
                : DirectImageGenerator.Connections;
            var available = choices.Where(value => value != ImageGenerationRoute.LocalCodex || value == routes[task] ||
                task is AiTaskKind.Upscale or AiTaskKind.ObjectSelection || LocalCodex.FindExecutable(path.Text) != null).ToArray();
            var field = Ui.Combo(available, routes[task], DirectImageGenerator.RouteLabel,
                value => source.SelectedIndex = Array.IndexOf(choices, value), double.NaN);
            return field;
        }

        internal void SetRoute(AiTaskKind task, ImageGenerationRoute selected)
        {
            var choices = task == AiTaskKind.ObjectSelection
                ? new[] { ImageGenerationRoute.BuiltIn }.Concat(DirectImageGenerator.Connections).ToArray()
                : DirectImageGenerator.Connections;
            operationPickers[task].SelectedIndex = Array.IndexOf(choices, selected);
        }

        internal void SelectConnection(AiTaskKind task)
        {
            if (routes[task] != ImageGenerationRoute.BuiltIn) Configure(routes[task]);
        }

        internal void Configure(ImageGenerationRoute connection) =>
            ((ComboBox)Picker).SelectedIndex = Array.IndexOf(DirectImageGenerator.Connections, connection);

        internal DirectImageGenerator ModelFor(AiTaskKind task) =>
            Draft() with { Route = routes[task], Task = task, ApiModel = modelOverrides.GetValueOrDefault(task) ?? Draft().ApiModel };

        internal string DisplayName => string.IsNullOrWhiteSpace(displayName.Text) ? Locale.T("Direct image API") : displayName.Text.Trim();
        internal int CatalogSize => catalog.Models.Count;

        internal string ModelLabel(AiTaskKind task)
        {
            var selected = ModelFor(task);
            if (task is AiTaskKind.Upscale or AiTaskKind.ObjectSelection) return selected.Label;
            return catalog.Models.FirstOrDefault(item => item.Id == selected.Model).Label
                ?? (selected.Model.Length > 0 ? selected.Model : Locale.T("Choose a model"));
        }

        internal Control ConnectionField(AiTaskKind task, bool credits)
        {
            var current = RouteFor(task);
            var label = current switch
            {
                ImageGenerationRoute.DirectApi when catalog.CanFetch => DisplayName,
                ImageGenerationRoute.LocalCodex when task == AiTaskKind.Upscale => "Real-ESRGAN",
                ImageGenerationRoute.LocalCodex when task == AiTaskKind.ObjectSelection => "rembg",
                ImageGenerationRoute.LocalCodex when LocalCodex.FindExecutable(path.Text) != null => "Codex CLI",
                ImageGenerationRoute.ComfyWorkflow when credits => Locale.T("Comfy credits"),
                ImageGenerationRoute.ComfyWorkflow => Locale.T("ComfyUI server"),
                ImageGenerationRoute.BuiltIn => Locale.T("Built-in"),
                _ => ""
            };
            return new ComboBox { ItemsSource = label.Length > 0 ? new[] { label } : Array.Empty<string>(),
                SelectedIndex = label.Length > 0 ? 0 : -1, PlaceholderText = Locale.T("No configured connection"),
                HorizontalAlignment = HorizontalAlignment.Stretch };
        }

        internal IReadOnlyList<EngineProfile> PromptProfiles()
        {
            var configured = Draft();
            var direct = catalog.Models.Select(item => item.Id).Concat(modelOverrides.Values).Append(configured.ApiModel)
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)
                .Select(id => (configured with { Route = ImageGenerationRoute.DirectApi, ApiModel = id }).PromptProfile());
            return (LocalCodex.FindExecutable(path.Text) != null || routes.ContainsValue(ImageGenerationRoute.LocalCodex)
                ? direct.Prepend((configured with { Route = ImageGenerationRoute.LocalCodex }).PromptProfile()) : direct).ToArray();
        }

        internal Control ApiModelField(AiTaskKind task)
        {
            var ids = catalog.Models;
            var current = ModelFor(task).ApiModel;
            var options = ids.Select(value => value.Id).Append(current).Distinct().Where(id => id.Length != 0).ToArray();
            var picker = Ui.Combo(options, current, id => id.Length == 0 ? Locale.T("Choose a model")
                : ids.FirstOrDefault(item => item.Id == id).Label ?? id, id =>
            {
                if (id.Length == 0) modelOverrides.Remove(task);
                else modelOverrides[task] = id;
                RouteChanged?.Invoke();
            }, double.NaN);
            picker.PlaceholderText = Locale.T("Choose a model");
            return picker;
        }

        internal void ResetModel(AiTaskKind task) => modelOverrides.Remove(task);

        internal ImageConnectionFields(Settings settings, AiTaskService service, TextBox server, CancellationToken lifetime, bool firstRun)
        {
            routes = DirectImageGenerator.Operations.ToDictionary(task => task, settings.ImageRouteFor);
            modelOverrides = new(settings.ImageOperationApiModels);
            route = routes[AiTaskKind.GenerateImage];
            var codex = new CodexConnectionFields(settings.ImageGenerationCodexPath, "gpt-image-2", showModel: false);
            path = codex.Path;
            url = new TextBox { Text = settings.ImageGenerationApiUrl, Name = "ImageGenerationApiUrl" };
            var model = new TextBox { Text = settings.ImageGenerationApiModel };
            displayName = new TextBox { Text = settings.ImageApiDisplayName, PlaceholderText = Locale.T("Direct image API") };
            key = new TextBox { PasswordChar = '●', PlaceholderText = Locale.T("Enter a new key to save or replace it") };
            catalog = new ConnectionModelCatalog(settings.ImageApiCatalogs, url, key, model, () =>
            {
                var api = DirectImageApi.Parse(url.Text ?? "");
                return (api.CredentialEndpoint, api.CatalogEndpoint);
            }, endpoint => !string.IsNullOrWhiteSpace(key.Text) ? key.Text.Trim()
                : forgetKey ? Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY") : DirectImageGenerator.ReadKey(endpoint), lifetime, images: true);
            var forget = Ui.TextButton("Forget saved key", () => { key.Text = ""; forgetKey = true; });
            upscalePath = new TextBox { Text = settings.UpscaleCliPath, PlaceholderText = "realesrgan-ncnn-vulkan" };
            segmentationPath = new TextBox { Text = settings.SegmentationCliPath, PlaceholderText = "rembg" };
            segmentationModel = new TextBox { Text = settings.SegmentationCliModel, PlaceholderText = "birefnet-general" };
            polzaKey = new TextBox { PasswordChar = '●', PlaceholderText = Locale.T("Enter a new key to save or replace it") };
            falKey = new TextBox { PasswordChar = '●', PlaceholderText = Locale.T("Enter a new key to save or replace it") };
            var upscaleCli = CanvasDialogs.Form(("Real-ESRGAN executable", upscalePath));
            var segmentationCli = CanvasDialogs.Form(("rembg executable", segmentationPath), ("Model ID", segmentationModel));
            var upscaleApi = CanvasDialogs.Form(("Polza AI key · Topaz Upscale", polzaKey),
                ("", Ui.TextButton("Forget saved key", () => { polzaKey.Text = ""; forgetPolza = true; })));
            var segmentationApi = CanvasDialogs.Form(("fal API key · BiRefNet", falKey),
                ("", Ui.TextButton("Forget saved key", () => { falKey.Text = ""; forgetFal = true; })));
            void RefreshUtilities()
            {
                upscaleCli.IsVisible = routes[AiTaskKind.Upscale] == ImageGenerationRoute.LocalCodex;
                upscaleApi.IsVisible = routes[AiTaskKind.Upscale] == ImageGenerationRoute.DirectApi;
                segmentationCli.IsVisible = routes[AiTaskKind.ObjectSelection] == ImageGenerationRoute.LocalCodex;
                segmentationApi.IsVisible = routes[AiTaskKind.ObjectSelection] == ImageGenerationRoute.DirectApi;
            }
            var operationRows = DirectImageGenerator.Operations.Select(task =>
            {
                var label = task == AiTaskKind.ObjectSelection ? "Object Selection / Select Subject" : task.DisplayName();
                var choices = task == AiTaskKind.ObjectSelection
                    ? new[] { ImageGenerationRoute.BuiltIn }.Concat(DirectImageGenerator.Connections).ToArray() : DirectImageGenerator.Connections;
                var picker = Ui.Combo(choices, routes[task], DirectImageGenerator.RouteLabel,
                    value =>
                    {
                        routes[task] = value;
                        RefreshUtilities();
                        RouteChanged?.Invoke();
                        if (task is not (AiTaskKind.Upscale or AiTaskKind.ObjectSelection) && DirectImageGenerator.IsDirect(value)
                            && Picker is ComboBox connectionPicker)
                            connectionPicker.SelectedIndex = Array.IndexOf(DirectImageGenerator.Connections, value);
                    }, 188);
                picker.Name = "ImageRoute" + task;
                operationPickers.Add(task, picker);
                Avalonia.Automation.AutomationProperties.SetName(picker, Locale.T(label));
                return (label, (Control)picker);
            }).ToArray();
            OperationPicker = operationRows[0].Item2;
            var nameField = StudioSettingsLayout.Field("Display name", displayName);
            var protocol = new TextBox { IsReadOnly = true };
            var protocolField = StudioSettingsLayout.Field("API protocol", protocol);
            var api = Ui.Column(8, firstRun
                    ? StudioSettingsLayout.Columns("*,*", StudioSettingsLayout.Field("API base URL", url), StudioSettingsLayout.Field("Image API key", key))
                    : Ui.Column(8, StudioSettingsLayout.Field("API base URL", url), StudioSettingsLayout.Field("Image API key", key)),
                forget, catalog.View);
            var serverAddress = new TextBox { Text = server.Text, Name = "ImageComfyServer" };
            serverAddress.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.TextProperty && serverAddress.Text != server.Text) server.Text = serverAddress.Text;
            };
            server.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.TextProperty && serverAddress.Text != server.Text) serverAddress.Text = server.Text;
            };
            var workflow = StudioSettingsLayout.Columns("*,*", StudioSettingsLayout.Field("ComfyUI server", serverAddress));
            Control? methodField = null;
            var check = new ConnectionCheck(lifetime, async token =>
            {
                if (route == ImageGenerationRoute.ComfyWorkflow)
                {
                    await service.TestConnectionAsync(token, server.Text ?? "");
                    return "Connected";
                }
                if (route == ImageGenerationRoute.LocalCodex)
                {
                    await LocalCodex.DiscoverModelsAsync(path.Text ?? "", token);
                    return "Codex CLI is signed in.";
                }
                var count = await catalog.FetchAsync(token);
                return count > 0 ? "The API responded. Models are available below." : "The API reported no models.";
            }, "", buttonText: "Check connection");
            void Refresh()
            {
                check.Reset();
                workflow.IsVisible = route == ImageGenerationRoute.ComfyWorkflow;
                api.IsVisible = nameField.IsVisible = route == ImageGenerationRoute.DirectApi;
                codex.View.IsVisible = route == ImageGenerationRoute.LocalCodex;
                protocolField.IsVisible = route == ImageGenerationRoute.DirectApi;
                try { protocol.Text = DirectImageApi.Parse(url.Text ?? "").Protocol; }
                catch (FormatException) { protocol.Text = ""; }
                if (methodField != null) methodField.IsVisible = firstRun || route != ImageGenerationRoute.DirectApi;
            }
            Picker = Ui.Combo(DirectImageGenerator.Connections, route, DirectImageGenerator.RouteLabel, value =>
            {
                route = value;
                if (firstRun) SetRoute(AiTaskKind.GenerateImage, value);
                Refresh();
            }, double.NaN);
            methodField = StudioSettingsLayout.Field("Connection method", Picker);
            View = Ui.Column(10, firstRun ? StudioSettingsLayout.Columns("*,*", methodField, nameField)
                    : Ui.Column(8, methodField, StudioSettingsLayout.Columns("*,*", nameField, protocolField)),
                workflow, codex.View, check.View, api);
            if (!firstRun)
                View.Children.Add(new Expander { Header = Locale.T("Connection by operation"), HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = Ui.Column(6, CanvasDialogs.Form(operationRows), upscaleCli, upscaleApi, segmentationCli, segmentationApi) });
            catalog.Changed += () => RouteChanged?.Invoke();
            foreach (var input in new[] { model, url, key, path }) input.TextChanged += (_, _) => Refresh();
            server.TextChanged += (_, _) => check.Reset();
            forget.Click += (_, _) => Refresh();
            View.AttachedToVisualTree += (_, _) => Refresh();
            Refresh();
            RefreshUtilities();
        }

        private DirectImageGenerator Draft() => new(route, path.Text?.Trim() ?? "", url.Text?.Trim() ?? "", catalog.LegacySelection);

        internal void Validate(out Control field, Dictionary<string, string?> changes)
        {
            field = url;
            var draft = Draft();
            var hasImageApi = routes.Any(item => item.Value == ImageGenerationRoute.DirectApi &&
                item.Key is not (AiTaskKind.Upscale or AiTaskKind.ObjectSelection));
            if (hasImageApi && draft.ApiAddress.Length > 0 || forgetKey || !string.IsNullOrWhiteSpace(key.Text))
            {
                var endpoint = DirectImageGenerator.Endpoint(draft.ApiAddress);
                CollectKey(UserApiKeys.Target("ImageApi", endpoint), key, forgetKey);
            }
            field = polzaKey;
            CollectKey(DirectImageUtilities.PolzaKeyTarget, polzaKey, forgetPolza);
            field = falKey;
            CollectKey(DirectImageUtilities.FalKeyTarget, falKey, forgetFal);

            void CollectKey(string target, TextBox entry, bool forget)
            {
                var value = entry.Text?.Trim();
                if (!forget && string.IsNullOrEmpty(value)) return;
                value = string.IsNullOrEmpty(value) ? null : value;
                if (changes.TryGetValue(target, out var previous) && previous != value)
                    throw new FormatException("Enter the shared Polza AI key in just one field.");
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
            settings.ImageOperationApiModels = new(modelOverrides);
            settings.ImageApiCatalogs = catalog.Save();
            settings.UpscaleCliPath = upscalePath.Text?.Trim() ?? "";
            settings.SegmentationCliPath = segmentationPath.Text?.Trim() ?? "";
            settings.SegmentationCliModel = segmentationModel.Text?.Trim() ?? "";
        }
    }
}
