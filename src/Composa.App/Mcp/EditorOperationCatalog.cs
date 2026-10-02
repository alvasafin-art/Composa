using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

/// <summary>One source of tool names, descriptions, schemas and read-only semantics. No duplicate chat API.</summary>
internal sealed class EditorOperationCatalog(ComposaTools target)
{
    private readonly Dictionary<string, MethodInfo> methods = typeof(ComposaTools).GetMethods()
        .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
        .ToDictionary(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name!, StringComparer.Ordinal);
    private readonly Dictionary<string, Tool> schemas = [];
    public IEnumerable<string> Names => methods.Keys.Order();
    public MethodInfo Method(string name) => methods.TryGetValue(name, out var method) ? method : throw new ArgumentException("Unknown editor operation: " + name);
    public Tool Schema(string name) => schemas.TryGetValue(name, out var schema) ? schema : schemas[name] = Create(Method(name), target).ProtocolTool;
    internal static McpServerTool Create(MethodInfo method, ComposaTools target)
    {
        var tool = McpServerTool.Create(method, target);
        if (method.GetParameters().Any(parameter => parameter.ParameterType == typeof(EditorExpectation[])))
        {
            var schema = System.Text.Json.Nodes.JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
            schema["properties"]!["checks"] = System.Text.Json.Nodes.JsonNode.Parse(EditorDocumentInspection.ChecksSchema);
            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
        }
        if (method.GetParameters().Any(parameter => parameter.ParameterType == typeof(EditorLayerQuery)))
        {
            var schema = System.Text.Json.Nodes.JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
            schema["properties"]!["query"] = System.Text.Json.Nodes.JsonNode.Parse(EditorLayerQuery.Schema);
            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
        }
        return tool;
    }
    public bool ReadOnly(string name) => Method(name).GetCustomAttribute<McpServerToolAttribute>()!.ReadOnly;
    public IEnumerable<Tool> Search(string query, IEnumerable<string> allowed, int count)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Select(Schema).Select(tool => (tool, score: words.Sum(word => tool.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ? 5
            : (tool.Description ?? "").Contains(word, StringComparison.OrdinalIgnoreCase) ? 1 : 0)))
            .Where(item => words.Length == 0 || item.score > 0).OrderByDescending(item => item.score).ThenBy(item => item.tool.Name).Take(count).Select(item => item.tool);
    }
}
