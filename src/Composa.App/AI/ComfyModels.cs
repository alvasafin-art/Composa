using Composa.AI;
using System.Text.Json;
using System.Text;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Composa.App.AI;

public enum ComfyConnectionState { Disconnected, Connecting, Connected, Error }
public enum AiOperationStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed record ComfyDeviceMemory(string Name, string Type, int Index, long Total, long Free);
public sealed record ComfyServerInfo(string? Version, string? OperatingSystem, string? PythonVersion, IReadOnlyList<string> Devices)
{
    public IReadOnlyList<ComfyDeviceMemory> Memory { get; init; } = [];
}
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
    public double? CreditsUsed { get; init; }
    public bool IsIndeterminate => Status == AiOperationStatus.Running && (Value == null || Maximum is null or <= 0);
}

public sealed record ComfyExecutionResult(string PromptId, JsonDocument History, IReadOnlyList<ComfyImageReference> Images)
{
    public double? CreditsUsed { get; init; }
}

internal static class ComfyEventParser
{
    public static AiOperationState ParseBinary(ReadOnlySpan<byte> bytes, AiOperationState current)
    {
        // ComfyUI BinaryEventTypes.TEXT: event id, node-id byte count, node id, UTF-8 progress text.
        if (bytes.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != 3) return current;
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        if (length > (uint)(bytes.Length - 8)) return current;
        var node = Encoding.UTF8.GetString(bytes.Slice(8, (int)length));
        if (node != current.NodeId) return current;
        var message = Encoding.UTF8.GetString(bytes[(8 + (int)length)..]);
        var match = Regex.Match(message, @"Price:\s*([\d,]+(?:\.\d+)?)\s+credits", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        double? price = match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            && double.IsFinite(amount) && amount >= 0 ? amount : null;
        return current with { Stage = message.Length > 500 ? message[..500] : message, CreditsUsed = price ?? current.CreditsUsed };
    }
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
            // Current ComfyUI emits executing/node:null just before execution_success. History is not guaranteed to
            // contain outputs yet, so only the explicit success event may complete the operation.
            return current with { Status = AiOperationStatus.Running, Stage = "Finalizing result", NodeId = null };
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
