using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Composa.AI;

/// <summary>Stable editor features. Engine packs bind these tasks to model-specific workflows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiTaskKind>))]
public enum AiTaskKind
{
    GenerateImage,
    GenerativeFill,
    RemoveObject,
    GenerativeExpand,
    ChangeBackground,
    Harmonize,
    MatchToScene,
    Relight,
    Upscale,
    SelectSubject,
    ObjectSelection
}

public static class AiTasks
{
    public static string DisplayName(this AiTaskKind task) => task switch
    {
        AiTaskKind.GenerateImage => "Generate Image",
        AiTaskKind.GenerativeFill => "Generative Fill",
        AiTaskKind.RemoveObject => "Remove Object",
        AiTaskKind.GenerativeExpand => "Generative Expand",
        AiTaskKind.ChangeBackground => "Change Background",
        AiTaskKind.MatchToScene => "Match to Scene",
        AiTaskKind.SelectSubject => "Select Subject",
        AiTaskKind.ObjectSelection => "Object Selection",
        _ => Regex.Replace(task.ToString(), "([a-z])([A-Z])", "$1 $2")
    };

    public static bool RequiresSelection(this AiTaskKind task) =>
        task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.ChangeBackground
            or AiTaskKind.Harmonize or AiTaskKind.Relight;
}

[JsonConverter(typeof(JsonStringEnumConverter<AiOutputMode>))]
public enum AiOutputMode { NewLayer, NewLayerWithMask, LayerGroup, Selection }

[JsonConverter(typeof(JsonStringEnumConverter<EngineAssetKind>))]
public enum EngineAssetKind { Checkpoint, DiffusionModel, TextEncoder, Vae, Lora, Upscaler, BackgroundRemoval }

[JsonConverter(typeof(JsonStringEnumConverter<EngineParameterKind>))]
public enum EngineParameterKind { Text, Integer, Number, Choice, Boolean, Resolution, Seed, Lora }

public sealed record WorkflowTarget
{
    public string NodeId { get; init; } = "";
    public string Input { get; init; } = "";
}

public sealed record EngineWorkflow
{
    public string Id { get; init; } = "";
    public int Version { get; init; } = 1;
    public string File { get; init; } = "";
    public List<string> OutputNodes { get; init; } = [];
}

