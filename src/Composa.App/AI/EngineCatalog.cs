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
        Profiles = profiles.OrderBy(profile => profile.PaidApi).ThenBy(profile => profile.DisplayName).ToArray();
        Errors = errors;
    }

    private readonly Dictionary<(string Engine, string Workflow), WorkflowModelSlot?> primarySlots = [];

    internal ImageModelChoice ModelFor(EngineProfile engine, AiTaskKind task, IReadOnlyDictionary<string, string> choices)
    {
        string? asset = engine.ApiModel;
        var title = engine.ModelName;
        var binding = engine.Binding(task);
        if (asset == null && binding != null)
        {
            var key = (engine.Id, binding.Workflow);
            if (!primarySlots.TryGetValue(key, out var slot))
            {
                try
                {
                    var graph = ReadWorkflow(engine, engine.Workflow(binding.Workflow));
                    slot = WorkflowModels.Slots(graph, engine.Id).FirstOrDefault(s => s.Kind == EngineAssetKind.DiffusionModel);
                    primarySlots[key] = slot;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException)
                {
                    // An unreadable graph still has a model entry. Workflow validation reports the error at launch.
                }
            }
            if (slot != null)
            {
                asset = choices.GetValueOrDefault(slot.Key) ?? slot.Default;
                var filename = asset.Replace('\\', '/').Split('/').Last();
                if (title == null || filename != slot.Default.Replace('\\', '/').Split('/').Last())
                    title = Path.GetFileNameWithoutExtension(filename);
            }
        }
        return new ImageModelChoice(title ?? asset ?? engine.DisplayName, asset ?? engine.Id, engine);
    }

    internal ImageModelChoice[] ImageModels(AiTaskKind task, EngineProfile? preferred, IReadOnlyDictionary<string,string> choices)
    {
        return Profiles.Where(p => p.Binding(task) != null).Select(p => ModelFor(p, task, choices))
            .GroupBy(m => (m.Engine.PaidApi, m.Identifier))
            .Select(group => group.FirstOrDefault(m => m.Engine.Id == preferred?.Id) ?? group.First()).ToArray();
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

internal sealed record ImageModelChoice(string Name, string Identifier, EngineProfile Engine);

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
