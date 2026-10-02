using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.Editing;
using Composa.Model;
using Composa.Selections;

namespace Composa.App.Mcp;

/// <summary>Machine-readable editor semantics, shared by MCP, chat and assertions. Never includes pixel buffers.</summary>
internal static class EditorDocumentInspection
{
    internal static readonly string[] Kinds = ["raster", "shape", "text", "group", "adjustment", "smartObject"];
    // This JSON is model data, not HTML. Preserve Unicode text instead of inflating every letter to \uXXXX.
    internal static readonly JsonSerializerOptions ContentJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal const string ChecksSchema = """
        {"type":"array","minItems":1,"maxItems":32,"items":{"type":"object","properties":{
        "layer":{"type":["string","null"],"description":"Target: full layer id or unique name, e.g. Tile. Use null for canvas, guides or selection."},
        "property":{"type":"string","enum":["width","height","resolution","layerCount","guideCount","hasSelection","showGuides","guidesLocked","name","kind","visible","opacity","blend","insideCanvas","hasMask","maskEnabled","shape.fill","shape.kind","text.value","text.color","text.size","transform.x","transform.y","transform.width","transform.height","transform.rotation"]},
        "operator":{"type":"string","enum":["equals","near","exists","absent"]},
        "expected":{"type":["string","number","boolean","null"],"description":"Actual scalar value, e.g. 2, true or #FFFF00. Not an object with a value property."},
        "tolerance":{"type":"number","minimum":0}},"required":["layer","property"],"additionalProperties":false}}
        """;

