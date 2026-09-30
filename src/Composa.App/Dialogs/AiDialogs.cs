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
        var megapixels = Ui.Combo(AiDimensions.MegapixelOptions, ClosestMegapixels(settings.AiMegapixels), AiDimensions.Label,
            value => settings.AiMegapixels = value, 110);
        var referenceSizes = new[] { "Original size" }.Concat(AiDimensions.MegapixelOptions.Select(AiDimensions.Label)).ToArray();
        var selectedReferenceSize = settings.AiReferenceMegapixels is { } referenceMp
            ? AiDimensions.Label(ClosestMegapixels(referenceMp)) : referenceSizes[0];
        var referenceSize = Ui.Combo(referenceSizes, selectedReferenceSize, value => value, value =>
        {
            settings.AiReferenceMegapixels = value == referenceSizes[0] ? null
                : AiDimensions.MegapixelOptions.First(option => AiDimensions.Label(option) == value);
        }, 130);
        var seed = Ui.Number(settings.AiSeed, -1, long.MaxValue, value => settings.AiSeed = (long)value, 1, "0", 150);
        var maskGrow = Ui.Number(settings.AiMaskGrow, 0, 64, value => settings.AiMaskGrow = (int)value, 1, "0", 80);
        var maskBlend = Ui.Number(settings.AiMaskBlend, 0, 64, value => settings.AiMaskBlend = (int)value, 1, "0", 80);
        var maskContext = Ui.Number(settings.AiMaskContext, 1, 8, value => settings.AiMaskContext = value, 0.1, "0.0", 80);
        var lora = new TextBox { Text = string.Join(", ", settings.AiLoras.Select(item => item.Name)), PlaceholderText = "Names from the connected server", Width = 260 };
        var connection = CanvasDialogs.Form(
            ("ComfyUI Server URL", url),
            ("Connection timeout", Ui.Row(6, timeout, Ui.Label("seconds", Palette.Secondary))),
            ("", test),
            ("Status", status),
            ("Workflow pack", engine));
        var form = CanvasDialogs.Form(
            ("Image size", megapixels),
            ("Reference images", referenceSize),
            ("Mask grow", Ui.Row(6, maskGrow, Ui.Label("px", Palette.Secondary))),
            ("Mask blend", Ui.Row(6, maskBlend, Ui.Label("px", Palette.Secondary))),
            ("Mask context", Ui.Row(6, maskContext, Ui.Label("× selection bounds", Palette.Secondary))),
            ("Seed", Ui.Row(6, seed, Ui.Label("-1 = random", Palette.Secondary))),
            ("LoRA", lora));
        var note = Ui.Label("Engine Packs contain workflows and compatibility metadata, never model weights.", Palette.Secondary);
        note.MaxWidth = 430;
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        RefreshStatus();
        var body = new ScrollViewer { Content = Ui.Column(12, connection, models, form, note), MaxHeight = Math.Clamp(owner.Bounds.Height - 140, 300, 620),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (!await new DialogWindow("ComfyUI Settings", body).Ask(owner)) return false;
        _ = ComfyServerAddress.Parse(url.Text ?? "");
        settings.ComfyServerUrl = ComfyServerAddress.Parse(url.Text ?? "").ToString();
        settings.ComfyConnectionTimeoutSeconds = (int)(timeout.Value ?? 5);
        settings.AiEngineId = service.SelectedEngine?.Id;
        models.Save();
        settings.AiLoras = (lora.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new AiLoraSetting(name, 1)).ToList();
        settings.Save();
        return true;
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
            settings.AiMegapixels = value;
            (width, height) = AiDimensions.FromMegapixels(value, documentWidth, documentHeight);
            dimensions.Text = $"{width} × {height} px";
        }, 110);
        var seed = settings.AiSeed;
        var seedBox = Ui.Number(seed, -1, long.MaxValue, value => seed = (long)value, 1, "0", 160);
        var body = Ui.Column(10, Ui.Label(task.DisplayName(), weight: Avalonia.Media.FontWeight.SemiBold), prompt,
            CanvasDialogs.Form(("Image size", Ui.Row(8, megapixels, dimensions)), ("Seed", Ui.Row(6, seedBox, Ui.Label("-1 = random", Palette.Secondary)))));
        var dialog = new DialogWindow(task.DisplayName(), body, "Run");
        dialog.Opened += (_, _) => prompt.Focus();
        if (!await dialog.Ask(owner)) return null;
        settings.AiSeed = seed;
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
