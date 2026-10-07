using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Composa.AI;
using Composa.App.AI;
using SkiaSharp;
using Composa.Model;

namespace Composa.App.Dialogs;

public sealed record AiPromptResult(string Prompt, int Width, int Height, long Seed, string? EngineId = null);

public static class AiDialogs
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

    public static async Task<bool> SettingsDialog(Window owner, Settings settings, AiTaskService service)
    {
        var originalEngine = service.SelectedEngine;
        DialogWindow? settingsDialog = null;
        var showContext = new CheckBox { Content = "Show AI context bounds after selection", IsChecked = settings.AiShowContextBounds };
        var taskDrafts = new Dictionary<string, string>(settings.AiTaskEngineIds);
        var url = new TextBox { Text = settings.ComfyServerUrl, Width = 330 };
        var timeout = Ui.Number(settings.ComfyConnectionTimeoutSeconds, 1, 120, _ => { }, 1, "0", 80);
        var keyEnvironment = new TextBox { Text = settings.ComfyApiKeyEnvironment, Width = 330 };
        var sessionKey = new TextBox { PasswordChar = '●', Width = 330, PlaceholderText = "Paste Comfy.org API key · kept for this session only" };
        var clearKey = false;
        var clear = Ui.TextButton("Clear session key", () => { sessionKey.Text = ""; clearKey = true; });
        var keyNote = Ui.Label("Partner Nodes use a Comfy.org API key, not an OpenAI key. Create one at platform.comfy.org. Paste it here for this session, or set the named environment variable. No secret is saved in settings. Browser login is separate. Use HTTPS for servers outside a trusted LAN.", Palette.Secondary);
        keyNote.MaxWidth = 430; keyNote.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var promptDrafts = new Dictionary<string, AiPromptSetting>(settings.AiPackPrompts);
        var promptPack = service.SelectedEngine?.Id ?? "";
        var promptScope = Ui.Label("Workflow: " + service.SelectedEngine?.DisplayName, Palette.Secondary);
        var currentPrompt = settings.PromptFor(promptPack);
        var additionalEnabled = new CheckBox { Content = "Append for this workflow pack", IsChecked = currentPrompt.Enabled };
        var additionalPrompt = new TextBox { Text = currentPrompt.Text, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Width = 430, Height = 115, IsEnabled = currentPrompt.Enabled };
        void SavePromptDraft() { if (promptPack.Length > 0) promptDrafts[promptPack] = new(additionalEnabled.IsChecked == true, additionalPrompt.Text ?? ""); }
        void LoadPromptDraft()
        {
            promptPack = service.SelectedEngine?.Id ?? "";
            promptScope.Text = "Workflow: " + service.SelectedEngine?.DisplayName;
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
                status.Text = "Refresh models to connect to this server.";
                status.Foreground = Palette.Secondary;
                return;
            }
            var compatibility = service.SelectedEngine == null ? null : service.Compatibility(service.SelectedEngine, models.Choices(address));
            var info = service.ServerInfo;
            status.Text = $"Connected{(info?.Version == null ? "" : " · ComfyUI " + info.Version)}" +
                (info?.Devices.Count > 0 ? "\n" + string.Join(", ", info.Devices) : "") +
                (compatibility == null ? "" : "\n" + AiTaskService.CompatibilityMessage(service.SelectedEngine!, compatibility));
            status.Foreground = compatibility is { IsCompatible: false } ? Avalonia.Media.Brushes.Orange : Palette.Accent;
        }
        models.ChoicesChanged += RefreshStatus;
        url.TextChanged += (_, _) => { models.Refresh(); RefreshStatus(); };
        var test = Ui.TextButton("Refresh Models / Test Connection", () => { });
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            status.Text = "Connecting…";
            try
            {
                service.ConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
                await service.TestConnectionAsync(overrideUrl: url.Text);
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
        var engine = Ui.Combo(engineNames, selectedName, name => name, name =>
        {
            SavePromptDraft();
            service.SelectedEngine = service.Engines.Profiles.FirstOrDefault(p => p.DisplayName == name);
            LoadPromptDraft();
            models.Refresh();
            RefreshStatus();
        }, 260);
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
            const string inherit = "Use default workflow";
            var assigned = taskDrafts.GetValueOrDefault(key) ?? settings.EngineForTask(task);
            var missing = assigned != null && packs.All(pack => pack.Id != assigned) ? "Unavailable: " + assigned : null;
            var names = new[] { inherit }.Concat(packs.Select(pack => pack.DisplayName)).Concat(missing == null ? [] : new[] { missing }).ToArray();
            var selected = packs.FirstOrDefault(pack => pack.Id == assigned)?.DisplayName ?? missing ?? inherit;
            taskRows.Add((task.DisplayName(), Ui.Combo(names, selected, name => name, name =>
            {
                if (name == inherit) taskDrafts[key] = "";
                else if (packs.FirstOrDefault(pack => pack.DisplayName == name) is { } chosen) taskDrafts[key] = chosen.Id;
            }, 260)));
        }
        var assignmentNote = Ui.Label("Each task keeps its own choice. Changing Fill does not change generation or Expand.", Palette.Secondary);
        assignmentNote.MaxWidth = 430; assignmentNote.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var assignments = Ui.Column(7, Ui.Label("Workflow by task", weight: Avalonia.Media.FontWeight.SemiBold), CanvasDialogs.Form(taskRows.ToArray()), assignmentNote);
        var note = Ui.Label("Models are read from this ComfyUI server, including shared folders. Generation and mask options are in Advanced on the AI panel.", Palette.Secondary);
        note.MaxWidth = 430;
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        RefreshStatus();
        var auth = CanvasDialogs.Form(("Key environment variable", keyEnvironment), ("Session API key", sessionKey), ("", clear));
        Control Page(params Control[] controls) => new ScrollViewer { Content = Ui.Column(12, controls),
            MaxHeight = Math.Clamp(owner.Bounds.Height - 230, 260, 520), Margin = new Thickness(0, 12, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var body = new TabControl { Width = 590, Name = "AiSettingsTabs", FontSize = 13, ItemsSource = new[]
        {
            new TabItem { Header = "Connection", FontSize = 13, Content = Page(connection, showContext,
                Ui.Label("Connect first, then choose the server's installed models in Workflows & models.", Palette.Secondary)) },
            new TabItem { Header = "Workflows & models", FontSize = 13, Content = Page(CanvasDialogs.Form(("Default workflow", engine)), assignments, models, note) },
            new TabItem { Header = "Prompts", FontSize = 13, Content = Page(Ui.Label("Additional image-editing prompt", weight: Avalonia.Media.FontWeight.SemiBold), promptScope, additionalEnabled,
                additionalPrompt, resetPrompt, Ui.Label("Added to this workflow's image-editing instructions.", Palette.Secondary)) },
            new TabItem { Header = "API access", FontSize = 13, Content = Page(Ui.Label("Paid Comfy.org models", weight: Avalonia.Media.FontWeight.SemiBold), auth, keyNote) }
        } };
        settingsDialog = new DialogWindow("ComfyUI Settings", body, "Save settings"); RefreshStatus();
        if (!await settingsDialog.Ask(owner)) { service.SelectedEngine = originalEngine; return false; }
        _ = ComfyServerAddress.Parse(url.Text ?? "");
        settings.ComfyServerUrl = ComfyServerAddress.Parse(url.Text ?? "").ToString();
        settings.ComfyConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
        settings.ComfyApiKeyEnvironment = keyEnvironment.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(sessionKey.Text)) service.SessionApiKey = sessionKey.Text.Trim();
        else if (clearKey) service.SessionApiKey = null;
        settings.AiEngineId = service.SelectedEngine?.Id;
        settings.AiTaskEngineIds = taskDrafts;
        settings.AiShowContextBounds = showContext.IsChecked == true;
        SavePromptDraft(); settings.AiPackPrompts = promptDrafts;
        models.Save();
        settings.Save();
        return true;
    }

    public static async Task<bool> Advanced(Window owner, Settings settings, AiTaskService? service = null, AiTaskKind task = AiTaskKind.GenerativeFill, AiOperationSettings? initialProfile = null)
    {
        var paid = service?.SelectedEngine?.PaidApi == true;
        var profile = initialProfile ?? settings.OperationFor(service?.SelectedEngine?.Id, task, paid);
        var mp = ClosestMegapixels(profile.Megapixels); var originalSize = profile.OriginalSize;
        var reference = profile.ReferenceMegapixels;
        var grow = profile.MaskGrow; var blend = profile.MaskBlend; var context = profile.MaskContext;
        var blur = profile.MaskBlur; var colorMatch = profile.ColorMatch;
        var fluxMemory = profile.FluxMemory is "reduced" or "standard" ? profile.FluxMemory : "auto";
        var seed = profile.Seed; var mode = profile.VariantMode;
        var gptContext = Math.Clamp(profile.GptContextPadding, 0, PartnerImageInputs.MaximumContextPadding);
        var quality = profile.ApiQuality;
        var model = service?.SelectedEngine?.ApiModel ?? "gpt-image-2.5-sunburst";
        var qualities = PartnerPricing.Choices(service?.ServerCapabilities, model, "quality");
        if (qualities.Count == 0) qualities = ["low", "medium", "high", "xhigh", "max"];
        if (!qualities.Contains(quality)) quality = qualities[0];
        var sizes = new[] { "Original size" }.Concat(AiDimensions.MegapixelOptions.Select(AiDimensions.Label)).ToArray();
        (string, Control)[] fields = [
            ("Image size", Ui.Combo(sizes, originalSize ? sizes[0] : AiDimensions.Label(mp), value => value,
                value => { originalSize = value == sizes[0]; if (!originalSize) mp = AiDimensions.MegapixelOptions.First(option => AiDimensions.Label(option) == value); }, 150)),
            ("Reference images", Ui.Combo(sizes, reference is { } r ? AiDimensions.Label(ClosestMegapixels(r)) : sizes[0], value => value,
                value => reference = value == sizes[0] ? null : AiDimensions.MegapixelOptions.First(option => AiDimensions.Label(option) == value), 150)),
            ("Execution", Ui.Combo(new[] { AiVariantMode.List, AiVariantMode.Batch }, mode,
                value => paid ? value == AiVariantMode.List ? "List · separate API requests" : "Batch · one API request"
                    : value == AiVariantMode.List ? "List · lower VRAM" : "Batch · faster, more VRAM", value => mode = value, 250)),
            ("Memory use", Ui.Combo(new[] { "auto", "reduced", "standard" }, fluxMemory,
                value => value == "auto" ? "Auto" : value == "reduced" ? "Lower VRAM" : "Standard", value => fluxMemory = value, 150)),
            ("Mask grow", Ui.Row(6, Ui.SliderField("", grow, 0, 64, value => grow = (int)value, 1, "0", 150, reset: 0), Ui.Label("px"))),
            ("Mask blend", Ui.Row(6, Ui.SliderField("", blend, 0, 64, value => blend = (int)value, 1, "0", 150, reset: 0), Ui.Label("px"))),
            ("Mask conditioning blur", Ui.Row(6, Ui.SliderField("", blur, 0, 64, value => blur = (int)value, 1, "0", 150, reset: 0), Ui.Label("px"))),
            ("Color match", Ui.Combo(new[] { "off", "subtle", "strong" }, colorMatch, value => value, value => colorMatch = value, 150)),
            ("Mask context", Ui.Row(6, Ui.SliderField("", context, 1, 8, value => context = value, 0.1, "0.0", 150, reset: 1), Ui.Label("× selection bounds"))),
            ("GPT context padding", Ui.Row(6, Ui.SliderField("", gptContext, 0, PartnerImageInputs.MaximumContextPadding, value => gptContext = (int)value, 1, "0", 150, reset: PartnerImageInputs.DefaultContextPadding), Ui.Label("px · each side"))),
            ("Seed", Ui.Row(6, Ui.Number(seed, -1, long.MaxValue, value => seed = (long)value, 1, "0", 150), Ui.Label("-1 = random")))];
        var form = CanvasDialogs.Form(fields.Where(field => field.Item1 != "Memory use" || service?.SelectedEngine?.Id == "flux2-klein-intel-xpu")
            .Where(field => task != AiTaskKind.GenerateImage || !field.Item1.StartsWith("Mask") && field.Item1 is not ("GPT context padding" or "Color match"))
            .Where(field => paid
            ? field.Item1 is not ("Color match" or "Seed" or "Mask conditioning blur" or "Mask context")
            : field.Item1 != "GPT context padding").ToArray());
        var note = Ui.Label(task == AiTaskKind.GenerateImage
            ? "Generate a new image using the prompt and optional references. Image size preserves proportions. List runs variants separately; Batch groups them. Paid variants are billed individually."
            : paid
            ? "GPT receives an image crop plus context padding and your references, not a mask image. Composa places the result back and applies the soft edit mask once. Padding controls what GPT sees; Mask blend controls the local edge, independently. Mask grow is ignored for Fill. Remove/Expand keep their black repair areas. Each variant is billed; Ctrl+Z undoes edits, not charges."
            : "List runs one variant at a time; Batch needs more VRAM. Mask blend controls the editable layer mask, conditioning blur the sampling mask. FLUX returns unmasked generated context pixels at their original coordinates. Color match uses unchanged surroundings; turn it off for intentional color changes. Use LoRAs compatible with the selected model. Ctrl+Z undoes the generation.", Palette.Secondary);
        note.MaxWidth = 470; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        if (service?.SelectedEngine?.Id == "flux2-klein-intel-xpu")
            note.Text += " Auto uses Lower VRAM on Intel: the text encoder runs on CPU and VAE works in tiles. This may take longer; Standard keeps normal server placement.";
        var extra = new StackPanel { Spacing = 9 };
        var lorasEnabled = profile.LorasEnabled;
        var loras = profile.Loras.Take(3).ToList(); while (loras.Count < 3) loras.Add(new());
        if (paid)
            extra.Children.Add(CanvasDialogs.Form(("GPT quality", Ui.Combo(qualities, qualities.Contains(quality) ? quality : "low", value => value, value => quality = value, 150))));
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
        var body = new ScrollViewer { Content = Ui.Column(12, form, extra, note), MaxHeight = Math.Clamp(owner.Bounds.Height - 160, 300, 650), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (!await new DialogWindow($"{task.DisplayName()} · Advanced", body).Ask(owner)) return false;
        settings.SetOperation(service?.SelectedEngine?.Id, task, profile with
        {
            Megapixels = mp, OriginalSize = originalSize, ReferenceMegapixels = reference, FluxMemory = fluxMemory,
            MaskGrow = grow, MaskBlend = blend, MaskContext = context, MaskBlur = blur, ColorMatch = colorMatch,
            GptContextPadding = gptContext, Seed = seed, VariantMode = mode, ApiQuality = quality,
            LorasEnabled = lorasEnabled, Loras = !paid && service?.SelectedEngine?.Lora.Supported == true
                ? loras.Where(lora => lora.Name.Length > 0).ToList() : profile.Loras
        });
        settings.Save();
        return true;
    }

    public static async Task<string?> ApiKey(Window owner, CancellationToken cancellationToken)
    {
        var key = new TextBox { Width = 390, PasswordChar = '●', PlaceholderText = "Comfy.org API key", Name = "ComfyApiKeyEntry" };
        var note = Ui.Label("This workflow needs a Comfy.org API key. It will be kept for this program session only, not saved to disk. An OpenAI key cannot be used here.", Palette.Secondary);
        note.MaxWidth = 390; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var dialog = new DialogWindow("Comfy.org API key", Ui.Column(10, note, key), "Continue");
        dialog.CanAccept = false;
        key.TextChanged += (_, _) => dialog.CanAccept = !string.IsNullOrWhiteSpace(key.Text);
        dialog.Opened += (_, _) => key.Focus();
        using var registration = cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
        if (!await dialog.Ask(owner)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return key.Text?.Trim();
    }

    public static async Task<bool> Upscale(Window owner, Settings settings, int width, int height, bool selection, EngineProfile? engine = null)
    {
        var profile = settings.OperationFor(engine?.Id, AiTaskKind.Upscale, engine?.PaidApi == true);
        var factor = profile.UpscaleFactor == 4 ? 4 : 2;
        var dimensions = Ui.Label("", Palette.Secondary);
        void Refresh() => dimensions.Text = $"{width * (long)factor} × {height * (long)factor} px" + (selection ? " · fitted back into your selection" : " · resizes the canvas");
        var scale = Ui.Combo(new[] { 2, 4 }, factor, value => "×" + value, value => { factor = value; Refresh(); }, 100);
        Refresh();
        var note = Ui.Label("Inference is tiled. A ×4 model still computes its native scale for a ×2 result; the input is not reduced, so source details are preserved. For lowest memory use a native ×2 model in ComfyUI Settings.", Palette.Secondary);
        note.MaxWidth = 450; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        if (!await new DialogWindow("AI Upscale", Ui.Column(12, CanvasDialogs.Form(("Scale", scale)), dimensions, note), "Upscale").Ask(owner)) return false;
        settings.SetOperation(engine?.Id, AiTaskKind.Upscale, profile with { UpscaleFactor = factor }); settings.Save(); return true;
    }

    public static async Task<AiPromptResult?> Prompt(Window owner, AiTaskKind task, Settings settings, int documentWidth, int documentHeight, string initialPrompt = "", AiTaskService? service = null, int referenceCount = 0, bool hasSelection = false, AiReferenceEditor? references = null,
        SKRectI? sourceCanvas = null, SKRectI? selectionBounds = null)
    {
        var previousEngine = service?.SelectedEngine;
        if (service != null) service.SelectedEngine = service.EngineFor(task);
        var profile = settings.OperationFor(service?.SelectedEngine?.Id, task, service?.SelectedEngine?.PaidApi == true);
        var profileDrafts = new Dictionary<string, AiOperationSettings>();
        var prompt = new TextBox { Text = initialPrompt, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Width = 430, Height = 100, PlaceholderText = "Describe the result" };
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
        var qualityHost = new StackPanel();
        var qualityOptions = new[] { "low", "medium", "high", "xhigh", "max" };
        var qualityCombo = Ui.Combo(qualityOptions, qualityOptions.Contains(quality) ? quality : "low", value => value,
            value => { quality = value; Refresh(); }, 150);
        qualityHost.Children.Add(CanvasDialogs.Form(("GPT quality", qualityCombo)));
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
        var variantsCombo = Ui.Combo(new[] { 1, 2, 3 }, variants, value => value.ToString(), value => { variants = value; Refresh(); }, 60);
        AiOperationSettings CaptureProfile() => profile with
        {
            OriginalSize = originalSize, Megapixels = mp, Variants = variants, ApiQuality = quality,
            ExpansionMode = task == AiTaskKind.GenerativeExpand ? expansionMode : profile.ExpansionMode,
            ExpansionMinimumSide = regionSide, WholeExpansionMinimumSide = wholeSide
        };
        void LoadProfile(AiOperationSettings next)
        {
            profile = next; originalSize = next.OriginalSize; mp = ClosestMegapixels(next.Megapixels);
            variants = Math.Clamp(next.Variants, 1, 3); quality = next.ApiQuality;
            expansionMode = hasSelection ? AiExpansionMode.MaskedRegion : next.ExpansionMode;
            regionSide = next.ExpansionMinimumSide; wholeSide = next.WholeExpansionMinimumSide;
            qualityCombo.SelectedItem = qualityOptions.Contains(quality) ? quality : "low";
            size.SelectedItem = originalSize ? sizeOptions[0] : AiDimensions.Label(mp);
            variantsCombo.SelectedItem = variants.ToString(); mode.SelectedIndex = expansionMode == AiExpansionMode.WholeImage ? 1 : 0;
            RefreshExpandChoices(); Refresh();
        }
        var advanced = Ui.TextButton("Advanced…", () => { });
        advanced.Click += async (_, _) =>
        {
            if (!await Advanced(dialog!, settings, service, task, CaptureProfile())) return;
            LoadProfile(settings.OperationFor(service?.SelectedEngine?.Id, task, service?.SelectedEngine?.PaidApi == true));
        };
        var modelHost = new StackPanel();
        if (service != null && service.Engines.Profiles.Where(pack => pack.Binding(task) != null).ToArray() is { Length: > 0 } packs)
        {
            var picker = Ui.Combo(packs, service.SelectedEngine ?? packs[0], pack => pack.DisplayName, pack =>
            {
                profileDrafts[service.SelectedEngine?.Id ?? "default"] = CaptureProfile();
                service.SelectedEngine = pack;
                LoadProfile(profileDrafts.GetValueOrDefault(pack.Id) ?? settings.OperationFor(pack.Id, task, pack.PaidApi));
            }, 210);
            modelHost.Children.Add(Ui.Row(12, picker, advanced));
        }
        else modelHost.Children.Add(advanced);
        var resolution = task == AiTaskKind.GenerativeExpand
            ? hasSelection ? CanvasDialogs.Form(("Generation size", expandSize)) : CanvasDialogs.Form(("Expand mode", mode), ("Generation size", expandSize))
            : CanvasDialogs.Form(("Image size", size));
        void Refresh()
        {
            var paid = service?.SelectedEngine?.PaidApi == true; qualityHost.IsVisible = paid;
            try
            {
                (width, height) = task == AiTaskKind.GenerativeExpand
                    ? AiDimensions.FromMaximumSide(expansionMode == AiExpansionMode.WholeImage ? wholeSide : regionSide, documentWidth, documentHeight)
                    : originalSize ? (documentWidth, documentHeight) : AiDimensions.FromMegapixels(mp, documentWidth, documentHeight);
                var api = paid ? PartnerImageSize.Plan(width, height) : (width, height);
                dimensions.Text = $"{width} × {height} px · proportions preserved" + (paid && api != (width, height) ? $"\nGPT request: {api.Item1} × {api.Item2}; uniform fitting, no stretching." : "");
                if (!paid && service?.SelectedEngine?.Id == "flux2-klein-intel-xpu" && sourceCanvas is { } canvas && selectionBounds is { } selection
                    && task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.Harmonize or AiTaskKind.Relight)
                {
                    var contextBounds = AiContextGeometry.Flux(selection,canvas,profile.MaskGrow,profile.MaskBlend,profile.MaskContext,profile.MaskBlur);
                    var generated = AiContextGeometry.FluxSize(contextBounds,selection,width,height,originalSize).Padded;
                    dimensions.Text = $"FLUX request including context: {generated.Width} × {generated.Height} px · {generated.Width * (double)generated.Height / 1_000_000:0.##} MP";
                }
                if (dialog != null) dialog.CanAccept = true;
            }
            catch (Exception error) { dimensions.Text = error.Message; if (dialog != null) dialog.CanAccept = false; }
            var sourceCount = task == AiTaskKind.GenerateImage ? 0 : 1;
            var count = sourceCount + (references?.Count ?? referenceCount);
            cost.Text = paid ? PartnerPricing.Estimate(service?.ServerCapabilities, service!.SelectedEngine!.ApiModel!, quality, "Custom", count, variants)?.Label ?? "Paid API · estimate unavailable" : "Local generation · no Comfy credits";
            note.Text = paid ? "GPT: 1:3–3:1, up to 3840 px / 8.29 MP. Quality affects detail, time and price. Undo does not refund credits."
                : task == AiTaskKind.GenerativeExpand ? hasSelection
                    ? "Fill only the selection with surrounding context. Existing canvas size is preserved; the expansion instruction is automatic."
                    : "Empty-area mode sends a soft mask plus context and preserves existing pixels. Whole-image mode may redraw everything. The generated patch is fitted back without changing the requested canvas size."
                : hasSelection ? "Original size keeps source pixels. For FLUX, MP sets the total request area including surrounding context. Seed and execution mode are in Advanced."
                    : "Original size follows the canvas; MP scales its area while keeping proportions. Seed and execution mode are in Advanced.";
            ToolTip.SetTip(note, paid ? "Explicit Custom dimensions, multiples of 16; never Auto or aspect stretching. This image node has no separate reasoning/effort setting." : null);
        }
        prompt.IsVisible = task != AiTaskKind.GenerativeExpand;
        var promptSection = Section("Describe the result", prompt); promptSection.IsVisible = prompt.IsVisible;
        var content = Ui.Column(10, Section("Workflow", modelHost), promptSection);
        if (references != null) content.Children.Add(Section("Optional references", references.View));
        content.Children.Add(Section("Output", resolution, dimensions, qualityHost, cost));
        content.Children.Add(note);
        var body = new ScrollViewer { Content = content, MaxHeight = Math.Clamp(owner.Bounds.Height - 170, 300, 650), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        dialog = new DialogWindow(task.DisplayName(), body, "Generate"); dialog.UseGenerationVariants(variantsCombo);
        if (references != null)
        {
            references.Owner = dialog;
            references.Changed += Refresh;
            dialog.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
            {
                if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                { e.Handled = true; await references.Paste(prompt.IsVisible ? prompt : null); }
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
        if (!accepted) return null;
        settings.SetOperation(chosenEngine?.Id, task, CaptureProfile());
        if (chosenEngine != null) settings.AiTaskEngineIds[task.ToString()] = chosenEngine.Id;
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