    internal static void ValidateChecks(EditorSession s, EditorExpectation[] checks)
    {
        if (checks.Length > 32) throw new ArgumentException("Supply at most 32 postconditions.");
        var document = Document(s);
        foreach (var check in checks)
        {
            using var schema = JsonDocument.Parse(ChecksSchema);
            if (!schema.RootElement.GetProperty("items").GetProperty("properties").GetProperty("property").GetProperty("enum")
                .EnumerateArray().Any(value => value.GetString() == check.Property))
                throw new ArgumentException("Choose property from the tool schema's enum; no JSON paths or layer indices.");
            if (check.Expected is { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
                throw new ArgumentException("expected must be a scalar literal (e.g. 2, true, #FFFF00), not {value:...}. Check array elements separately.");
            if (check.Property == "kind" && check.Expected is { ValueKind: JsonValueKind.String } kind && !Kinds.Contains(kind.GetString()))
                throw new ArgumentException("kind uses exact native values: " + string.Join(", ", Kinds) + ". Read the actual result; do not recreate correctly converted objects to fix a check's spelling.");
            var isDocument = document.ContainsKey(check.Property);
            if ((check.Layer == null) != isDocument)
                throw new ArgumentException(isDocument ? "Use layer:null for document properties." : "Specify layer as its full id or unique name for this property.");
        }
    }
    public static JsonObject Document(EditorSession s) => JsonSerializer.SerializeToNode(new
    {
        width = s.Document.Width, height = s.Document.Height, resolution = s.Document.Resolution,
        activeLayerId = s.Document.ActiveLayerId, layerCount = s.Document.AllLayers().Count(),
        hasSelection = s.Selection != null,
        selection = s.Selection == null ? null : Bounds(SelectionMask.Bounds(s.Selection)),
        guideCount = s.Guides.Count, guides = s.Guides.Select(g => new { id = g.Id, axis = g.Axis.ToString().ToLowerInvariant(), position = g.Position }),
        showGuides = s.View.ShowGuides, guidesLocked = s.View.LockGuides
    })!.AsObject();

    public static JsonObject Layer(EditorSession s, Layer layer, bool fullText = false) => JsonSerializer.SerializeToNode(new
    {
        id = layer.Id, name = layer.Name, parentId = s.Document.ParentOf(layer.Id)?.Id,
        kind = Kind(layer),
        visible = layer.Visible, opacity = layer.Opacity, blend = layer.Blend.ToString(), tags = layer.Tags,
        transform = new { x = layer.Transform.X, y = layer.Transform.Y, width = layer.Transform.Width, height = layer.Transform.Height, rotation = layer.Transform.Rotation },
        bounds = Bounds(ContentBounds(layer)), insideCanvas = InsideCanvas(s, ContentBounds(layer)),
        shape = layer.Shape == null ? null : new { kind = layer.Shape.Kind.ToString(), fill = Color(layer.Shape.Fill) },
        text = layer.Text == null ? null : new { value = fullText ? layer.Text.Text : layer.Text.Text[..Math.Min(400, layer.Text.Text.Length)],
            length = layer.Text.Text.Length, truncated = !fullText && layer.Text.Text.Length > 400, size = layer.Text.Size, color = Color(layer.Text.Color), font = layer.Text.FontFamily },
        hasMask = layer.Mask != null, maskEnabled = layer.MaskEnabled,
        smartSourceId = layer.SmartObject?.Id, children = layer.Children.Select(child => child.Id)
    })!.AsObject();

    internal static string Kind(Layer layer) => layer.IsSmartObject ? "smartObject" : layer.IsGroup ? "group" : layer.IsAdjustment ? "adjustment" : layer.Text != null ? "text" : layer.Shape != null ? "shape" : "raster";

    private static SkiaSharp.SKRect ContentBounds(Layer layer)
    {
        if (!layer.IsGroup) return layer.Bounds;
        var bounds = SkiaSharp.SKRect.Empty;
        foreach (var child in layer.Children)
        {
            var content = ContentBounds(child);
            if (!content.IsEmpty) bounds = bounds.IsEmpty ? content : SkiaSharp.SKRect.Union(bounds, content);
        }
        return bounds;
    }
    private static bool InsideCanvas(EditorSession s, SkiaSharp.SKRect bounds) => bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= s.Document.Width && bounds.Bottom <= s.Document.Height;

    private static object Bounds(SkiaSharp.SKRect rect) => new { x = rect.Left, y = rect.Top, width = rect.Width, height = rect.Height };
    private static string Color(uint color) => $"#{color & 0xFFFFFF:X6}";

    public static string Read(EditorSession s, int offset, int count, string? layer = null)
    {
        if (offset < 0 || count is < 1 or > 20) throw new ArgumentException("offset must be nonnegative; count must be 1–20.");
        var all = s.Document.AllLayers().Reverse().ToArray();
        var page = layer == null ? all.Skip(offset).Take(count).ToArray() : [Resolve(s, layer)];
        var state = Document(s);
        state["schemaVersion"] = 1; state["layerOrder"] = "panel-top-to-bottom";
        state["layers"] = new JsonArray(page.Select(item => (JsonNode)Layer(s, item)).ToArray());
        state["offset"] = offset; state["nextOffset"] = offset + page.Length;
        state["hasMore"] = layer == null && offset + page.Length < all.Length;
        return state.ToJsonString(ContentJson);
    }

    internal static Layer Resolve(EditorSession s, string reference)
    {
        var matches = s.Document.AllLayers().Where(layer => layer.Id.ToString() == reference || layer.Name == reference).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException(matches.Length == 0
            ? "Layer not found: " + reference : "Ambiguous layer name; use the full id from get_document_state: " + reference);
    }

    // JSON Pointer (RFC 6901), not executable expressions supplied by the model.
    internal static JsonNode? Pointer(JsonNode root, string path, out bool exists)
    {
        if (path == "") { exists = true; return root; }
        if (!path.StartsWith('/')) throw new ArgumentException("path must be a JSON Pointer, e.g. /text/value or /width.");
        JsonNode? current = root;
        foreach (var encoded in path[1..].Split('/'))
        {
            var part = encoded.Replace("~1", "/").Replace("~0", "~");
            if (current is JsonObject obj && obj.TryGetPropertyValue(part, out var next)) current = next;
            else if (current is JsonArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count) current = array[index];
            else { exists = false; return null; }
        }
        exists = true; return current;
    }

    public static string Verify(EditorSession s, EditorExpectation[] checks)
    {
        if (checks.Length is < 1 or > 32) throw new ArgumentException("Supply 1–32 explicit postconditions.");
        ValidateChecks(s, checks);
        var results = checks.Select(check =>
        {
            if (!double.IsFinite(check.Tolerance) || check.Tolerance < 0) throw new ArgumentException("tolerance must be finite and nonnegative.");
            var root = check.Layer == null ? Document(s) : Layer(s, Resolve(s, check.Layer), fullText: true);
            var actual = Pointer(root, "/" + check.Property.Replace('.', '/'), out var exists);
            var expected = check.Expected is { } value ? JsonNode.Parse(value.GetRawText()) : null;
            var passed = check.Operator switch
            {
                "exists" => exists && actual != null,
                "absent" => !exists || actual == null,
                "equals" => exists && JsonNode.DeepEquals(actual, expected),
                "near" => exists && actual is JsonValue a && expected is JsonValue b && a.TryGetValue<double>(out var av)
                    && b.TryGetValue<double>(out var bv) && Math.Abs(av - bv) <= check.Tolerance,
                _ => throw new ArgumentException("operator must be equals, near, exists or absent.")
            };
            return new { check.Layer, check.Property, check.Operator, passed, actual = actual?.ToJsonString() is { Length: > 1000 } ? "[large value; inspect layer]" : actual, expected };
        }).ToArray();
        return JsonSerializer.Serialize(new { passed = results.All(result => result.passed), checks = results }, ContentJson);
    }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record EditorExpectation
{
    public string? Layer { get; init; }
    public string Property { get; init; } = "";
    public string Operator { get; init; } = "equals";
    public JsonElement? Expected { get; init; }
    public double Tolerance { get; init; }
}
