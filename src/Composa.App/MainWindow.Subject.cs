using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Composa.App.AI;
using Composa.AI;
using Composa.App.Dialogs;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;
using SelectionMode = Composa.Selections.SelectionMode;

namespace Composa.App;

public sealed partial class MainWindow
{
    private bool localSelectionBusy;
    private SubjectDetect LocalDetect => settings.ObjectSelectionModel switch
    {
        ObjectSelectionSource.Person => SubjectDetect.Person,
        ObjectSelectionSource.PlainBackdrop => SubjectDetect.Backdrop,
        ObjectSelectionSource.BiRefNet => SubjectDetect.BiRefNet,
        _ => SubjectDetect.Any
    };

    private sealed record ObjectModelChoice(ObjectSelectionSource Source, string Label, string? ServerModel = null, string? SlotKey = null);
    private ComboBox? objectModelsCombo;
    private string? objectModelChoicesStamp;
    private bool refreshingObjectModels;
    private List<ObjectModelChoice> objectModelChoices = [];

    private List<ObjectModelChoice> AvailableObjectModels()
    {
        var choices = new List<ObjectModelChoice>
        {
            new(ObjectSelectionSource.AnySubject, "U²-Net lite · Local"), new(ObjectSelectionSource.Person, "MODNet · Local"),
            new(ObjectSelectionSource.PlainBackdrop, "Plain backdrop")
        };
        if (SubjectModels.NativeBiRefNet.IsInstalled) choices.Add(new(ObjectSelectionSource.BiRefNet, "BiRefNet · Local (CPU)"));
        var engine = aiTasks.EngineFor(AiTaskKind.ObjectSelection);
        if (engine?.Binding(AiTaskKind.ObjectSelection) is { } binding && aiTasks.ServerCapabilities is { } server)
        {
            try
            {
                if (ComfyServerAddress.Parse(settings.ComfyServerUrl).ToString() != aiTasks.ConnectedServerUrl) return choices;
                var graph = aiTasks.Engines.ReadWorkflow(engine, engine.Workflow(binding.Workflow));
                foreach (var slot in WorkflowModels.Slots(graph, engine.Id).Where(s => s.Kind == EngineAssetKind.BackgroundRemoval))
                    foreach (var model in server.ModelChoices.GetValueOrDefault(slot.LoaderKey, []).Order(StringComparer.OrdinalIgnoreCase))
                        choices.Add(new(ObjectSelectionSource.ComfyUI, model + " · ComfyUI", model, slot.Key));
            }
            catch (Exception error) when (error is IOException or FormatException or System.Text.Json.JsonException) { }
        }
        return choices;
    }

    private void RefreshObjectModelChoices()
    {
        if (objectModelsCombo == null) return;
        var choices = AvailableObjectModels();
        var stamp = string.Join("\n", choices.Select(c => c.Label + c.SlotKey)) + settings.ObjectSelectionModel + settings.ObjectSelectionComfyModel;
        if (objectModelChoicesStamp == stamp) return;
        objectModelChoicesStamp = stamp; objectModelChoices = choices;
        refreshingObjectModels = true;
        objectModelsCombo.ItemsSource = choices.Select(c => c.Label).ToArray();
        objectModelsCombo.SelectedIndex = choices.FindIndex(c => c.Source == settings.ObjectSelectionModel && (c.ServerModel == null || c.ServerModel == settings.ObjectSelectionComfyModel));
        objectModelsCombo.PlaceholderText = settings.ObjectSelectionModel == ObjectSelectionSource.ComfyUI
            ? settings.ObjectSelectionComfyModel ?? "Connect ComfyUI to list models" : "Choose an installed model";
        refreshingObjectModels = false;
    }

    private Control ObjectSelectionModelMenu()
    {
        var combo = objectModelsCombo = new ComboBox { Width = 250, Name = "ObjectSelectionModel" };
        objectModelChoicesStamp = null;
        combo.SelectionChanged += (_, _) =>
        {
            if (refreshingObjectModels || combo.SelectedIndex < 0 || combo.SelectedIndex >= objectModelChoices.Count) return;
            var choice = objectModelChoices[combo.SelectedIndex];
            settings.ObjectSelectionModel = choice.Source;
            settings.ObjectSelectionComfyModel = choice.ServerModel;
            if (choice.SlotKey != null && choice.ServerModel != null)
            {
                var address = aiTasks.ConnectedServerUrl ?? settings.ComfyServerUrl;
                var selected = new Dictionary<string, string>(settings.ComfyModelsFor(address)) { [choice.SlotKey] = choice.ServerModel };
                settings.SetComfyModels(address, selected);
            }
            if (session != null) session.Detect = LocalDetect;
            settings.Save();
        };
        RefreshObjectModelChoices();
        ToolTip.SetTip(combo, "Local models work without ComfyUI. Server entries are the actual installed background-removal weights.");
        var native = Ui.TextButton("Local BiRefNet…", () => _ = ChooseNativeBiRefNet()); native.MinWidth = 0;
        ToolTip.SetTip(native, SubjectModels.BiRefNet.Note + " Choose the FP32 model.onnx from onnx-community/BiRefNet-ONNX.");
        return Ui.Row(6, Ui.Label("Model", Palette.Secondary), combo, native);
    }

