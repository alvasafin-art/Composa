using System.ComponentModel;
using System.Text.Json;
using Composa.Editing;
using Composa.Model;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace Composa.App.Mcp;

/// <summary>Declarative selection shared by inspection and atomic property edits; never executes model expressions.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record EditorLayerQuery
{
    public string? Kind { get; init; }
    public string? ShapeKind { get; init; }
    public string? NameContains { get; init; }
    public string? Tag { get; init; }
    public string? Parent { get; init; }
    public double? AspectRatio { get; init; }
    public string Order { get; init; } = "top";
    public int Start { get; init; }
    public int Step { get; init; } = 1;

    internal const string Schema = """
    {"type":"object","properties":{
    "kind":{"type":"string","enum":["raster","shape","text","group","adjustment","smartObject"]},
    "shapeKind":{"type":"string","enum":["Rectangle","Ellipse","RoundedRectangle","Line"]},
    "nameContains":{"type":"string","description":"Case-insensitive name substring; not a regex."},
    "tag":{"type":"string"},"parent":{"type":"string","description":"Full id or unique group name; restrict to its descendants."},
    "aspectRatio":{"type":"number","exclusiveMinimum":0,"description":"Frame width/height, e.g. 1 for equal-sided shapes. Filter BEFORE ordering and stepping."},
    "order":{"type":"string","enum":["top","bottom"],"default":"top","description":"Panel order among matching layers."},
    "start":{"type":"integer","minimum":0,"default":0,"description":"Zero-based starting index among FILTERED, ORDERED matches."},
    "step":{"type":"integer","minimum":1,"default":1,"description":"Take start, start+step, start+2*step etc. Machine computes this; do not count ids manually."}},"additionalProperties":false}
    """;

    internal Layer[] Resolve(EditorSession s)
    {
        if (Order is not ("top" or "bottom") || Start < 0 || Step < 1) throw new ArgumentException("order is top/bottom; start >= 0; step >= 1.");
        if (Kind != null && !EditorDocumentInspection.Kinds.Contains(Kind)) throw new ArgumentException("Use an exact native layer kind.");
        if (ShapeKind != null && !Enum.TryParse<Composa.Model.ShapeKind>(ShapeKind, out _)) throw new ArgumentException("Use an exact native shapeKind.");
        if (AspectRatio is { } ratio && (!double.IsFinite(ratio) || ratio <= 0)) throw new ArgumentException("aspectRatio must be finite and positive.");
        var scope = Parent == null ? s.Document.AllLayers() : Descendants(EditorDocumentInspection.Resolve(s, Parent));
        if (Order == "top") scope = scope.Reverse();
        var filtered = scope.Where(layer => (Kind == null || EditorDocumentInspection.Kind(layer) == Kind)
            && (ShapeKind == null || layer.Shape?.Kind.ToString() == ShapeKind)
            && (NameContains == null || layer.Name.Contains(NameContains, StringComparison.OrdinalIgnoreCase))
            && (Tag == null || layer.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase))
            && (AspectRatio == null || layer.Transform.Height > 0 && Math.Abs(layer.Transform.Width / layer.Transform.Height - AspectRatio.Value) <= 0.000001));
        return filtered.Where((_, index) => index >= Start && (index - Start) % Step == 0).ToArray();
    }
    private static IEnumerable<Layer> Descendants(Layer parent) => parent.IsGroup ? Document.Flatten(parent.Children)
        : throw new ArgumentException("parent must be a group.");
}

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "query_layers", ReadOnly = true, Idempotent = true)]
    [Description("Find layers deterministically by native kind, shape kind, geometry/aspect ratio, name, tag or parent. Filtering comes FIRST, then panel order, then zero-based start/step. Returns exact matches with ids and properties; page results without changing query. Use this instead of guessing ids or mentally counting large stacks. Same selector is accepted by batch_set_layers.")]
    public Task<string> QueryLayers(EditorLayerQuery query, int page = 0, int count = 20, int? document = null) => OnUi(() =>
    {
        if (page < 0 || count is < 1 or > 20) throw new ArgumentException("page >= 0; count 1–20.");
        var s = Session(document); var matches = query.Resolve(s);
        var offset = (long)page * count;
        var subset = offset >= matches.Length ? [] : matches.Skip((int)offset).Take(count).ToArray();
        return JsonSerializer.Serialize(new { matchedCount = matches.Length, page, nextPage = page + 1, hasMore = offset + subset.Length < matches.Length,
            layers = subset.Select(layer => EditorDocumentInspection.Layer(s, layer)) }, EditorDocumentInspection.ContentJson);
    });

    [McpServerTool(Name = "batch_set_layers")]
    [Description("Apply properties to ALL layers matched by a deterministic query in ONE undoable command. Query filters first, orders matches, then applies start/step; no manual id counting. shapeColor edits live shape fills without rasterization. visible, opacity and blend edit metadata. Unmatched layers, geometry, hierarchy and masks stay unchanged. Preview query_layers first when the target is uncertain. A query with zero matches is an error, not completion.")]
    public Task<string> BatchSetLayers(EditorLayerQuery query, string? shapeColor = null, bool? visible = null, double? opacity = null,
        string? blend = null, int? document = null) => OnUi(() =>
    {
        var s = Editable(document); var targets = query.Resolve(s);
        if (targets.Length == 0) throw new ArgumentException("The query matched no layers; inspect targets before editing.");
        if (shapeColor == null && visible == null && opacity == null && blend == null) throw new ArgumentException("Supply a property to change.");
        SKColor? color = null;
        if (shapeColor != null)
        {
            if (!SKColor.TryParse(shapeColor, out var parsed)) throw new ArgumentException("shapeColor must be a color such as #FFFF00.");
            if (targets.Any(layer => layer.Shape == null)) throw new ArgumentException("shapeColor requires only live shape targets; restrict query.kind to shape.");
            color = parsed;
        }
        if (opacity is { } alpha && (!double.IsFinite(alpha) || alpha is < 0 or > 1)) throw new ArgumentException("opacity must be 0–1.");
        var mode = blend == null ? (BlendMode?)null : ParseBlend(blend);
        // Resolve and validate the entire batch before editing. IDs/order stay stable during mutation.
        s.RunTransaction("Edit layers", editor =>
        {
            foreach (var layer in targets)
            {
                if (color is { } fill) editor.SetShapeColor(layer, fill);
                if (visible is { } show) editor.SetVisible(layer, show);
                if (opacity is { } amount) editor.SetOpacity(layer, amount);
                if (mode is { } value) editor.SetBlend(layer, value);
            }
        });
        s.NotifyLayersChanged();
        return JsonSerializer.Serialize(new { matchedCount = targets.Length, editedLayerIds = targets.Take(64).Select(layer => layer.Id), idsTruncated = targets.Length > 64 }, EditorDocumentInspection.ContentJson);
    });
}
