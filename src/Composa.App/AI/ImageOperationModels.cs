using Composa.AI;

namespace Composa.App.AI;

/// <summary>Builds the route-aware model list used both by settings and by the editor.</summary>
internal static class ImageOperationModels
{
    internal static AiTaskKind ModelTask(AiTaskKind task) =>
        task == AiTaskKind.SelectSubject ? AiTaskKind.ObjectSelection : task;

    internal static bool Supports(EngineProfile engine, AiTaskKind task) => engine.Binding(task) != null ||
        task == AiTaskKind.MatchToScene && engine.PaidApi && engine.Binding(AiTaskKind.Harmonize) != null;

    internal static ImageModelChoice DirectChoice(DirectImageGenerator direct, string name = "", string? apiName = null)
    {
        var utility = direct.Task is AiTaskKind.Upscale or AiTaskKind.ObjectSelection;
        var local = direct.Route == ImageGenerationRoute.LocalCodex;
        if (string.IsNullOrWhiteSpace(name))
            name = direct.Task switch
            {
                AiTaskKind.Upscale => local ? "Real-ESRGAN" : "Topaz Upscale",
                AiTaskKind.ObjectSelection => local ? (string.IsNullOrWhiteSpace(direct.UtilityModel) ? "rembg" : direct.UtilityModel) : "BiRefNet",
                _ => direct.Model
            };
        if (name.Length == 0) name = "Choose a model";
        var connection = local ? "CLI on this computer" : utility
            ? (direct.Task == AiTaskKind.Upscale ? "Polza AI · API" : "fal API")
            : string.IsNullOrWhiteSpace(apiName) ? "API directly" : apiName;
        return new(name, utility ? direct.Label : direct.Model, direct.PromptProfile(), direct, connection);
    }

    internal static ImageModelChoice LocalSelection(Settings settings)
    {
        var name = settings.ObjectSelectionModel switch
        {
            ObjectSelectionSource.MobileSam => "MobileSAM",
            ObjectSelectionSource.EfficientSamTi => "EfficientSAM Ti",
            ObjectSelectionSource.Person => "Person",
            ObjectSelectionSource.PlainBackdrop => "Plain backdrop",
            _ => "EfficientSAM S"
        };
        return new(name, name, new EngineProfile { Id = "built-in-selection", DisplayName = name }, Connection: "Built-in", BuiltIn: true);
    }

    internal static Dictionary<string, string> ApiCatalog(Settings settings)
        => ApiScope(settings) is { } scope ? settings.ImageApiCatalogs.GetValueOrDefault(scope) ?? [] : [];

    internal static IReadOnlySet<string>? GenerationOnly(Settings settings) =>
        ApiScope(settings) is { } scope ? settings.ImageApiGenerationOnly.GetValueOrDefault(scope) : null;

    private static string? ApiScope(Settings settings)
    {
        try { return DirectImageGenerator.Endpoint(settings.ImageGenerationApiUrl).AbsoluteUri; }
        catch (FormatException) { return null; }
    }

    internal static IEnumerable<ImageModelChoice> DirectChoices(DirectImageGenerator draft,
        IEnumerable<(string Id, string Label)> apiModels, string? apiName, bool apiEnabled, IReadOnlySet<string>? generationOnly = null)
    {
        var local = draft with { Route = ImageGenerationRoute.LocalCodex };
        if (local.Availability(draft.Task) == null) yield return DirectChoice(local);
        if (!apiEnabled) yield break;
        var api = draft with { Route = ImageGenerationRoute.DirectApi };
        if (draft.Task is AiTaskKind.Upscale or AiTaskKind.ObjectSelection)
        {
            yield return DirectChoice(api);
            yield break;
        }
        foreach (var (id, label) in apiModels.Append((Id: api.Model, Label: "")).Where(item => item.Id.Length > 0 &&
                     (draft.Task == AiTaskKind.GenerateImage || generationOnly?.Contains(item.Id) != true)).DistinctBy(item => item.Id))
            yield return DirectChoice(api with { ApiModel = id }, label, apiName);
    }

