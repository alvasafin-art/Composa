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
        const string repository = "https://github.com/alvasafin-art/ComfyUI-AVS-Prompt-Helper";
        var open = Ui.TextButton("Open the node on GitHub", () => { });
        var status = ConnectionNote("");
        open.Click += (_, _) =>
        {
            try { using var process = Process.Start(new ProcessStartInfo(repository) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { status.Text = repository; }
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
                    status.Text = "";
                }
            };
            var cli = Ui.Label("Codex CLI");
            View = Ui.Column(6, CanvasDialogs.Form(("CLI", cli)), CanvasDialogs.Form(("Text model", Model)),
                CanvasDialogs.Form(("Executable", Path), ("", Ui.Row(5, find, choose)), ("", status)));
            View.Children[1].IsVisible = showModel;
        }
    }

    private sealed class ConnectionCheck
    {
        private readonly string hint;
        private readonly TextBlock status;
        private CancellationTokenSource? current;
        private int revision;
        internal StackPanel View { get; }

        internal ConnectionCheck(CancellationToken lifetime, Func<CancellationToken, Task<string>> check, string hint)
        {
            this.hint = hint;
            status = ConnectionNote(hint);
            var test = Ui.TextButton("Test connection", () => { });
            var cancel = Ui.TextButton("Cancel", () => current?.Cancel());
            cancel.IsVisible = false;
            View = Ui.Column(5, Ui.Row(5, test, cancel), status);
            test.Click += async (_, _) =>
            {
                if (current != null) return;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                current = request;
                var expected = revision;
                test.IsEnabled = false;
                cancel.IsVisible = true;
                status.Text = Locale.T("Testing the selected model…");
                try
                {
                    var message = await check(request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    if (expected == revision) status.Text = Locale.Message(message);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                { if (expected == revision && !lifetime.IsCancellationRequested) status.Text = Locale.T("Test cancelled."); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { if (expected == revision && !request.IsCancellationRequested) status.Text = Locale.Message(error.Message); }
                finally
                {
                    current = null;
                    test.IsEnabled = true;
                    cancel.IsVisible = false;
                }
            };
        }

        internal void Reset()
        {
            revision++;
            current?.Cancel();
            status.Text = Locale.T(hint);
        }
    }

    private sealed class ImageConnectionFields
    {
        private ImageGenerationRoute route;
        private readonly TextBox path, url, model, key;
        private readonly TextBox upscalePath, segmentationPath, segmentationModel, polzaKey, falKey;
        private readonly Dictionary<AiTaskKind, ImageGenerationRoute> routes;
        private bool forgetKey;
        private bool forgetPolza, forgetFal;
        internal StackPanel View { get; }
        internal Control Picker { get; }
        internal Control OperationPicker { get; }
        internal Control ApiUrl => url;

        internal ImageConnectionFields(Settings settings, AiTaskService service, Func<string> server, CancellationToken lifetime)
        {
            routes = DirectImageGenerator.Operations.ToDictionary(task => task, settings.ImageRouteFor);
            route = routes[AiTaskKind.GenerateImage];
            var codex = new CodexConnectionFields(settings.ImageGenerationCodexPath, "gpt-image-2", showModel: false);
            path = codex.Path;
            url = new TextBox { Text = settings.ImageGenerationApiUrl, Name = "ImageGenerationApiUrl" };
            model = new TextBox { Text = settings.ImageGenerationApiModel, Name = "ImageGenerationApiModel" };
            key = new TextBox { PasswordChar = '●', PlaceholderText = Locale.T("Enter a new key to save or replace it") };
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
                        if (task is not (AiTaskKind.Upscale or AiTaskKind.ObjectSelection) && DirectImageGenerator.IsDirect(value)
                            && Picker is ComboBox connectionPicker)
                            connectionPicker.SelectedIndex = Array.IndexOf(DirectImageGenerator.Connections, value);
                    }, 188);
                picker.Name = "ImageRoute" + task;
                Avalonia.Automation.AutomationProperties.SetName(picker, Locale.T(label));
                return (label, (Control)picker);
            }).ToArray();
            OperationPicker = operationRows[0].Item2;
            var api = CanvasDialogs.Form(("API base URL", url), ("Model ID", model), ("Image API key", key), ("", forget),
                ("", ConnectionNote("Enter the provider's base URL and the exact model ID from its catalog.")));
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
            Picker = Ui.Combo(DirectImageGenerator.Connections, route, DirectImageGenerator.RouteLabel, value => { route = value; Refresh(); }, double.NaN);
            View = Ui.Column(10, Ui.Label(Locale.T("Connection by operation"), weight: FontWeight.SemiBold),
                CanvasDialogs.Form(operationRows), upscaleCli, upscaleApi, segmentationCli, segmentationApi,
                Ui.Separator(), Ui.Column(5, ConnectionNote("Connection to configure"), Picker),
                summary.View, workflow, codex.View, api, check.View, operations, workflowOperations);
            model.TextChanged += (_, _) => Refresh();
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
            settings.UpscaleCliPath = upscalePath.Text?.Trim() ?? "";
            settings.SegmentationCliPath = segmentationPath.Text?.Trim() ?? "";
            settings.SegmentationCliModel = segmentationModel.Text?.Trim() ?? "";
        }
    }
}