    private async Task ChooseNativeBiRefNet()
    {
        var note = new TextBlock { Text = "BiRefNet runs locally without ComfyUI. Its separate model is 973 MB; CPU inference can need about 9 GB RAM. The installer stays small. Choose the original FP32 model.onnx.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 440 };
        var download = Ui.TextButton("Open model download", () => UpdateNotice.OpenInBrowser(SubjectModels.BiRefNet.Url));
        var dialog = new DialogWindow("Local BiRefNet", Ui.Column(10, note, download), "Choose file…");
        if (!await dialog.Ask(this)) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "BiRefNet ONNX (FP32, 973 MB)",
            FileTypeFilter = [new FilePickerFileType("BiRefNet model.onnx") { Patterns = ["*.onnx"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        var model = SubjectModels.BiRefNet with { File = path };
        var verified = await ProgressWindow.Run(this, "Verifying BiRefNet…", cancellation => Task.Run(() =>
        { cancellation.ThrowIfCancellationRequested(); var valid = model.Verify(); cancellation.ThrowIfCancellationRequested(); return valid; }, cancellation));
        if (!verified) { ShowProblem("Choose the original FP32 model.onnx from https://huggingface.co/onnx-community/BiRefNet-ONNX. The selected file does not match its verified size and SHA-256."); return; }
        settings.NativeBiRefNetPath = path; SubjectModels.BiRefNetPath = path;
        settings.ObjectSelectionModel = ObjectSelectionSource.BiRefNet;
        settings.Save(); if (session != null) session.Detect = LocalDetect;
        RefreshObjectModelChoices();
    }

    internal async Task RunObjectSelection(SKRectI? region, SelectionMode mode, SKPointI? point = null)
    {
        var target = session;
        if (target == null || localSelectionBusy || canvas.IsDragging || target.IsInteracting) return;
        if (settings.ObjectSelectionModel == ObjectSelectionSource.PlainBackdrop && region == null)
        {
            if (point is { } p) target.SelectObject(p.X, p.Y, mode);
            else if (!target.SelectSubject(mode)) ShowNote("No subject stands out from the plain backdrop.");
            return;
        }
        if (settings.ObjectSelectionModel == ObjectSelectionSource.ComfyUI)
        {
            var choice = AvailableObjectModels().FirstOrDefault(c => c.Source == ObjectSelectionSource.ComfyUI && c.ServerModel == settings.ObjectSelectionComfyModel);
            if (choice?.SlotKey == null)
            { ShowProblem("Connect ComfyUI and choose an installed object-selection model in the top bar."); return; }
            var address = aiTasks.ConnectedServerUrl!;
            var selected = new Dictionary<string, string>(settings.ComfyModelsFor(address)) { [choice.SlotKey] = choice.ServerModel! };
            settings.SetComfyModels(address, selected);
            if (point is { } p)
            {
                // A ComfyUI workflow consumes a search image, so a point uses the whole picture.
                if (p.X < 0 || p.Y < 0 || p.X >= target.Document.Width || p.Y >= target.Document.Height) return;
                region = target.Document.Bounds;
            }
            await RunAi(AiTaskKind.ObjectSelection, useInlinePrompt: true, selectionRegion: region ?? target.Document.Bounds, selectionOperation: mode);
            return;
        }
        target.Detect = LocalDetect;
        var detect = target.Detect;
        var fallback = SubjectFinder.FallbackReason(detect);
        localSelectionBusy = true;
        try
        {
            var found = await ProgressWindow.Run(this, "Selecting object…", async cancellation =>
            {
                if (region is { } box) { await target.SelectObjectInBoxAsync(box, mode, cancellation); return true; }
                if (point is { } p) { await target.SelectObjectAsync(p.X, p.Y, mode, cancellation); return true; }
                return await target.SelectSubjectAsync(mode, cancellation);
            });
            if (session != target) return;
            if (fallback != null) ShowNote(fallback);
            else if (!found) ShowNote("No subject found, the operation was cancelled, or the picture changed while selecting.");
        }
        catch (Exception error) { ShowProblem("Object selection failed: " + error.Message); }
        finally { localSelectionBusy = false; }
    }
}
