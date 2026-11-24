using Composa.AI;
using System.Text.Json.Nodes;

namespace Composa.App.AI;

public sealed class EngineCatalog
{
    public IReadOnlyList<EngineProfile> Profiles { get; }
    public IReadOnlyList<string> Errors { get; }
    public string Root { get; }

    public EngineCatalog(string root)
    {
        Root = root;
        var profiles = new List<EngineProfile>();
        var errors = new List<string>();
        if (Directory.Exists(root))
        {
            foreach (var manifest in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).Order())
            {
                try { profiles.Add(EngineProfile.Load(manifest)); }
                catch (Exception error) { errors.Add($"{manifest}: {error.Message}"); }
            }
        }
        Profiles = profiles.OrderByDescending(profile => profile.Id == "flux2-klein-intel-xpu").ThenBy(profile => profile.PaidApi).ThenBy(profile => profile.DisplayName).ToArray();
        Errors = errors;
    }

    internal static EngineWorkflow WorkflowFor(EngineProfile engine, AiTaskKind task)
    {
        var scene = task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground;
        if (scene && !engine.PaidApi) return engine.Workflow("sceneEdit");
        return engine.Workflow(engine.Binding(scene ? AiTaskKind.ImageEdit : task)?.Workflow
            ?? throw new InvalidDataException($"No workflow for {task.DisplayName()}."));
    }

    private readonly Dictionary<(string Engine, AiTaskKind Operation), WorkflowModelSlot?> primarySlots = [];

    internal ImageModelChoice ModelFor(EngineProfile engine, AiTaskKind task, IReadOnlyDictionary<string, string> choices)
    {
        string? asset = engine.ApiModel;
        var title = engine.ModelName;
        WorkflowModelSlot? fileSlot = null;
        var binding = engine.Binding(task);
        if (asset == null && binding != null)
        {
            var key = (engine.Id, task);
            if (!primarySlots.TryGetValue(key, out var slot))
            {
                try
                {
                    var graph = ReadWorkflow(engine, WorkflowFor(engine, task));
                    var slots = WorkflowModels.Slots(graph, engine.Id);
                    slot = slots.FirstOrDefault(s => s.Kind == EngineAssetKind.DiffusionModel)
                        ?? slots.FirstOrDefault(s => s.Kind is EngineAssetKind.Upscaler or EngineAssetKind.BackgroundRemoval);
                    primarySlots[key] = slot;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException)
                {
                    // An unreadable graph still has a model entry. Workflow validation reports the error at launch.
                }
            }
            if (slot != null)
            {
                if (task is AiTaskKind.Upscale or AiTaskKind.ObjectSelection) fileSlot = slot;
                asset = choices.GetValueOrDefault(slot.Key) ?? slot.Default;
                var filename = asset.Replace('\\', '/').Split('/').Last();
                var defaultFilename = slot.Default.Replace('\\', '/').Split('/').Last();
                // Keep the server's exact identifier while retaining the title across variants of the same model.
                var defaultModel = filename == defaultFilename || engine.Id == "flux2-klein-intel-xpu"
                    && WorkflowModels.IsKlein9BVariant(filename);
                if (title == null || slot.Kind != EngineAssetKind.DiffusionModel || !defaultModel)
                    title = Path.GetFileNameWithoutExtension(filename);
            }
        }
        return new ImageModelChoice(title ?? asset ?? engine.DisplayName, asset ?? engine.Id, engine, FileSlot: fileSlot);
    }

    internal ImageModelChoice[] ImageModels(AiTaskKind task, EngineProfile? preferred, IReadOnlyDictionary<string,string> choices, IReadOnlyDictionary<string,string>? routes = null)
    {
        return Profiles.Where(p => p.Binding(task) != null).Select(p => ModelFor(p, task, choices))
            .GroupBy(m => m.ModelKey)
            .Select(group => group.FirstOrDefault(m => m.Engine.Id == routes?.GetValueOrDefault(group.Key))
                ?? group.FirstOrDefault(m => m.Engine.Id == preferred?.Id) ?? group.First()).ToArray();
    }

    public EngineProfile? Find(string? id) => Profiles.FirstOrDefault(profile => profile.Id == id) ?? Profiles.FirstOrDefault();
    public string DirectoryOf(EngineProfile profile) => Path.Combine(Root, profile.Id);

    public JsonObject ReadWorkflow(EngineProfile engine, EngineWorkflow workflow)
    {
        var path = Path.GetFullPath(Path.Combine(DirectoryOf(engine), workflow.File));
        var directory = Path.GetFullPath(DirectoryOf(engine)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !File.Exists(path)) throw new FileNotFoundException($"Workflow \"{workflow.Id}\" is missing from Engine Pack \"{engine.DisplayName}\".", path);
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"Workflow \"{workflow.Id}\" is not a JSON object.");
    }

    public IReadOnlyList<WorkflowModelSlot> ModelSlots(EngineProfile engine) => engine.Workflows
        .SelectMany(workflow => WorkflowModels.Slots(ReadWorkflow(engine, workflow), engine.Id))
        .DistinctBy(slot => slot.Key).OrderBy(slot => slot.Kind).ThenBy(slot => slot.NodeId, StringComparer.Ordinal).ToArray();
}

internal sealed record ImageModelChoice(string Name, string Identifier, EngineProfile Engine, DirectImageGenerator? Direct = null,
    string? Connection = null, bool AssignedWorkflow = false, bool BuiltIn = false, bool Available = true, WorkflowModelSlot? FileSlot = null)
{
    public string ModelKey => (Engine.PaidApi ? "api:" : "server:") + Identifier.Replace('\\', '/');
    internal ImageGenerationRoute Route => Direct?.Route ?? (BuiltIn ? ImageGenerationRoute.BuiltIn : ImageGenerationRoute.ComfyWorkflow);
    internal string ConnectionLabel => Connection ?? (Engine.PaidApi ? "Comfy credits" : "ComfyUI server");
    internal string SelectionKey => Direct != null ? $"{Route}:{Identifier}" : FileSlot != null
        ? $"{FileSlot.Key}:{Identifier.Replace('\\', '/')}" : $"{Engine.Id}:{AssignedWorkflow}";
}


public static class AppPromptPresets
{
    public static IReadOnlyList<PromptPreset> Load(string root)
    {
        var result = new List<PromptPreset>();
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.EnumerateFiles(root, "*.json").Order())
        {
            using var stream = File.OpenRead(file);
            result.AddRange(PromptPresetCatalog.Parse(stream));
        }
        return result;
    }
}
