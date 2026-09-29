using System.Text.Json;
using Composa.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Selections;
using Jint;
using SkiaSharp;

namespace Composa.App.Automation;

public sealed record ScriptResult(string? ExportedPath = null, int ExportQuality = 90);

/// <summary>A constrained JavaScript host. Scripts only receive explicit editor delegates; CLR access is not enabled.</summary>
public sealed class JavaScriptRuntime : IScriptRuntime
{
    public string Language => "JavaScript";
    public const string Reference = """
    const doc = app.activeDocument;
    doc.info;                         // { title, width, height, hasSelection, activeLayerId, layers }
    doc.layers;                       // ordered Layer[]
    doc.activeLayer;                  // Layer | null
    doc.findLayersByTag("title");     // Layer[]; prefer this over guessing names
    doc.findLayersByName("Heading"); // Layer[]
    doc.addLayer("Name");            // Layer
    doc.addAttachedImage(index);     // place an explicitly attached chat image as a new layer
    doc.selectRect(x, y, width, height); doc.deselect();
    doc.export("C:/output/image.webp", 90); // only when the user explicitly requests export

    // Layer properties are live and assignable:
    layer.name; layer.kind; layer.tags; layer.text; layer.visible; layer.opacity; layer.transform;
    layer.name = "New name";
    layer.text = "Winter Sale";       // editable text layers only
    layer.tags = ["title", "editable"];
    layer.opacity = 0.8; layer.visible = true;
    layer.transform = { x: 10, y: 20, width: 400, height: 300, rotation: 0 };
    layer.select(); layer.remove();

    // AI calls are queued after local edits and use the selected Engine Pack:
    ai.generativeFill("replace with flowers"); ai.removeObject(); ai.upscale();
    ai.changeBackground("sunset studio"); ai.harmonize("match the scene lighting");
    // Optional settings: { width, height, seed, x, y } (x/y are expansion origin).
    """;

    public ScriptResult Execute(EditorSession session, string script, string transactionName = "Run Script", CancellationToken cancellationToken = default)
    {
        var calls = new List<QueuedAiTask>();
        ScriptResult result = new();
        session.RunTransaction(transactionName, editor => result = ExecuteScript(editor, script, calls, allowAi: false, cancellationToken));
        SaveExport(session, result);
        return result;
    }

    public async Task<ScriptResult> ExecuteAsync(EditorSession session, string script, IAiTaskRunner aiRunner, Settings settings,
        string transactionName = "Run Script", CancellationToken cancellationToken = default, IReadOnlyList<string?>? attachedImages = null)
    {
        var calls = new List<QueuedAiTask>();
        ScriptResult result = new();
        await session.RunTransactionAsync(transactionName, async editor =>
        {
            result = ExecuteScript(editor, script, calls, allowAi: true, cancellationToken, attachedImages);
            foreach (var call in calls)
                await aiRunner.RunAsync(new EditorCommandService(editor), Request(editor, call, settings), cancellationToken);
        });
        SaveExport(session, result);
        return result;
    }

