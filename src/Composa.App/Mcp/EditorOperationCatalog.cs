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
        // Token matching avoids substring accidents ("layer" used to drown out "rectangle").
        // Rare capability words carry more weight than ubiquitous words such as layer/selection.
        static string Stem(string word) => word switch
        {
            "selects" or "selected" or "selection" => "select",
            "rectangular" or "marquee" => "rectangle",
            "elliptical" => "ellipse",
            _ => word.Length > 4 && word.EndsWith('s') ? word[..^1] : word
        };
        static HashSet<string> Words(string value) => System.Text.RegularExpressions.Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => Stem(match.Value)).ToHashSet(StringComparer.Ordinal);
        var words = Words(query);
        var entries = allowed.Select(Schema).Select(tool => new { tool, names = Words(tool.Name), text = Words(tool.Name + " " + tool.Description) }).ToArray();
        var weights = words.ToDictionary(word => word, word => Math.Log(1 + (double)entries.Length / (1 + entries.Count(entry => entry.text.Contains(word)))));
        return entries.Select(entry => (entry.tool, score: (entry.tool.Name.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 100 : 0)
                + words.Sum(word => weights[word] * (entry.names.Contains(word) ? 2 : entry.text.Contains(word) ? 1 : 0))))
            .Where(item => words.Count == 0 || item.score > 0).OrderByDescending(item => item.score).ThenBy(item => item.tool.Name).Take(count).Select(item => item.tool);
    }
}
