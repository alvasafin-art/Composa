using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;

namespace Composa.App.AI;

/// <summary>A model input of a workflow, not a client-side file path. Equal slots across tasks share a choice.</summary>
public sealed record WorkflowModelSlot(string EngineId, string NodeId, string NodeType, string Input, string Default, EngineAssetKind Kind)
{
    public string Key => JsonSerializer.Serialize(new[] { EngineId, NodeType, NodeId, Input, Default });
    public string LoaderKey => NodeType + "." + Input;
    public string Label => Kind switch
    {
        EngineAssetKind.DiffusionModel => "Diffusion model",
        EngineAssetKind.TextEncoder => "Text encoder",
        EngineAssetKind.Vae => "VAE",
        EngineAssetKind.Upscaler => "Upscaler",
        EngineAssetKind.BackgroundRemoval => "Subject selection",
        _ => Kind.ToString()
    };
}

public static class WorkflowModels
{
    public static IReadOnlyList<WorkflowModelSlot> Slots(JsonObject graph, string engineId)
    {
        var slots = new List<WorkflowModelSlot>();
        foreach (var (id, value) in graph)
        {
            if (value is not JsonObject node || node["class_type"]?.GetValue<string>() is not { } type
                || node["inputs"] is not JsonObject inputs) continue;
            foreach (var (input, model) in inputs)
                if (ComfyClient.AssetKind(input, type, out var kind) && model is JsonValue scalar
                    && scalar.TryGetValue<string>(out var name)) slots.Add(new(engineId, id, type, input, name, kind));
        }
        return slots;
    }

    public static void ApplyChoices(JsonObject graph, string engineId, IReadOnlyDictionary<string, string> choices)
    {
        foreach (var slot in Slots(graph, engineId))
            if (choices.TryGetValue(slot.Key, out var name) && !string.IsNullOrWhiteSpace(name))
                graph[slot.NodeId]!["inputs"]![slot.Input] = name;
    }

    /// <summary>Only resolve an exact identifier or a unique same filename in a subfolder. Never pick a different model.</summary>
    public static string? Resolve(string name, IEnumerable<string> choices)
    {
        var names = choices.ToArray();
        if (names.Contains(name, StringComparer.Ordinal)) return name;
        static string Normalize(string value) => value.Replace('\\', '/');
        var normalized = names.Where(choice => Normalize(choice).Equals(Normalize(name), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (normalized.Length == 1) return normalized[0];
        // Explicit subfolder choices must not silently jump to a different folder when removed.
        if (Normalize(name).Contains('/')) return null;
        var matches = names.Where(choice => Normalize(choice).Split('/').Last().Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static string? ResolveSlot(WorkflowModelSlot slot, string name, IEnumerable<string> choices)
    {
        var exact = Resolve(name, choices);
        if (exact != null || slot.Kind != EngineAssetKind.DiffusionModel || name.Contains('/') || name.Contains('\\')) return exact;
        var norm = NormalizeFile(name);
        var matches = choices.Where(choice => NormalizeFile(choice) == norm).Take(2).ToArray();
        if (matches.Length != 0) return matches.Length == 1 ? matches[0] : null;
        // Only the bundled reference weight gets a quantization fallback, and only when the family is unambiguous.
        if (norm != "flux-2-klein-9b-int8-convrot.safetensors" || slot.LoaderKey != "UNETLoader.unet_name") return null;
        var variants = choices.Where(IsKlein9BVariant).Take(2).ToArray();
        return variants.Length == 1 ? variants[0] : null;
    }

    internal static bool IsKlein9BVariant(string name)
    {
        var file = NormalizeFile(name);
        return file.StartsWith("flux-2-klein-9b-", StringComparison.Ordinal) && file.EndsWith(".safetensors", StringComparison.Ordinal)
            && !file.Contains("-base", StringComparison.Ordinal);
    }

    private static string NormalizeFile(string file) => file.Replace('\\', '/').Split('/').Last().Replace('_', '-').ToLowerInvariant();

    internal static bool IsKnownIncompatible(WorkflowModelSlot slot, string name)
    {
        if (slot.Kind != EngineAssetKind.DiffusionModel || slot.EngineId != "flux2-klein-intel-xpu") return false;
        var file = name.Replace('\\', '/').Split('/').Last().Replace('_', '-').ToLowerInvariant();
        return file.Contains("klein-4b") || file.Contains("klein-base") || file.StartsWith("flux3");
    }

    public static void ResolvePaths(JsonObject graph, ComfyServerCapabilities server)
    {
        foreach (var slot in Slots(graph, ""))
        {
            if (!server.ModelChoices.TryGetValue(slot.LoaderKey, out var choices)) continue;
            var resolved = ResolveSlot(slot, slot.Default, choices);
            if (resolved != null) graph[slot.NodeId]!["inputs"]![slot.Input] = resolved;
        }
    }
}