    private static ScriptResult ExecuteScript(EditorSession editor, string script, List<QueuedAiTask> calls, bool allowAi, CancellationToken cancellationToken,
        IReadOnlyList<string?>? attachedImages = null)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("The script is empty.", nameof(script));
        string? exportPath = null;
        var exportQuality = 90;
        var engine = new Engine(options => options
                .Strict()
                .TimeoutInterval(TimeSpan.FromSeconds(8))
                .LimitMemory(24_000_000)
                .MaxStatements(100_000)
                .CancellationToken(cancellationToken));
        engine.SetValue("__documentJson", (Func<string>)(() => DocumentJson(editor)));
        engine.SetValue("__layersJson", (Func<string>)(() => LayersJson(editor)));
        engine.SetValue("__layerJson", (Func<string, string>)(id => LayerJson(Find(editor, id))));
        engine.SetValue("__activeLayerJson", (Func<string>)(() => editor.ActiveLayer == null ? "null" : LayerJson(editor.ActiveLayer)));
        engine.SetValue("__findTagJson", (Func<string, string>)(tag => JsonSerializer.Serialize(editor.FindLayersByTag(tag).Select(LayerData))));
        engine.SetValue("__findNameJson", (Func<string, string>)(name => JsonSerializer.Serialize(editor.Document.AllLayers().Where(layer => layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(LayerData))));
        engine.SetValue("__selectLayer", (Action<string>)(id => editor.SelectLayer(Find(editor, id).Id)));
        engine.SetValue("__addLayer", (Func<string, string>)(name => { var layer = editor.AddBlankLayer(); editor.Rename(layer, name); return LayerJson(layer); }));
        engine.SetValue("__addAttachedImage", (Func<int, string>)(index =>
        {
            if (attachedImages == null || index < 0 || index >= attachedImages.Count || attachedImages[index] is not { } path)
                throw new InvalidOperationException("Choose an image explicitly attached to this chat message.");
            var image = ImageFiles.Load(path);
            return LayerJson(editor.AddImageLayer(Path.GetFileNameWithoutExtension(path), image));
        }));
        engine.SetValue("__deleteLayer", (Action<string>)(id => { editor.SelectLayer(Find(editor, id).Id); editor.DeleteSelectedLayers(); }));
        engine.SetValue("__rename", (Action<string, string>)((id, name) => editor.Rename(Find(editor, id), name)));
        engine.SetValue("__visible", (Action<string, bool>)((id, visible) => editor.SetVisible(Find(editor, id), visible)));
        engine.SetValue("__opacity", (Action<string, double>)((id, opacity) => editor.SetOpacity(Find(editor, id), opacity)));
        engine.SetValue("__tags", (Action<string, string>)((id, json) => editor.SetLayerTags(Find(editor, id), JsonSerializer.Deserialize<string[]>(json) ?? [])));
        engine.SetValue("__text", (Action<string, string>)((id, text) =>
        {
            var layer = Find(editor, id);
            if (layer.Text == null) throw new InvalidOperationException($"Layer \"{layer.Name}\" is not editable text.");
            editor.SetText(layer, layer.Text with { Text = text });
        }));
        engine.SetValue("__transform", (Action<string, double, double, double, double, double>)((id, x, y, width, height, rotation) =>
        {
            var layer = Find(editor, id);
            editor.SetTransform(layer, layer.Transform with
            {
                X = Finite(x, layer.Transform.X), Y = Finite(y, layer.Transform.Y),
                Width = Math.Max(1, Finite(width, layer.Transform.Width)), Height = Math.Max(1, Finite(height, layer.Transform.Height)),
                Rotation = Finite(rotation, layer.Transform.Rotation)
            });
        }));
        engine.SetValue("__selectRect", (Action<double, double, double, double>)((x, y, width, height) =>
            editor.SelectRect(new SKRect((float)x, (float)y, (float)(x + width), (float)(y + height)))));
        engine.SetValue("__deselect", (Action)editor.Deselect);
        engine.SetValue("__export", (Action<string, int>)((path, quality) =>
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Export needs a file path.");
            exportPath = Path.GetFullPath(path);
            exportQuality = Math.Clamp(quality, 1, 100);
        }));
        engine.SetValue("__queueAi", (Action<string, string, string>)((task, prompt, json) =>
        {
            if (!allowAi) throw new InvalidOperationException("AI tasks require asynchronous script execution.");
            if (!Enum.TryParse<AiTaskKind>(task, true, out var kind)) throw new InvalidOperationException($"Unknown AI task {task}.");
            var options = JsonSerializer.Deserialize<ScriptAiOptions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            calls.Add(new QueuedAiTask(kind, prompt, options));
        }));
        engine.Execute(Bootstrap).Execute(script);
        return new ScriptResult(exportPath, exportQuality);
    }

    private static void SaveExport(EditorSession session, ScriptResult result)
    {
        if (result.ExportedPath != null)
        {
            var format = ImageFiles.FormatFor(result.ExportedPath);
            using var flat = session.Flatten();
            ImageFiles.Save(flat, result.ExportedPath, format, format == ExportFormat.Png ? 100 : result.ExportQuality);
        }
    }

    private static AiTaskRequest Request(EditorSession session, QueuedAiTask call, Settings settings)
    {
        SKRectI? selection = session.Selection == null ? null : SelectionMask.Bounds(session.Selection);
        var aspect = call.Task != AiTaskKind.ChangeBackground && selection is { IsEmpty: false } bounds
            ? (bounds.Width, bounds.Height) : (session.Document.Width, session.Document.Height);
        var dimensions = AiDimensions.FromMegapixels(settings.AiMegapixels, aspect.Item1, aspect.Item2);
        var width = Math.Clamp(call.Options.Width ?? dimensions.Width, 16, DocumentLimits.MaxSide);
        var height = Math.Clamp(call.Options.Height ?? dimensions.Height, 16, DocumentLimits.MaxSide);
        var seed = call.Options.Seed is >= 0 ? call.Options.Seed.Value : Random.Shared.NextInt64(long.MaxValue);
        var expansion = call.Task == AiTaskKind.GenerativeExpand
            ? new SKRectI(call.Options.X ?? 0, call.Options.Y ?? 0, (call.Options.X ?? 0) + width, (call.Options.Y ?? 0) + height)
            : (SKRectI?)null;
        return new AiTaskRequest
        {
            Task = call.Task, Prompt = call.Prompt, ExpansionBounds = expansion,
            ReferenceMegapixels = settings.AiReferenceMegapixels,
            Settings = new AiGenerationSettings
            {
                Width = width, Height = height, Seed = seed,
                Values = new Dictionary<string, object?>
                {
                    ["maskGrow"] = settings.AiMaskGrow, ["maskBlend"] = settings.AiMaskBlend,
                    ["maskContext"] = settings.AiMaskContext,
                    ["upscaleModel"] = settings.AiUpscalerModel
                }
            }
        };
    }

    private sealed record QueuedAiTask(AiTaskKind Task, string Prompt, ScriptAiOptions Options);
    private sealed record ScriptAiOptions
    {
        public int? Width { get; init; }
        public int? Height { get; init; }
        public long? Seed { get; init; }
        public int? X { get; init; }
        public int? Y { get; init; }
    }

    public static string Describe(EditorSession session)
    {
        const int maximumLayers = 160;
        var all = session.Document.AllLayers().ToList();
        return JsonSerializer.Serialize(new
        {
            title = session.Title, width = session.Document.Width, height = session.Document.Height,
            hasSelection = session.Selection != null, activeLayerId = session.ActiveLayer?.Id.ToString(),
            layerCount = all.Count, truncated = all.Count > maximumLayers,
            layers = ContextLayers(session.Document.Layers, "").Take(maximumLayers)
        });
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
    private static Layer Find(EditorSession session, string id) => Guid.TryParse(id, out var guid) && session.Document.Find(guid) is { } layer
        ? layer : throw new InvalidOperationException($"Layer {id} no longer exists.");
    private static string DocumentJson(EditorSession session) => JsonSerializer.Serialize(new
    {
        title = session.Title, width = session.Document.Width, height = session.Document.Height,
        hasSelection = session.Selection != null, activeLayerId = session.ActiveLayer?.Id.ToString(),
        layers = session.Document.AllLayers().Select(LayerData)
    });
    private static string LayersJson(EditorSession session) => JsonSerializer.Serialize(session.Document.AllLayers().Select(LayerData));
    private static string LayerJson(Layer layer) => JsonSerializer.Serialize(LayerData(layer));
    private static object LayerData(Layer layer) => new
    {
        id = layer.Id.ToString(), name = layer.Name, kind = layer.Text != null ? "text" : layer.IsGroup ? "group" : layer.IsAdjustment ? "adjustment" : "raster",
        tags = layer.Tags.Order(StringComparer.OrdinalIgnoreCase), text = layer.Text?.Text, visible = layer.Visible, opacity = layer.Opacity,
        transform = new { x = layer.Transform.X, y = layer.Transform.Y, width = layer.Transform.Width, height = layer.Transform.Height, rotation = layer.Transform.Rotation }
    };

    private static IEnumerable<object> ContextLayers(IEnumerable<Layer> layers, string parent)
    {
        foreach (var layer in layers)
        {
            var path = string.IsNullOrEmpty(parent) ? layer.Name : parent + "/" + layer.Name;
            yield return new
            {
                id = layer.Id.ToString(), name = layer.Name, path,
                kind = layer.Text != null ? "text" : layer.IsGroup ? "group" : layer.IsAdjustment ? "adjustment" : "raster",
                tags = layer.Tags.Order(StringComparer.OrdinalIgnoreCase),
                text = layer.Text?.Text is { Length: > 500 } longText ? longText[..500] + "…" : layer.Text?.Text,
                visible = layer.Visible, opacity = layer.Opacity,
                transform = new { x = layer.Transform.X, y = layer.Transform.Y, width = layer.Transform.Width, height = layer.Transform.Height, rotation = layer.Transform.Rotation }
            };
            foreach (var child in ContextLayers(layer.Children, path)) yield return child;
        }
    }

    private const string Bootstrap = """
    (() => {
      const parse = value => JSON.parse(value);
      const wrap = data => {
        const id = data.id;
        const current = () => parse(__layerJson(id));
        return {
          id,
          get name() { return current().name; }, set name(value) { __rename(id, String(value)); },
          get kind() { return current().kind; },
          get tags() { return current().tags; }, set tags(value) { __tags(id, JSON.stringify(Array.from(value))); },
          get text() { return current().text; }, set text(value) { __text(id, String(value)); },
          get visible() { return current().visible; }, set visible(value) { __visible(id, Boolean(value)); },
          get opacity() { return current().opacity; }, set opacity(value) { __opacity(id, Number(value)); },
          get transform() { return current().transform; },
          set transform(value) {
            const old = current().transform; value = value || {};
            __transform(id, value.x ?? old.x, value.y ?? old.y, value.width ?? old.width, value.height ?? old.height, value.rotation ?? old.rotation);
          },
          select() { __selectLayer(id); },
          remove() { __deleteLayer(id); }
        };
      };
      const doc = {
        get info() { return parse(__documentJson()); },
        get width() { return this.info.width; }, get height() { return this.info.height; },
        get layers() { return parse(__layersJson()).map(wrap); },
        get activeLayer() { const value = parse(__activeLayerJson()); return value ? wrap(value) : null; },
        findLayersByTag(tag) { return parse(__findTagJson(String(tag))).map(wrap); },
        findLayersByName(name) { return parse(__findNameJson(String(name))).map(wrap); },
        addLayer(name = 'Layer') { return wrap(parse(__addLayer(String(name)))); },
        addAttachedImage(index) { return wrap(parse(__addAttachedImage(Number(index)))); },
        selectRect(x, y, width, height) { __selectRect(x, y, width, height); },
        deselect() { __deselect(); },
        export(path, quality = 90) { __export(String(path), Number(quality)); }
      };
      globalThis.app = Object.freeze({ get activeDocument() { return doc; }, get documents() { return [doc]; } });
      const queue = (task, prompt = '', options = {}) => __queueAi(task, String(prompt ?? ''), JSON.stringify(options ?? {}));
      globalThis.ai = Object.freeze({
        generateImage: (prompt, options) => queue('GenerateImage', prompt, options),
        generativeFill: (prompt, options) => queue('GenerativeFill', prompt, options),
        removeObject: (options) => queue('RemoveObject', '', options),
        generativeExpand: (prompt, options) => queue('GenerativeExpand', prompt, options),
        changeBackground: (prompt, options) => queue('ChangeBackground', prompt, options),
        harmonize: (prompt, options) => queue('Harmonize', prompt, options),
        matchToScene: (options) => queue('MatchToScene', '', options),
        relight: (prompt, options) => queue('Relight', prompt, options),
        upscale: (options) => queue('Upscale', '', options),
        selectSubject: (options) => queue('SelectSubject', '', options),
        objectSelection: (prompt, options) => queue('ObjectSelection', prompt, options)
      });
    })();
    """;
}
