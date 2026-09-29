using System.Text.Json;
using System.Text.Json.Nodes;

namespace Composa.AI;

/// <summary>Applies semantic values to explicit node ids. Core code never searches ComfyUI display names.</summary>
public static class WorkflowBinder
{
    public static JsonObject Bind(JsonObject workflow, EngineTaskBinding binding, IReadOnlyDictionary<string, object?> values)
    {
        var result = (JsonObject)workflow.DeepClone();
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
}
