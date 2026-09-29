using Composa.AI;
using System.Text.Json;

namespace Composa.App.AI;

public enum ComfyConnectionState { Disconnected, Connecting, Connected, Error }
public enum AiOperationStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed record ComfyServerInfo(string? Version, string? OperatingSystem, string? PythonVersion, IReadOnlyList<string> Devices);
public sealed record ComfyQueueState(int Running, int Pending, int? Position = null);
public sealed record ComfyImageReference(string Filename, string Subfolder, string Type, string? NodeId = null);

public sealed record AiOperationState
{
    public AiOperationStatus Status { get; init; } = AiOperationStatus.Queued;
    public string? PromptId { get; init; }
    public int? QueuePosition { get; init; }
    public string? NodeId { get; init; }
    public string? Stage { get; init; }
    public int? Value { get; init; }
    public int? Maximum { get; init; }
    public string? Error { get; init; }
    public bool IsIndeterminate => Status == AiOperationStatus.Running && (Value == null || Maximum is null or <= 0);
}

public sealed record ComfyExecutionResult(string PromptId, JsonDocument History, IReadOnlyList<ComfyImageReference> Images);

internal static class ComfyEventParser
{
    public static AiOperationState Parse(string json, string promptId, AiOperationState current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        var data = root.TryGetProperty("data", out var dataValue) ? dataValue : default;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("prompt_id", out var eventPrompt)
            && eventPrompt.GetString() is { } id && id != promptId) return current;
        return type switch
        {
            "execution_start" => current with { Status = AiOperationStatus.Running, Stage = "Starting", Value = null, Maximum = null },
            "executing" => Executing(data, current),
            "progress" => Progress(data, current),
            "executed" => current with { Status = AiOperationStatus.Running, Stage = "Collecting result" },
            "execution_success" => current with { Status = AiOperationStatus.Completed, Stage = "Completed", Value = current.Maximum, Error = null },
            "execution_error" => current with { Status = AiOperationStatus.Failed, Error = Message(data), Stage = "Failed" },
            "execution_interrupted" => current with { Status = AiOperationStatus.Cancelled, Stage = "Cancelled" },
            _ => current
        };
    }

    private static AiOperationState Executing(JsonElement data, AiOperationState current)
    {
        if (!data.TryGetProperty("node", out var node) || node.ValueKind == JsonValueKind.Null)
            return current with { Status = AiOperationStatus.Completed, Stage = "Completed", NodeId = null };
        return current with { Status = AiOperationStatus.Running, NodeId = node.GetString(), Stage = "Running node " + node.GetString(), Value = null, Maximum = null };
    }

    private static AiOperationState Progress(JsonElement data, AiOperationState current)
    {
        int? value = data.TryGetProperty("value", out var v) && v.TryGetInt32(out var number) ? number : null;
        int? maximum = data.TryGetProperty("max", out var m) && m.TryGetInt32(out var max) ? max : null;
        var node = data.TryGetProperty("node", out var n) ? n.GetString() : current.NodeId;
        return current with { Status = AiOperationStatus.Running, NodeId = node, Stage = node == null ? "Running" : "Running node " + node, Value = value, Maximum = maximum };
    }

    private static string Message(JsonElement data)
    {
        foreach (var property in new[] { "exception_message", "error", "message" })
            if (data.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } message) return message;
        return "ComfyUI reported an execution error.";
    }
}
