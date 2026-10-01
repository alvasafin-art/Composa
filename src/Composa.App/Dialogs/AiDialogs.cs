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
            service.SelectedEngine = service.Engines.Profiles.FirstOrDefault(p => p.DisplayName == name);
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
        var body = new ScrollViewer { Content = Ui.Column(12, connection, models, note), MaxHeight = Math.Clamp(owner.Bounds.Height - 140, 300, 620),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (!await new DialogWindow("ComfyUI Settings", body).Ask(owner)) { service.SelectedEngine = originalEngine; return false; }
        _ = ComfyServerAddress.Parse(url.Text ?? "");
        settings.ComfyServerUrl = ComfyServerAddress.Parse(url.Text ?? "").ToString();
        settings.ComfyConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
        settings.AiEngineId = service.SelectedEngine?.Id;
        models.Save();
        settings.Save();
        return true;
    }

    public static async Task<bool> Advanced(Window owner, Settings settings)
    {
        var mp = ClosestMegapixels(settings.AiMegapixels);
        var reference = settings.AiReferenceMegapixels;
        var grow = settings.AiMaskGrow; var blend = settings.AiMaskBlend; var context = settings.AiMaskContext;
        var seed = settings.AiSeed; var count = settings.AiVariants == 3 ? 3 : 1; var mode = settings.AiVariantMode;
        var sizes = new[] { "Original size" }.Concat(AiDimensions.MegapixelOptions.Select(AiDimensions.Label)).ToArray();
        var form = CanvasDialogs.Form(
            ("Image size", Ui.Combo(AiDimensions.MegapixelOptions, mp, AiDimensions.Label, value => mp = value, 120)),
            ("Reference images", Ui.Combo(sizes, reference is { } r ? AiDimensions.Label(ClosestMegapixels(r)) : sizes[0], value => value,
                value => reference = value == sizes[0] ? null : AiDimensions.MegapixelOptions.First(option => AiDimensions.Label(option) == value), 150)),
            ("Variants", Ui.Combo(new[] { 1, 3 }, count, value => value.ToString(), value => count = value, 90)),
            ("Execution", Ui.Combo(new[] { AiVariantMode.List, AiVariantMode.Batch }, mode,
                value => value == AiVariantMode.List ? "List · lower VRAM" : "Batch · faster, more VRAM", value => mode = value, 250)),
            ("Mask grow", Ui.Row(6, Ui.SliderField("", grow, 0, 64, value => grow = (int)value, 1, "0", 150, reset: 0), Ui.Label("px"))),
            ("Mask blend", Ui.Row(6, Ui.SliderField("", blend, 0, 64, value => blend = (int)value, 1, "0", 150, reset: 0), Ui.Label("px"))),
            ("Mask context", Ui.Row(6, Ui.SliderField("", context, 1, 8, value => context = value, 0.1, "0.0", 150, reset: 1), Ui.Label("× selection bounds"))),
            ("Seed", Ui.Row(6, Ui.Number(seed, -1, long.MaxValue, value => seed = (long)value, 1, "0", 150), Ui.Label("-1 = random"))));
        var note = Ui.Label("List runs one variant at a time and reuses uploaded references. Batch samples three together; it needs more VRAM. Each result is editable and Ctrl+Z undoes the entire generation.", Palette.Secondary);
        note.MaxWidth = 470; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        if (!await new DialogWindow("AI · Advanced", Ui.Column(12, form, note)).Ask(owner)) return false;
        settings.AiMegapixels = mp; settings.AiReferenceMegapixels = reference;
        settings.AiMaskGrow = grow; settings.AiMaskBlend = blend; settings.AiMaskContext = context;
        settings.AiSeed = seed; settings.AiVariants = count; settings.AiVariantMode = mode; settings.Save();
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

    public static async Task<AiPromptResult?> Prompt(Window owner, AiTaskKind task, Settings settings, int documentWidth, int documentHeight, string initialPrompt = "")
    {
        var prompt = new TextBox { Text = initialPrompt, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Width = 430, Height = 100,
            PlaceholderText = task == AiTaskKind.RemoveObject ? "Optional guidance; the selected object is removed by its mask" : "Describe the result" };
        var selectedMegapixels = ClosestMegapixels(settings.AiMegapixels);
        var (width, height) = AiDimensions.FromMegapixels(selectedMegapixels, documentWidth, documentHeight);
        var dimensions = Ui.Label($"{width} × {height} px", Palette.Secondary);
        var megapixels = Ui.Combo(AiDimensions.MegapixelOptions, selectedMegapixels, AiDimensions.Label, value =>
        {
            selectedMegapixels = value;
            (width, height) = AiDimensions.FromMegapixels(value, documentWidth, documentHeight);
            dimensions.Text = $"{width} × {height} px";
        }, 110);
        var seed = settings.AiSeed;
        var seedBox = Ui.Number(seed, -1, long.MaxValue, value => seed = (long)value, 1, "0", 160);
        var variants = settings.AiVariants == 3 ? 3 : 1;
        var variantsCombo = Ui.Combo(new[] { 1, 3 }, variants, value => value.ToString(), value => variants = value, 90);
        DialogWindow? dialog = null;
        var advanced = Ui.TextButton("Advanced…", () => { });
        advanced.Click += async (_, _) =>
        {
            if (!await Advanced(dialog!, settings)) return;
            megapixels.SelectedIndex = AiDimensions.MegapixelOptions.ToList().IndexOf(ClosestMegapixels(settings.AiMegapixels));
            variantsCombo.SelectedIndex = settings.AiVariants == 3 ? 1 : 0;
            seedBox.Value = settings.AiSeed;
        };
        var body = Ui.Column(10, Ui.Label(task.DisplayName(), weight: Avalonia.Media.FontWeight.SemiBold), prompt,
            Ui.Row(8, Ui.Label("Variants"), variantsCombo, advanced),
            CanvasDialogs.Form(("Image size", Ui.Row(8, megapixels, dimensions)), ("Seed", Ui.Row(6, seedBox, Ui.Label("-1 = random", Palette.Secondary)))));
        dialog = new DialogWindow(task.DisplayName(), body, "Run");
        dialog.Opened += (_, _) => prompt.Focus();
        if (!await dialog.Ask(owner)) return null;
        settings.AiSeed = seed;
        settings.AiMegapixels = selectedMegapixels;
        settings.AiVariants = variants;
        settings.Save();
        return new(prompt.Text ?? "", width, height, seed);
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
