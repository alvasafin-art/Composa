using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using SkiaSharp;

namespace Composa.App.AI;

public sealed record SavedWorkflow(string Name, JsonObject Graph, Dictionary<string, WorkflowTarget> Bindings,
    bool OutputIsComposited = false);

public sealed class WorkflowPreferences
{
    public Dictionary<string, SavedWorkflow> Workflows { get; init; } = [];
    public Dictionary<AiTaskKind, string> Assignments { get; init; } = [];
    public Dictionary<AiTaskKind, string> AssignedEngines { get; init; } = [];
    public HashSet<AiTaskKind> FactoryDefaults { get; init; } = [];

    internal static readonly AiTaskKind[] Operations = [AiTaskKind.GenerateImage, AiTaskKind.ImageEdit, AiTaskKind.GenerativeFill, AiTaskKind.GenerativeExpand];

    internal WorkflowPreferences Copy() => new()
    {
        Workflows = new(Workflows), Assignments = new(Assignments), FactoryDefaults = new(FactoryDefaults),
        AssignedEngines = new(AssignedEngines)
    };

    internal EngineProfile? AssignedEngine(EngineCatalog catalog, AiTaskKind task, EngineProfile? fallback) =>
        AssignedEngines.TryGetValue(task, out var id) ? catalog.Profiles.FirstOrDefault(pack => pack.Id == id) ?? fallback : fallback;

    internal SavedWorkflow? Active(AiTaskKind task, string? engineId, bool? useAssigned = null)
    {
        var id = ActiveId(task, engineId, useAssigned);
        if (id == null) return null;
        if (Workflows.TryGetValue(id, out var workflow)) return workflow;
        throw new InvalidDataException("The assigned workflow is missing. Choose a workflow in Settings → Workflows.");
    }

    internal string? ActiveId(AiTaskKind task, string? engineId, bool? useAssigned = null)
    {
        if (!Operations.Contains(task)) return null;
        if ((useAssigned ?? !FactoryDefaults.Contains(task)) && Assignments.TryGetValue(task, out var assigned)) return assigned;
        if (engineId == null) return null;
        var factory = FactoryId(engineId, task);
        return Workflows.ContainsKey(factory) ? factory : null;
    }

    internal static string FactoryId(string engineId, AiTaskKind task) => $"factory:{engineId}:{task}";
}

internal static class EditableWorkflows
{
    internal const string ImportFormats = "ComfyUI API-format JSON or a request containing the prompt object. In ComfyUI, use Export Workflow (API).";
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    internal static readonly string[] Sources =
        ["prompt", "negativePrompt", "sourceImage", "contextImage", "activeLayerImage", "selectionMask",
            "referenceImage1", "referenceImage2", "referenceImage3", "referenceImage4", "referenceImage5", "referenceImage6",
            "seed", "width", "height", "steps", "sampler"];

    internal static bool HasTarget(JsonObject graph, WorkflowTarget target) =>
        graph[target.NodeId] is JsonObject node && node["inputs"] is JsonObject inputs && inputs.ContainsKey(target.Input);

