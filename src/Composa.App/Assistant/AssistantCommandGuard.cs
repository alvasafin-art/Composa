using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Composa.AI;

namespace Composa.App.Assistant;

/// <summary>Turn-local replay protection, independent of the model's generated call id.</summary>
internal sealed class AssistantCommandGuard
{
    private sealed record Applied(string State, bool CreatesObjects);
    private readonly Dictionary<string, Applied> completed = [];
    private readonly Dictionary<string, int> failures = [];
    private int replays;

    public bool AlreadyApplied(AssistantToolCall call, string state)
    {
        if (!completed.TryGetValue(Key(call), out var applied) || !applied.CreatesObjects && applied.State != state) return false;
        if (++replays > 2) throw new InvalidOperationException("The Assistant repeatedly requested an already-applied operation. Stopped to prevent duplicates; all pending edits were rolled back.");
        return true;
    }

    public void CheckFailures(AssistantToolCall call, string state)
    {
        if (failures.GetValueOrDefault(Key(call) + state) >= 2)
            throw new InvalidOperationException("The Assistant repeated the same failing command without correcting it. All pending edits were rolled back. See Operations for the original error.");
    }

    public void Failed(AssistantToolCall call, string state)
    {
        var key = Key(call) + state;
        failures[key] = failures.GetValueOrDefault(key) + 1;
    }

    public void AppliedCommand(AssistantToolCall call, string state) => completed[Key(call)] = new(state, CreatesObjects(call));

    private static (string Name, JsonElement Args) Operation(AssistantToolCall call) =>
        call.Name == "editor_operation" && call.Arguments.ValueKind == JsonValueKind.Object &&
        call.Arguments.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
        call.Arguments.TryGetProperty("arguments", out var args) ? (name.GetString()!, args) : (call.Name, call.Arguments);

    private static bool CreatesObjects(AssistantToolCall call)
    {
        var (name, args) = Operation(call);
        if (name is "add_shape" or "add_text" or "add_line" or "new_layer" or "duplicate_layer" or "group_layers" or "import_attachment") return true;
        return name == "execute_script" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.String && Regex.IsMatch(code.GetString()!, @"\.(?:add\w+|duplicate|groupLayers)\s*\(");
    }

    private static string Key(AssistantToolCall call)
    {
        var (name, args) = Operation(call);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, args);
        return name + ":" + Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var item in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(item.Name); WriteCanonical(writer, item.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number:
                if (value.TryGetDecimal(out var number)) writer.WriteRawValue(number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture));
                else value.WriteTo(writer);
                break;
            default: value.WriteTo(writer); break;
        }
    }
}
