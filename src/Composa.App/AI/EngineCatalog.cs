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