    internal static JsonObject Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                throw new InvalidDataException("The workflow must be a JSON object. " + ImportFormats);
            if (root["nodes"] is JsonArray)
                throw new InvalidDataException("This JSON contains the ComfyUI canvas layout. Open it in ComfyUI and use Export Workflow (API).");
            var graph = root["prompt"] is JsonObject prompt && prompt["class_type"] is not JsonValue ? prompt : root;
            if (graph.Count == 0) throw new InvalidDataException("The workflow contains no nodes.");
            foreach (var (id, value) in graph)
                if (value is not JsonObject node || node["inputs"] is not JsonObject
                    || node["class_type"] is not JsonValue type || !type.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
                    throw new InvalidDataException($"Node \"{id}\" needs a class_type string and an inputs object. {ImportFormats}");
            return graph;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Fix the JSON syntax: " + error.Message, error);
        }
    }

    internal static void Validate(SavedWorkflow workflow, AiTaskKind? task = null)
    {
        if (string.IsNullOrWhiteSpace(workflow.Name)) throw new InvalidDataException("Enter a workflow name.");
        var used = new HashSet<WorkflowTarget>();
        foreach (var (source, target) in workflow.Bindings)
        {
            if (!Sources.Contains(source)) throw new InvalidDataException($"Unknown Composa input \"{source}\".");
            if (!HasTarget(workflow.Graph, target))
                throw new InvalidDataException($"Update the {source} binding: \"{target.NodeId}\" has no input \"{target.Input}\".");
            if (!used.Add(target)) throw new InvalidDataException($"Choose a different target for {source}. \"{target.NodeId}.{target.Input}\" already has a binding.");
            var value = workflow.Graph[target.NodeId]!["inputs"]![target.Input];
            var numeric = source is "seed" or "width" or "height" or "steps";
            var kind = numeric ? JsonValueKind.Number : JsonValueKind.String;
            if (value != null && value.GetValueKind() != kind)
                throw new InvalidDataException($"Bind {source} to a {kind.ToString().ToLowerInvariant()} input. Check \"{target.NodeId}.{target.Input}\".");
        }
        if (task == null) return;
        if (!workflow.Bindings.ContainsKey("prompt"))
            throw new InvalidDataException("Bind Prompt to a text input before assigning this workflow.");
        if (task is AiTaskKind.ImageEdit or AiTaskKind.GenerativeFill
            && !workflow.Bindings.Keys.Any(source => source is "sourceImage" or "contextImage" or "activeLayerImage"))
            throw new InvalidDataException("Bind Source image, Context image or Active layer image to an image input for this operation.");
        if (task == AiTaskKind.GenerativeExpand && !workflow.Bindings.Keys.Any(source => source is "sourceImage" or "contextImage"))
            throw new InvalidDataException("Bind Source image or Context image to the expanded canvas input.");
        if (task == AiTaskKind.GenerativeExpand && !workflow.Bindings.ContainsKey("selectionMask"))
            throw new InvalidDataException("Bind Selection mask to the expansion mask input.");
        if (task == AiTaskKind.GenerativeFill && !workflow.Bindings.ContainsKey("selectionMask"))
            throw new InvalidDataException("Bind Selection mask to an input that loads a grayscale mask for Generative Fill.");
    }

    internal static SavedWorkflow Factory(EngineCatalog catalog, EngineProfile engine, AiTaskKind task,
        IReadOnlyDictionary<string, string>? choices = null, ComfyServerCapabilities? capabilities = null)
    {
        var binding = engine.Binding(task)!;
        var graph = catalog.ReadWorkflow(engine, engine.Workflow(binding.Workflow));
        if (choices != null) WorkflowModels.ApplyChoices(graph, engine.Id, choices);
        if (capabilities != null) WorkflowModels.ResolvePaths(graph, capabilities);
        if (engine.Id == "flux2-klein-intel-xpu") WorkflowExecution.NativeFluxSchedule(graph);
        // Use the pack's no-reference graph as the starting point for a standalone workflow.
        graph = WorkflowBinder.Bind(graph, binding, new Dictionary<string, object?>());
        var bindings = new Dictionary<string, WorkflowTarget>();
        foreach (var (source, target) in binding.Inputs)
        {
            var key = source == "preprocessedMask" ? "selectionMask" : source;
            if (key is "prompt" or "sourceImage" or "contextImage" or "selectionMask" && HasTarget(graph, target))
                bindings[key] = target;
        }
        if (engine.Id == QwenImageWorkflow.EngineId)
        {
            bindings["prompt"] = new() { NodeId = "conditioning", Input = "prompt" };
            if (graph.ContainsKey("source")) bindings["sourceImage"] = new() { NodeId = "source", Input = "image" };
        }
        if (engine.Id == Flux3Workflow.EngineId)
        {
            bindings["prompt"] = new() { NodeId = "prompt", Input = "text" };
            if (task != AiTaskKind.GenerateImage)
            {
                var latent = graph["latent"]!["inputs"]!;
                Flux3Workflow.Bind(graph, new Dictionary<string, string> { ["sourceImage"] = "placeholder.png" },
                    graph["prompt"]!["inputs"]!["text"]!.GetValue<string>(), graph["sample"]!["inputs"]!["seed"]!.GetValue<long>(),
                    latent["width"]!.GetValue<int>(), latent["height"]!.GetValue<int>());
                bindings["sourceImage"] = new() { NodeId = "reference0", Input = "image" };
            }
        }
        if (engine.PaidApi && task != AiTaskKind.GenerateImage && graph["gpt"]?["inputs"] is JsonObject apiInputs)
        {
            graph["composa_source"] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = "placeholder.png" } };
            apiInputs["model.images.image_1"] = new JsonArray("composa_source", 0);
            bindings["sourceImage"] = new() { NodeId = "composa_source", Input = "image" };
        }
        return new(engine.DisplayName + " · " + task.DisplayName(), graph, bindings, binding.OutputIsComposited);
    }

    internal static JsonObject Bind(SavedWorkflow workflow, EngineTaskBinding binding, AiTaskInputs inputs,
        AiTaskRequest request, IReadOnlyDictionary<string, string> files, long seed)
    {
        var values = inputs.Values(files);
        var frame = inputs.SourceImage.Info.Rect;
        values["prompt"] = RegionPrompt.MaskedPrompt(inputs.Prompt, request, frame, frame,
            new SKSizeI(frame.Width, frame.Height), annotationBounds: inputs.MaskPlan?.Bounds);
        values["seed"] = seed;
        foreach (var key in new[] { "steps", "sampler" })
            if (request.Settings.Values.TryGetValue(key, out var value) && value != null) values[key] = value;
        foreach (var source in binding.Inputs.Keys)
            if (source is not ("steps" or "sampler") && !values.ContainsKey(source))
                throw new InvalidDataException($"Workflow \"{workflow.Name}\" needs {source}. Supply it or remove its binding in Settings → Workflows.");
        return WorkflowBinder.Bind(workflow.Graph, binding, values, pruneOptionalInputs: false);
    }
}