    internal static ImageModelChoice? ComfyDefault(EngineCatalog catalog, AiTaskKind task, EngineProfile? engine,
        IReadOnlyDictionary<string, string> files, WorkflowPreferences workflows)
    {
        var id = workflows.ActiveId(task, engine?.Id);
        if (id != null && id == workflows.Assignments.GetValueOrDefault(task) && workflows.Workflows.TryGetValue(id, out var assigned))
            return Assigned(catalog, task, workflows.AssignedEngine(catalog, task, engine) ?? CustomEngine(assigned), files, assigned);
        return engine != null && Supports(engine, task) ? catalog.ModelFor(engine, task, files) : null;
    }

    internal static ImageModelChoice[] Choices(AiTaskService service, AiTaskKind task, EngineProfile? preferred,
        IReadOnlyDictionary<string, string> files, WorkflowPreferences workflows, Settings settings, bool comfyConfigured,
        bool serverResponded, bool hasCredits, IEnumerable<ImageModelChoice> direct, ImageModelChoice? current)
    {
        var result = new List<ImageModelChoice>();
        if (task == AiTaskKind.ObjectSelection) result.Add(LocalSelection(settings));
        result.AddRange(direct);
        if (comfyConfigured)
        {
            foreach (var engine in service.Engines.Profiles)
            {
                if (!Supports(engine, task) || engine.PaidApi && !hasCredits && !serverResponded) continue;
                if (Compatible(engine, false)) result.Add(service.Engines.ModelFor(engine, task, files));
            }
            if (workflows.Assignments.TryGetValue(task, out var id) && workflows.Workflows.TryGetValue(id, out var assigned))
            {
                var engine = workflows.AssignedEngine(service.Engines, task, preferred) ?? CustomEngine(assigned);
                if ((!ComfyClient.UsesApiCredits(assigned.Graph) || hasCredits || serverResponded) && Compatible(engine, true))
                    result.Add(Assigned(service.Engines, task, engine, files, assigned));
            }
        }
        // Different packs can expose the same model on the same connection. Keep the currently preferred route for it.
        var available = result.GroupBy(choice => (choice.Route, choice.ModelKey, choice.ConnectionLabel, choice.AssignedWorkflow))
            .Select(group => group.FirstOrDefault(choice => choice.SelectionKey == current?.SelectionKey)
                ?? group.FirstOrDefault(choice => choice.Engine.Id == preferred?.Id) ?? group.First()).ToArray();
        return current != null && !available.Any(choice => choice.SelectionKey == current.SelectionKey)
            ? [current with { Available = false }, .. available] : available;

        bool Compatible(EngineProfile engine, bool useAssigned)
        {
            if (!serverResponded) return true;
            if (engine.PaidApi && !useAssigned)
                return service.ServerCapabilities is { } capabilities && engine.ApiModel is { } apiModel &&
                    PartnerPricing.SupportsModel(capabilities, apiModel);
            try
            {
                return service.OperationCompatibility(engine, task, files, settings.OperationFor(engine.Id, task, engine.PaidApi),
                    useAssigned).IsCompatible;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or KeyNotFoundException or System.Text.Json.JsonException)
            {
                return false;
            }
        }
    }

    private static ImageModelChoice Assigned(EngineCatalog catalog, AiTaskKind task, EngineProfile engine,
        IReadOnlyDictionary<string, string> files, SavedWorkflow saved)
    {
        var graph = (System.Text.Json.Nodes.JsonObject)saved.Graph.DeepClone();
        WorkflowModels.ApplyChoices(graph, engine.Id, files, savedGraph: true);
        var slot = WorkflowModels.Slots(graph, engine.Id).FirstOrDefault(item =>
            item.Kind is EngineAssetKind.DiffusionModel or EngineAssetKind.Upscaler or EngineAssetKind.BackgroundRemoval);
        var model = catalog.ModelFor(engine, task, files);
        var credits = ComfyClient.UsesApiCredits(graph);
        return model with { Name = slot == null ? saved.Name : $"{model.Name} · {saved.Name}", Identifier = slot?.Default ?? model.Identifier,
            Connection = credits ? "Comfy credits" : "ComfyUI server", AssignedWorkflow = true };
    }

    private static EngineProfile CustomEngine(SavedWorkflow workflow) =>
        new() { Id = "custom-workflow", DisplayName = workflow.Name };
}