public sealed record EngineTaskBinding
{
    public AiTaskKind Task { get; init; }
    public int Version { get; init; } = 1;
    public string Workflow { get; init; } = "";
    /// <summary>Named deterministic preprocessing pipeline, for example <c>remove-object</c>.</summary>
    public string? Preprocess { get; init; }
    public AiOutputMode OutputMode { get; init; } = AiOutputMode.NewLayer;
    /// <summary>Semantic input name to an explicit ComfyUI node id and input key.</summary>
    public Dictionary<string, WorkflowTarget> Inputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record EngineAssetRequirement
{
    public EngineAssetKind Kind { get; init; }
    public string Name { get; init; } = "";
    public bool Optional { get; init; }
}

public sealed record EngineParameter
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public EngineParameterKind Kind { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? Default { get; init; }
    public List<string>? Choices { get; init; }
}

public sealed record EngineLoraSupport
{
    public bool Supported { get; init; }
    public int Maximum { get; init; }
    public double MinimumStrength { get; init; } = -2;
    public double MaximumStrength { get; init; } = 2;
}

/// <summary>A lightweight, versioned description of a complete compatible inference pipeline. It contains no weights.</summary>
public sealed record EngineProfile
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int ManifestVersion { get; init; } = 1;
    public string? MinimumComfyVersion { get; init; }
    public List<string> RequiredNodeTypes { get; init; } = [];
    public List<EngineAssetRequirement> RequiredAssets { get; init; } = [];
    public List<EngineWorkflow> Workflows { get; init; } = [];
    public List<EngineTaskBinding> Tasks { get; init; } = [];
    public List<EngineParameter> Parameters { get; init; } = [];
    public EngineLoraSupport Lora { get; init; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static EngineProfile Parse(Stream json)
    {
        var profile = JsonSerializer.Deserialize<EngineProfile>(json, Json) ?? throw new InvalidDataException("The engine manifest is empty.");
        profile.Validate();
        return profile;
    }

    public static EngineProfile Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    public EngineTaskBinding? Binding(AiTaskKind task) => Tasks.FirstOrDefault(binding => binding.Task == task);

    public EngineWorkflow Workflow(string id) => Workflows.FirstOrDefault(workflow => workflow.Id == id)
        ?? throw new InvalidDataException($"Engine \"{DisplayName}\" refers to missing workflow \"{id}\".");

    public void Validate()
    {
        if (!Regex.IsMatch(Id, "^[a-z0-9][a-z0-9._-]{1,63}$")) throw new InvalidDataException("An engine id must be a lowercase, file-safe identifier.");
        if (string.IsNullOrWhiteSpace(DisplayName)) throw new InvalidDataException($"Engine \"{Id}\" has no display name.");
        if (ManifestVersion < 1) throw new InvalidDataException($"Engine \"{Id}\" has an invalid manifest version.");
        if (Workflows.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != Workflows.Count) throw new InvalidDataException($"Engine \"{Id}\" has duplicate workflow ids.");
        if (Tasks.Select(t => t.Task).Distinct().Count() != Tasks.Count) throw new InvalidDataException($"Engine \"{Id}\" binds a task more than once.");
        foreach (var workflow in Workflows)
        {
            if (string.IsNullOrWhiteSpace(workflow.Id) || workflow.Version < 1 || string.IsNullOrWhiteSpace(workflow.File))
                throw new InvalidDataException($"Engine \"{Id}\" has an incomplete workflow declaration.");
            if (Path.IsPathRooted(workflow.File) || workflow.File.Split('/', '\\').Contains(".."))
                throw new InvalidDataException($"Engine \"{Id}\" has an unsafe workflow path.");
        }
        foreach (var task in Tasks)
        {
            if (task.Version < 1 || Workflows.All(w => w.Id != task.Workflow))
                throw new InvalidDataException($"Task {task.Task.DisplayName()} in engine \"{Id}\" has an invalid workflow binding.");
            foreach (var (semantic, target) in task.Inputs)
                if (string.IsNullOrWhiteSpace(semantic) || string.IsNullOrWhiteSpace(target.NodeId) || string.IsNullOrWhiteSpace(target.Input))
                    throw new InvalidDataException($"Task {task.Task.DisplayName()} in engine \"{Id}\" has an incomplete parameter binding.");
        }
    }
}

public sealed record ComfyServerCapabilities
{
    public string? Version { get; init; }
    public HashSet<string> NodeTypes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<EngineAssetKind, HashSet<string>> Assets { get; init; } = [];

    public bool Has(EngineAssetRequirement requirement) =>
        Assets.TryGetValue(requirement.Kind, out var names) && names.Contains(requirement.Name);
}

public sealed record EngineCompatibility(bool IsCompatible, IReadOnlyList<string> Missing)
{
    public static EngineCompatibility Check(EngineProfile profile, ComfyServerCapabilities server)
    {
        var missing = new List<string>();
        if (profile.MinimumComfyVersion is { Length: > 0 } minimum && Version.TryParse(minimum.TrimStart('v'), out var required)
            && (!Version.TryParse(server.Version?.TrimStart('v'), out var actual) || actual < required))
            missing.Add("ComfyUI version " + minimum + " or newer");
        foreach (var node in profile.RequiredNodeTypes.Where(node => !server.NodeTypes.Contains(node))) missing.Add("node " + node);
        foreach (var asset in profile.RequiredAssets.Where(asset => !asset.Optional && !server.Has(asset)))
            missing.Add($"{asset.Kind.ToString().ToLowerInvariant()} {asset.Name}");
        return new EngineCompatibility(missing.Count == 0, missing);
    }
}

public sealed record PromptPreset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public AiTaskKind Task { get; init; }
    public string Prompt { get; init; } = "";
}

public static class PromptPresetCatalog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static IReadOnlyList<PromptPreset> Parse(Stream json)
    {
        var presets = JsonSerializer.Deserialize<List<PromptPreset>>(json, Json) ?? [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in presets)
        {
            if (!Regex.IsMatch(preset.Id, "^[a-z0-9][a-z0-9._-]{1,63}$") || !ids.Add(preset.Id) || string.IsNullOrWhiteSpace(preset.Name) || string.IsNullOrWhiteSpace(preset.Prompt))
                throw new InvalidDataException("A prompt preset is incomplete or has a duplicate/invalid id.");
        }
        return presets;
    }
}
