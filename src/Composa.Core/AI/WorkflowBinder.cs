using System.Text.Json;
using System.Text.Json.Nodes;

namespace Composa.AI;

/// <summary>Applies semantic values to explicit node ids. Core code never searches ComfyUI display names.</summary>
public static class WorkflowBinder
{
    public static JsonObject Bind(JsonObject workflow, EngineTaskBinding binding, IReadOnlyDictionary<string, object?> values)
    {
        var result = (JsonObject)workflow.DeepClone();
        PruneMissingOptionalInputs(result, values);
        foreach (var (semantic, value) in values)
        {
            if (!binding.Inputs.TryGetValue(semantic, out var target)) continue;
            if (result[target.NodeId] is not JsonObject node)
                throw new InvalidDataException($"Workflow has no node \"{target.NodeId}\" required for \"{semantic}\".");
            if (node["inputs"] is not JsonObject inputs)
                throw new InvalidDataException($"Workflow node \"{target.NodeId}\" has no inputs object.");
            inputs[target.Input] = JsonSerializer.SerializeToNode(value);
        }
        return result;
    }

    /// <summary>
    /// Engine workflows may tag a node with <c>_meta.optionalInput</c>. If that semantic input was not supplied,
    /// the node is removed. A tagged node may also declare a ComfyUI link in <c>_meta.fallback</c>; links to the
    /// removed node are then rewired to that fallback. This keeps one verified graph usable with zero to six visual
    /// references without loading placeholders or maintaining seven near-identical workflow files.
    /// </summary>
    private static void PruneMissingOptionalInputs(JsonObject workflow, IReadOnlyDictionary<string, object?> values)
    {
        var removed = new Dictionary<string, JsonArray?>(StringComparer.Ordinal);
        foreach (var (id, value) in workflow)
        {
            if (value is not JsonObject node || node["_meta"] is not JsonObject meta || meta["optionalInput"]?.GetValue<string>() is not { } semantic
                || values.ContainsKey(semantic)) continue;
            removed[id] = meta["fallback"] is JsonArray fallback ? (JsonArray)fallback.DeepClone() : null;
        }
        if (removed.Count == 0) return;

        JsonArray? Resolve(JsonArray link)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (link.Count > 0 && link[0]?.GetValue<string>() is { } id && removed.TryGetValue(id, out var fallback))
            {
                if (!seen.Add(id) || fallback == null) return null;
                link = (JsonArray)fallback.DeepClone();
            }
            return link;
        }

        foreach (var (_, value) in workflow)
        {
            if (value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            foreach (var key in inputs.Select(pair => pair.Key).ToArray())
                if (inputs[key] is JsonArray link && link.Count == 2 && link[0] is JsonValue)
                    inputs[key] = Resolve(link);
        }
        foreach (var id in removed.Keys) workflow.Remove(id);
    }
}
