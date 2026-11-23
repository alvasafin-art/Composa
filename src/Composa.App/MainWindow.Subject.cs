using Avalonia.Controls;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.AI;
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
        _ => SubjectDetect.Any
    };

    private sealed record ObjectModelChoice(ObjectSelectionSource Source, string Label, string? ServerModel = null, string? SlotKey = null,
        bool QuickOutline = false, ImageGenerationRoute Route = ImageGenerationRoute.BuiltIn);
    private ComboBox? objectModelsCombo;
    private string? objectModelChoicesStamp;
    private bool refreshingObjectModels;
    private List<ObjectModelChoice> objectModelChoices = [];

    private Composa.AI.EngineProfile? ObjectSelectionEngine() => aiTasks.EngineFor(AiTaskKind.ObjectSelection)
        ?? aiTasks.Engines.Profiles.FirstOrDefault(pack => pack.Binding(AiTaskKind.ObjectSelection) != null);

    private List<ObjectModelChoice> AvailableObjectModels()
    {
        var choices = new List<ObjectModelChoice>
        {
            new(ObjectSelectionSource.EfficientSamS, "Automatic · Local"),
            new(ObjectSelectionSource.EfficientSamS, "Quick outline · Local", QuickOutline: true),
            new(ObjectSelectionSource.EfficientSamS, "rembg · CLI", Route: ImageGenerationRoute.LocalCodex),
            new(ObjectSelectionSource.EfficientSamS, "BiRefNet · fal API", Route: ImageGenerationRoute.DirectApi)
        };
        var engine = ObjectSelectionEngine();
        if (engine?.Binding(AiTaskKind.ObjectSelection) is { } binding && aiTasks.ServerCapabilities is { } server)
        {
            try
            {
                if (ComfyServerAddress.Parse(settings.ComfyServerUrl).ToString() != aiTasks.ConnectedServerUrl) return choices;
                var graph = aiTasks.Engines.ReadWorkflow(engine, engine.Workflow(binding.Workflow));
                foreach (var slot in WorkflowModels.Slots(graph, engine.Id).Where(s => s.Kind == EngineAssetKind.BackgroundRemoval))
                    foreach (var model in server.ModelChoices.GetValueOrDefault(slot.LoaderKey, []).Where(name => name.Contains("birefnet", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
                        choices.Add(new(ObjectSelectionSource.ComfyUI, model + " · ComfyUI", model, slot.Key, Route: ImageGenerationRoute.ComfyWorkflow));
            }
            catch (Exception error) when (error is IOException or FormatException or System.Text.Json.JsonException) { }
        }
        return choices;
    }

    private void RefreshObjectModelChoices()
    {
        if (objectModelsCombo == null) return;
        var route = settings.ImageRouteFor(AiTaskKind.ObjectSelection);
        var choices = AvailableObjectModels();
        var stamp = string.Join("\n", choices.Select(c => c.Label + c.SlotKey)) + route + settings.ObjectSelectionComfyModel + settings.ObjectSelectionQuickOutline;
        if (objectModelChoicesStamp == stamp) return;
        objectModelChoicesStamp = stamp; objectModelChoices = choices;
        refreshingObjectModels = true;
        objectModelsCombo.ItemsSource = choices.Select(c => c.Label).ToArray();
        objectModelsCombo.SelectedIndex = choices.FindIndex(c => c.Route == route && (route == ImageGenerationRoute.ComfyWorkflow
            ? c.ServerModel == settings.ObjectSelectionComfyModel : route != ImageGenerationRoute.BuiltIn || c.QuickOutline == settings.ObjectSelectionQuickOutline));
        if (objectModelsCombo.SelectedIndex < 0 && route == ImageGenerationRoute.ComfyWorkflow)
            objectModelsCombo.SelectedIndex = choices.FindIndex(c => c.Route == route);
        objectModelsCombo.PlaceholderText = route == ImageGenerationRoute.ComfyWorkflow
            ? settings.ObjectSelectionComfyModel ?? "Connect ComfyUI to list models" : "Choose an installed model";
        refreshingObjectModels = false;
    }

    private Control ObjectSelectionModelMenu()
    {
        var combo = objectModelsCombo = new ComboBox { Width = 200, Name = "ObjectSelectionModel" };
        objectModelChoicesStamp = null;
        combo.SelectionChanged += (_, _) =>
        {
            if (refreshingObjectModels || combo.SelectedIndex < 0 || combo.SelectedIndex >= objectModelChoices.Count) return;
            var choice = objectModelChoices[combo.SelectedIndex];
            settings.ImageOperationRoutes[AiTaskKind.ObjectSelection] = choice.Route;
            if (choice.Route == ImageGenerationRoute.BuiltIn)
            {
                settings.ObjectSelectionModel = choice.Source;
                settings.ObjectSelectionQuickOutline = choice.QuickOutline;
            }
            if (choice.SlotKey != null && choice.ServerModel != null)
            {
                settings.ObjectSelectionModel = ObjectSelectionSource.ComfyUI;
                settings.ObjectSelectionComfyModel = choice.ServerModel;
                var address = aiTasks.ConnectedServerUrl ?? settings.ComfyServerUrl;
                var selected = new Dictionary<string, string>(settings.ComfyModelsFor(address)) { [choice.SlotKey] = choice.ServerModel };
                var engine = ObjectSelectionEngine();
                settings.SetComfyModels(address, selected);
                if (engine != null)
                {
                    settings.AiTaskModelKeys[nameof(AiTaskKind.ObjectSelection)] = aiTasks.Engines.ModelFor(engine, AiTaskKind.ObjectSelection, selected).ModelKey;
                    settings.AiTaskEngineIds[nameof(AiTaskKind.ObjectSelection)] = engine.Id;
                }
            }
            if (session != null) session.Detect = LocalDetect;
            settings.Save();
            RefreshAiUi();
        };
        RefreshObjectModelChoices();
        Locale.Tip(combo, "Click an object or drag a box around it. Automatic refines hair and soft edges locally. Quick outline makes a crisp selection.");
        return combo;
    }

    internal async Task RunObjectSelection(SKRectI? region, SelectionMode mode, SKPointI? point = null)
    {
        var target = session;
        if (target == null || localSelectionBusy || canvas.IsDragging || target.IsInteracting) return;
        var route = settings.ImageRouteFor(AiTaskKind.ObjectSelection);
        if (DirectImageGenerator.IsDirect(route))
        {
            if (point is { } clicked && !target.Document.Bounds.Contains(clicked.X, clicked.Y)) return;
            await RunAi(AiTaskKind.ObjectSelection, useInlinePrompt: true, selectionRegion: region ?? target.Document.Bounds, selectionOperation: mode, selectionPoint: point);
            return;
        }
        if (route == ImageGenerationRoute.BuiltIn && settings.ObjectSelectionModel is
            ObjectSelectionSource.MobileSam or ObjectSelectionSource.EfficientSamTi or ObjectSelectionSource.EfficientSamS or ObjectSelectionSource.ComfyUI)
        {
            localSelectionBusy = true;
            refreshOptions?.Invoke();
            try
            {
                var model = settings.ObjectSelectionModel switch { ObjectSelectionSource.MobileSam => PromptModels.MobileSam,
                    ObjectSelectionSource.EfficientSamTi => PromptModels.EfficientSamTi, _ => PromptModels.EfficientSamS };
                var precise = !settings.ObjectSelectionQuickOutline && TrimapMatting.IsAvailable;
                var found = await ProgressWindow.Run(this, "Selecting with " + model.Name + "…", async (c, progress) =>
                {
                    return await target.SelectPromptObjectAsync(model, point, region ?? (point == null ? target.Document.Bounds : null), mode, c,
                        preciseEdges: precise, progress: progress, canApply: () => session == target && sessions.Contains(target));
                });
                if (found == false && session == target)
                    ShowNote(target.SampleAllLayers ? "No object found. Try a tighter box or click inside the object."
                        : "No object found on this layer. Try a tighter box or enable Sample all layers.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { ShowProblem("Object selection failed: " + error.Message); }
            finally { localSelectionBusy = false; refreshOptions?.Invoke(); }
            return;
        }
        if (route == ImageGenerationRoute.BuiltIn && settings.ObjectSelectionModel == ObjectSelectionSource.PlainBackdrop && region == null)
        {
            if (point is { } p) target.SelectObject(p.X, p.Y, mode);
            else if (!target.SelectSubject(mode)) ShowNote("No subject stands out from the plain backdrop.");
            return;
        }
        if (route == ImageGenerationRoute.ComfyWorkflow)
        {
            var available = AvailableObjectModels().Where(c => c.Route == ImageGenerationRoute.ComfyWorkflow).ToArray();
            var choice = available.FirstOrDefault(c => c.ServerModel == settings.ObjectSelectionComfyModel) ?? available.FirstOrDefault();
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
            await RunAi(AiTaskKind.ObjectSelection, useInlinePrompt: true, selectionRegion: region ?? target.Document.Bounds, selectionOperation: mode, selectionPoint: point);
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
