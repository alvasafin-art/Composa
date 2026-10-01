using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Composa.AI;
using Composa.App.AI;
using Composa.Model;

namespace Composa.App.Dialogs;

public sealed record AiPromptResult(string Prompt, int Width, int Height, long Seed);

public static class AiDialogs
{
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
        var currentPrompt = settings.PromptFor(promptPack);
        var additionalEnabled = new CheckBox { Content = "Append for this workflow pack", IsChecked = currentPrompt.Enabled };
        var additionalPrompt = new TextBox { Text = currentPrompt.Text, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Width = 430, Height = 115, IsEnabled = currentPrompt.Enabled };
        void SavePromptDraft() { if (promptPack.Length > 0) promptDrafts[promptPack] = new(additionalEnabled.IsChecked == true, additionalPrompt.Text ?? ""); }
        void LoadPromptDraft()
        {
            promptPack = service.SelectedEngine?.Id ?? "";
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
            catch (FormatException) { status.Text = "Enter a complete ComfyUI URL."; return; }
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
            ("Status", status),
            ("Workflow pack", engine));
        var note = Ui.Label("Models are read from this ComfyUI server, including shared folders. Generation and mask options are in Advanced on the AI panel.", Palette.Secondary);
        note.MaxWidth = 430;
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        RefreshStatus();
        var auth = CanvasDialogs.Form(("Key environment variable", keyEnvironment), ("Session API key", sessionKey), ("", clear));
        var body = new ScrollViewer { Content = Ui.Column(12, connection, models, note, Ui.Label("Paid Partner Nodes", weight: Avalonia.Media.FontWeight.SemiBold), auth, keyNote, Ui.Label("Additional image-editing prompt"), additionalEnabled, additionalPrompt, resetPrompt,
            Ui.Label("Sent as text to the image model, not as a separate chat system role.", Palette.Secondary)), MaxHeight = Math.Clamp(owner.Bounds.Height - 140, 300, 620),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (!await new DialogWindow("ComfyUI Settings", body).Ask(owner)) { service.SelectedEngine = originalEngine; return false; }
        _ = ComfyServerAddress.Parse(url.Text ?? "");
        settings.ComfyServerUrl = ComfyServerAddress.Parse(url.Text ?? "").ToString();
        settings.ComfyConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
        settings.ComfyApiKeyEnvironment = keyEnvironment.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(sessionKey.Text)) service.SessionApiKey = sessionKey.Text.Trim();
        else if (clearKey) service.SessionApiKey = null;
        settings.AiEngineId = service.SelectedEngine?.Id;
        SavePromptDraft(); settings.AiPackPrompts = promptDrafts;
        models.Save();
        settings.Save();
        return true;
    }

    public static async Task<bool> Advanced(Window owner, Settings settings, AiTaskService? service = null)
    {
        var mp = ClosestMegapixels(settings.AiMegapixels);
        var originalSize = settings.AiOriginalSize;
        var reference = settings.AiReferenceMegapixels;
        var grow = settings.AiMaskGrow; var blend = settings.AiMaskBlend; var context = settings.AiMaskContext;
        var blur = settings.AiMaskBlur; var colorMatch = settings.AiColorMatch;
        var seed = settings.AiSeed; var mode = settings.AiVariantMode;
        var paid = service?.SelectedEngine?.PaidApi == true;
        var gptContext = Math.Clamp(settings.AiGptContextPadding, 0, PartnerImageInputs.MaximumContextPadding);
        var quality = settings.AiApiQuality;
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
            ("Mask grow", Ui.Row(6, Ui.SliderField("", grow, 0, 64, value => grow = (int)value, 1, "0", 150, reset: 0), Ui.Label("px"))),
            ("Mask blend", Ui.Row(6, Ui.SliderField("", blend, 0, 64, value => blend = (int)value, 1, "0", 150, reset: 0), Ui.Label("px"))),
            ("Mask conditioning blur", Ui.Row(6, Ui.SliderField("", blur, 0, 64, value => blur = (int)value, 1, "0", 150, reset: 0), Ui.Label("px"))),
            ("Color match", Ui.Combo(new[] { "off", "subtle", "strong" }, colorMatch, value => value, value => colorMatch = value, 150)),
            ("Mask context", Ui.Row(6, Ui.SliderField("", context, 1, 8, value => context = value, 0.1, "0.0", 150, reset: 1), Ui.Label("× selection bounds"))),
            ("GPT context padding", Ui.Row(6, Ui.SliderField("", gptContext, 0, PartnerImageInputs.MaximumContextPadding, value => gptContext = (int)value, 1, "0", 150, reset: PartnerImageInputs.DefaultContextPadding), Ui.Label("px · each side"))),
            ("Seed", Ui.Row(6, Ui.Number(seed, -1, long.MaxValue, value => seed = (long)value, 1, "0", 150), Ui.Label("-1 = random")))];
        var form = CanvasDialogs.Form(fields.Where(field => paid
            ? field.Item1 is not ("Color match" or "Seed" or "Mask conditioning blur" or "Mask context")
            : field.Item1 != "GPT context padding").ToArray());
        var note = Ui.Label(paid
            ? "GPT receives an image crop plus context padding and your references, not a mask image. Composa places the result back and applies the soft edit mask once. Padding controls what GPT sees; Mask blend controls the local edge, independently. Mask grow is ignored for Fill. Remove/Expand keep their black repair areas. Each variant is billed; Ctrl+Z undoes edits, not charges."
            : "List runs one variant at a time; Batch needs more VRAM. Mask blend controls the final seam, conditioning blur the sampling mask. Pixaroma nodes are used automatically when installed. Color match uses unchanged surroundings; turn it off for intentional color changes. Use LoRAs compatible with the selected model. Ctrl+Z undoes the generation.", Palette.Secondary);
        note.MaxWidth = 470; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var extra = new StackPanel { Spacing = 9 };
        var lorasEnabled = settings.AiLorasEnabled;
        var loras = settings.AiLoras.Take(3).ToList(); while (loras.Count < 3) loras.Add(new());
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
                var type = Ui.TextButton("123", strength.BeginEdit); type.MinWidth = 0; type.Padding = new Thickness(5, 0); type.Height = 26;
                ToolTip.SetTip(type, "Type an exact LoRA strength · 0 to 3");
                rows.Children.Add(Ui.Column(3, Ui.Label($"LoRA {i + 1}", Palette.Secondary), Ui.Row(7, toggle, combo, strength, type)));
            }
            extra.Children.Add(rows);
            if (names.Count == 0) extra.Children.Add(Ui.Label("Connect / refresh models to read the server's LoRA list.", Palette.Secondary));
        }
        var body = new ScrollViewer { Content = Ui.Column(12, form, extra, note), MaxHeight = Math.Clamp(owner.Bounds.Height - 160, 300, 650), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (!await new DialogWindow("AI · Advanced", body).Ask(owner)) return false;
        settings.AiMegapixels = mp; settings.AiOriginalSize = originalSize; settings.AiReferenceMegapixels = reference;
        settings.AiMaskGrow = grow; settings.AiMaskBlend = blend; settings.AiMaskContext = context;
        settings.AiMaskBlur = blur; settings.AiColorMatch = colorMatch;
        if (paid) settings.AiGptContextPadding = gptContext;
        settings.AiSeed = seed; settings.AiVariantMode = mode;
        settings.AiApiQuality = quality;
        if (!paid && service?.SelectedEngine?.Lora.Supported == true) { settings.AiLorasEnabled = lorasEnabled; settings.AiLoras = loras.Where(lora => lora.Name.Length > 0).ToList(); }
        settings.Save();
        return true;
    }

    public static async Task<bool> Upscale(Window owner, Settings settings, int width, int height, bool selection)
    {
        var factor = settings.AiUpscaleFactor == 4 ? 4 : 2;
        var dimensions = Ui.Label("", Palette.Secondary);
        void Refresh() => dimensions.Text = $"{width * (long)factor} × {height * (long)factor} px" + (selection ? " · fitted back into your selection" : " · resizes the canvas");
        var scale = Ui.Combo(new[] { 2, 4 }, factor, value => "×" + value, value => { factor = value; Refresh(); }, 100);
        Refresh();
        var note = Ui.Label("Inference is tiled. A ×4 model still computes its native scale for a ×2 result; the input is not reduced, so source details are preserved. For lowest memory use a native ×2 model in ComfyUI Settings.", Palette.Secondary);
        note.MaxWidth = 450; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        if (!await new DialogWindow("AI Upscale", Ui.Column(12, CanvasDialogs.Form(("Scale", scale)), dimensions, note), "Upscale").Ask(owner)) return false;
        settings.AiUpscaleFactor = factor; settings.Save(); return true;
    }

    public static async Task<AiPromptResult?> Prompt(Window owner, AiTaskKind task, Settings settings, int documentWidth, int documentHeight, string initialPrompt = "", AiTaskService? service = null, int referenceCount = 0, bool hasSelection = false)
    {
        var previousEngine = service?.SelectedEngine;
        var prompt = new TextBox { Text = initialPrompt, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Width = 430, Height = 100, PlaceholderText = "Describe the result" };
        var originalSize = settings.AiOriginalSize; var mp = ClosestMegapixels(settings.AiMegapixels);
        var variants = Math.Clamp(settings.AiVariants, 1, 3); var quality = settings.AiApiQuality;
        var expansionMode = AiExpansionMode.MaskedRegion;
        var regionSide = settings.AiExpansionMinimumSide; var wholeSide = settings.AiWholeExpansionMinimumSide;
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
            expandSize.ItemsSource = choices.Select(value => value == 0 ? "Original expanded size" : value + " px · minimum side").ToArray();
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
        var advanced = Ui.TextButton("Advanced…", () => { });
        advanced.Click += async (_, _) =>
        {
            if (!await Advanced(dialog!, settings, service)) return;
            quality = settings.AiApiQuality; qualityCombo.SelectedIndex = Array.IndexOf(qualityOptions, quality);
            originalSize = settings.AiOriginalSize; mp = ClosestMegapixels(settings.AiMegapixels);
            size.SelectedItem = originalSize ? sizeOptions[0] : AiDimensions.Label(mp);
            Refresh();
        };
        var modelHost = new StackPanel();
        if (service != null && service.Engines.Profiles.Where(pack => pack.Binding(task) != null).ToArray() is { Length: > 0 } packs)
        {
            var picker = Ui.Combo(packs, service.SelectedEngine ?? packs[0], pack => pack.DisplayName, pack => { service.SelectedEngine = pack; Refresh(); }, 210);
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
                    ? AiDimensions.FromMinimumSide(expansionMode == AiExpansionMode.WholeImage ? wholeSide : regionSide, documentWidth, documentHeight)
                    : originalSize ? (documentWidth, documentHeight) : AiDimensions.FromMegapixels(mp, documentWidth, documentHeight);
                var api = paid ? PartnerImageSize.Plan(width, height) : (width, height);
                dimensions.Text = $"{width} × {height} px · proportions preserved" + (paid && api != (width, height) ? $"\nGPT request: {api.Item1} × {api.Item2}; uniform fitting, no stretching." : "");
                if (dialog != null) dialog.CanAccept = true;
            }
            catch (Exception error) { dimensions.Text = error.Message; if (dialog != null) dialog.CanAccept = false; }
            var sourceCount = task == AiTaskKind.GenerateImage ? 0 : 1;
            var count = sourceCount + referenceCount;
            cost.Text = paid ? PartnerPricing.Estimate(service?.ServerCapabilities, service!.SelectedEngine!.ApiModel!, quality, "Custom", count, variants)?.Label ?? "Paid API · estimate unavailable" : "Local generation · no Comfy credits";
            note.Text = paid ? "GPT: 1:3–3:1, up to 3840 px / 8.29 MP. Quality affects detail, time and price. Undo does not refund credits."
                : task == AiTaskKind.GenerativeExpand ? hasSelection
                    ? "Fill only the selection with surrounding context. Existing canvas size is preserved; the expansion instruction is automatic."
                    : "Empty-area mode sends a soft mask plus context and preserves existing pixels. Whole-image mode may redraw everything. The generated patch is fitted back without changing the requested canvas size."
                : "Original size follows the canvas; MP scales its area while keeping proportions. Seed and execution mode are in Advanced.";
            ToolTip.SetTip(note, paid ? "Explicit Custom dimensions, multiples of 16; never Auto or aspect stretching. This image node has no separate reasoning/effort setting." : null);
        }
        prompt.IsVisible = task != AiTaskKind.GenerativeExpand;
        var body = Ui.Column(10, prompt, resolution, dimensions, qualityHost, note, cost, modelHost);
        dialog = new DialogWindow(task.DisplayName(), body, "Generate"); dialog.UseGenerationVariants(variantsCombo);
        RefreshExpandChoices(); Refresh(); dialog.Opened += (_, _) => { if (prompt.IsVisible) prompt.Focus(); };
        if (!await dialog.Ask(owner)) { if (service != null) service.SelectedEngine = previousEngine; return null; }
        settings.AiOriginalSize = originalSize; settings.AiMegapixels = mp; settings.AiVariants = variants; settings.AiApiQuality = quality;
        settings.AiExpansionMode = expansionMode; settings.AiExpansionMinimumSide = regionSide; settings.AiWholeExpansionMinimumSide = wholeSide;
        if (service != null) settings.AiEngineId = service.SelectedEngine?.Id;
        settings.Save();
        return new(task == AiTaskKind.GenerativeExpand ? AiPromptDefaults.Expand : prompt.Text ?? "", width, height, settings.AiSeed);
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
