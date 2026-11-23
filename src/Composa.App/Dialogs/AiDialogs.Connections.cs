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

    private sealed class ConnectionSummary
    {
        private readonly TextBlock source = Value();
        private readonly TextBlock model = Value();
        private readonly TextBlock auth = Value();
        internal Border View { get; }

        internal ConnectionSummary()
        {
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("1.1*,1*,1.2*"), ColumnSpacing = 8 };
            var fields = new (string Caption, TextBlock Value)[] { ("Request sent by", source), ("Model", model), ("Authentication", auth) };
            for (var i = 0; i < fields.Length; i++)
            {
                var column = Ui.Column(2, Ui.Label(Locale.T(fields[i].Caption), Palette.Secondary, size: 11), fields[i].Value);
                Grid.SetColumn(column, i);
                columns.Children.Add(column);
            }
            View = new Border { Child = columns, Background = Palette.Panel, Padding = new Thickness(9), CornerRadius = new CornerRadius(5) };
        }

        internal void Update(string location, string? modelId, string authentication)
        {
            source.Text = Locale.T(location);
            model.Text = string.IsNullOrWhiteSpace(modelId) ? Locale.T("Choose a model") : modelId;
            auth.Text = Locale.T(authentication);
        }

        private static TextBlock Value() => new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
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
            bool showHelperWarning = false)
        {
            this.hint = hint;
            status = ConnectionNote(hint);
            var test = Ui.TextButton(showHelperWarning ? "Test prompt" : "Test connection", () => { });
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
        private readonly TextBox path, url, model, key, displayName;
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
            var field = Ui.Combo(choices, routes[task], DirectImageGenerator.RouteLabel,
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
            if (Picker is ComboBox combo && routes[task] != ImageGenerationRoute.BuiltIn)
                combo.SelectedIndex = Array.IndexOf(DirectImageGenerator.Connections, routes[task]);
        }

        internal DirectImageGenerator ModelFor(AiTaskKind task) =>
            Draft() with { Route = routes[task], Task = task, ApiModel = modelOverrides.GetValueOrDefault(task) ?? Draft().ApiModel };

        internal IReadOnlyList<EngineProfile> PromptProfiles()
        {
            var configured = Draft();
            var direct = catalog.Models.Select(item => item.Id).Concat(modelOverrides.Values).Append(configured.ApiModel)
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)
                .Select(id => (configured with { Route = ImageGenerationRoute.DirectApi, ApiModel = id }).PromptProfile());
            return direct.Prepend((configured with { Route = ImageGenerationRoute.LocalCodex }).PromptProfile()).ToArray();
        }

        internal Control ApiModelField(AiTaskKind task)
        {
            var ids = catalog.Models;
            var current = modelOverrides.GetValueOrDefault(task) ?? "";
            var options = ids.Select(value => value.Id).Append(current).Prepend("").Distinct().ToArray();
            return Ui.Combo(options, current, id => id.Length == 0 ? Locale.T("Use connection’s default model")
                : ids.FirstOrDefault(item => item.Id == id).Label ?? id, id =>
            {
                if (id.Length == 0) modelOverrides.Remove(task);
                else modelOverrides[task] = id;
                RouteChanged?.Invoke();
            }, double.NaN);
        }

        internal void ResetModel(AiTaskKind task) => modelOverrides.Remove(task);

        internal ImageConnectionFields(Settings settings, AiTaskService service, Func<string> server, CancellationToken lifetime, bool firstRun)
        {
            routes = DirectImageGenerator.Operations.ToDictionary(task => task, settings.ImageRouteFor);
            modelOverrides = new(settings.ImageOperationApiModels);
            route = routes[AiTaskKind.GenerateImage];
            var codex = new CodexConnectionFields(settings.ImageGenerationCodexPath, "gpt-image-2", showModel: false);
            path = codex.Path;
            url = new TextBox { Text = settings.ImageGenerationApiUrl, Name = "ImageGenerationApiUrl" };
            model = new TextBox { Text = settings.ImageGenerationApiModel, Name = "ImageGenerationApiModel" };
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
            var api = Ui.Column(8, StudioSettingsLayout.Columns("*,*", StudioSettingsLayout.Field("Display name", displayName),
                StudioSettingsLayout.Field("Default model ID", model)),
                StudioSettingsLayout.Columns("*,*", StudioSettingsLayout.Field("API base URL", url), StudioSettingsLayout.Field("Image API key", key)),
                forget, catalog.View);
            var serverLabel = ConnectionNote("");
            var workflow = CanvasDialogs.Form(("ComfyUI server", serverLabel));
            var summary = new ConnectionSummary();
            var operations = Ui.Column(6, Ui.Label(Locale.T("Operations for the selected model"), weight: FontWeight.SemiBold),
                CanvasDialogs.Form(("Text-to-image", ConnectionNote("Available when configured")),
                    ("Edit with a reference", ConnectionNote("Requires a model that accepts input images")),
                    ("Edit through a mask", ConnectionNote("Uses the selection and surrounding context")),
                    ("Expand canvas", ConnectionNote("Uses the expanded canvas and surrounding context"))));
            var workflowOperations = ConnectionNote("Available operations depend on the selected workflow.");
            var check = new ConnectionCheck(lifetime, async token =>
            {
                if (route == ImageGenerationRoute.ComfyWorkflow)
                {
                    await service.TestConnectionAsync(token, server());
                    return "Connected";
                }
                var draftKey = !string.IsNullOrWhiteSpace(key.Text) ? key.Text.Trim()
                    : forgetKey ? Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY") ?? "" : null;
                using var image = await Draft().GenerateAsync("Product photograph of a cobalt-blue ceramic cup on a pale gray background.",
                    1024, 1024, token, draftKey);
                return $"Image received ({image.Width} × {image.Height} px).";
            }, "The test generates one image using the selected connection.");
            void Refresh()
            {
                check.Reset();
                workflow.IsVisible = workflowOperations.IsVisible = route == ImageGenerationRoute.ComfyWorkflow;
                operations.IsVisible = !workflow.IsVisible;
                api.IsVisible = route == ImageGenerationRoute.DirectApi;
                codex.View.IsVisible = route == ImageGenerationRoute.LocalCodex;
                serverLabel.Text = server();
                summary.Update(workflow.IsVisible ? "ComfyUI server" : "This computer",
                    workflow.IsVisible ? Locale.T("Selected workflow") : Draft().Model,
                    workflow.IsVisible ? "ComfyUI connection settings" : api.IsVisible ? "Image API key" : "Local Codex sign-in");
            }
            Picker = Ui.Combo(DirectImageGenerator.Connections, route, DirectImageGenerator.RouteLabel, value =>
            {
                route = value;
                if (firstRun) SetRoute(AiTaskKind.GenerateImage, value);
                Refresh();
            }, double.NaN);
            View = Ui.Column(10, Ui.Label(Locale.T("Connection by operation"), weight: FontWeight.SemiBold),
                CanvasDialogs.Form(operationRows), upscaleCli, upscaleApi, segmentationCli, segmentationApi,
                Ui.Separator(), Ui.Column(5, ConnectionNote("Connection to configure"), Picker),
                summary.View, workflow, codex.View, api, check.View, operations, workflowOperations);
            if (firstRun)
            {
                for (var index = 0; index < 7; index++) View.Children[index].IsVisible = false;
            }
            foreach (var input in new[] { model, url, key, path }) input.TextChanged += (_, _) => Refresh();
            forget.Click += (_, _) => Refresh();
            View.AttachedToVisualTree += (_, _) => Refresh();
            Refresh();
            RefreshUtilities();
        }

        private DirectImageGenerator Draft() => new(route, path.Text?.Trim() ?? "", url.Text?.Trim() ?? "", model.Text?.Trim() ?? "");

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
