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
public sealed partial class JavaScriptRuntime : IScriptRuntime
{
    public string Language => "JavaScript";
    public const string Reference = """
    const doc = app.activeDocument;
    doc.info;                         // { title, width, height, hasSelection, selection, activeLayerId, layers }
    doc.layers;                       // ordered Layer[]
    doc.activeLayer;                  // Layer | null
    doc.selection;                    // { x, y, width, height } | null, read-only selection bounds
    doc.findLayersByTag("title");     // Layer[]; prefer this over guessing names
    doc.findLayersByName("Heading"); // Layer[]
    doc.addLayer("Name");            // Layer
    doc.addAttachedImage(index);     // place an explicitly attached chat image as a new layer
    doc.selectRect(x, y, width, height); doc.deselect();
    doc.export("C:/output/image.webp", 90); // only when the user explicitly requests export

    // READ-ONLY: layer.id, layer.kind. Never assign kind, pixels or arbitrary properties.
    // Assignable layer properties:
    layer.name; layer.tags; layer.text; layer.visible; layer.opacity; layer.transform;
    layer.name = "New name";
    layer.text = "Winter Sale";       // editable text layers only
    layer.tags = ["title", "editable"];
    layer.opacity = 0.8; layer.visible = true;
    layer.transform = { x: 10, y: 20, width: 400, height: 300, rotation: 0 };
    layer.select(); layer.remove();
    layer.fill("#87CEEB"); layer.duplicate(); layer.moveBy(20, 20); layer.blendMode = "Multiply";
    doc.addRectangle(40, 40, 240, 120, "#87CEEB", "Blue Rectangle"); // creates a new live shape layer
    doc.addEllipse(40, 40, 120, 120, "#FF0000", "Circle");
    doc.addShape({ kind: "rounded", x: 40, y: 40, width: 240, height: 120, color: "#87CEEB", cornerRadius: 20, name: "Card" });
    doc.addLine(0, 0, 200, 200, "#000000", 4);
    doc.addText("Hello", 40, 80, { size: 48, color: "#000000", fontFamily: "Inter", bold: true, name: "Title" });
    doc.paintStroke([{x:10,y:10},{x:80,y:90}], { color: "#000000", size: 20, hardness: 1, opacity: 1 });
    doc.fill("#FFFFFF"); doc.selectEllipse(x, y, width, height); doc.selectAll(); doc.invertSelection();
    doc.resizeImage(width, height); doc.resizeCanvas(width, height, "Center");
    doc.groupLayers([layer.id, otherLayer.id], "Group");

    // AI calls are queued after local edits and use the selected Engine Pack. Do local edits FIRST.
    ai.generativeFill("replace with flowers"); ai.removeObject(); ai.upscale();
    ai.changeBackground("sunset studio"); ai.harmonize("match the scene lighting");
    // Optional settings: { width, height, seed, x, y } (x/y are expansion origin).
    """;

    public ScriptResult Execute(EditorSession session, string script, string transactionName = "Run Script", CancellationToken cancellationToken = default)
    {
        var calls = new List<QueuedAiTask>();
        ScriptResult result = new();
        session.RunTransaction(transactionName, editor =>
        { result = ExecuteScript(editor, script, calls, allowAi: false, cancellationToken); SaveExport(session, result); });
        return result;
    }

    public async Task<ScriptResult> ExecuteAsync(EditorSession session, string script, IAiTaskRunner aiRunner, Settings settings,
        string transactionName = "Run Script", CancellationToken cancellationToken = default, IReadOnlyList<string?>? attachedImages = null, bool allowExport = true)
    {
        var calls = new List<QueuedAiTask>();
        ScriptResult result = new();
        await session.RunTransactionAsync(transactionName, async editor =>
        {
            result = ExecuteScript(editor, script, calls, allowAi: true, cancellationToken, attachedImages, allowExport);
            foreach (var call in calls)
                await aiRunner.RunAsync(new EditorCommandService(editor), Request(editor, call, settings), cancellationToken);
            SaveExport(session, result);
        });
        return result;
    }

    private static ScriptResult ExecuteScript(EditorSession editor, string script, List<QueuedAiTask> calls, bool allowAi, CancellationToken cancellationToken,
        IReadOnlyList<string?>? attachedImages = null, bool allowExport = true)
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
        AddEditorApi(engine, editor);
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
            if (text.Length > TextStyle.MaxLength) throw new ArgumentException($"Text must be at most {TextStyle.MaxLength} characters.");
            var style = layer.Text.WithReplacedCharacters(0, layer.Text.Text.Length, text.Length) with { Text = text };
            CheckText(editor, style); editor.SetText(layer, style);
        }));
        engine.SetValue("__transform", (Action<string, double, double, double, double, double>)((id, x, y, width, height, rotation) =>
        {
            var layer = Find(editor, id);
            if (layer.Pixels == null) throw new InvalidOperationException("Transform fields require a raster, shape or text layer. Use layer.moveBy(dx,dy) to move a group.");
            Frame(x, y, Math.Max(1, Finite(width, layer.Transform.Width)), Math.Max(1, Finite(height, layer.Transform.Height)));
            if (layer.IsLive) CheckRaster(editor, width, height);
            editor.SetTransform(layer, layer.Transform with
            {
                X = Finite(x, layer.Transform.X), Y = Finite(y, layer.Transform.Y),
                Width = Math.Max(1, Finite(width, layer.Transform.Width)), Height = Math.Max(1, Finite(height, layer.Transform.Height)),
                Rotation = Finite(rotation, layer.Transform.Rotation)
            });
        }));
        engine.SetValue("__selectRect", (Action<double, double, double, double>)((x, y, width, height) =>
            editor.SelectRect(Frame(x, y, width, height))));
        engine.SetValue("__deselect", (Action)editor.Deselect);
        engine.SetValue("__export", (Action<string, int>)((path, quality) =>
        {
            if (!allowExport) throw new InvalidOperationException("This plugin has no export permission.");
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
        engine.Execute(Bootstrap).Execute(EditorBootstrap).Execute(script);
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
            hasSelection = session.Selection != null, selection = SelectionData(session), activeLayerId = session.ActiveLayer?.Id.ToString(),
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
        hasSelection = session.Selection != null, selection = SelectionData(session), activeLayerId = session.ActiveLayer?.Id.ToString(),
        layers = session.Document.AllLayers().Select(LayerData)
    });
    private static string LayersJson(EditorSession session) => JsonSerializer.Serialize(session.Document.AllLayers().Select(LayerData));
    private static string LayerJson(Layer layer) => JsonSerializer.Serialize(LayerData(layer));
    private static object LayerData(Layer layer) => new
    {
        id = layer.Id.ToString(), name = layer.Name, kind = LayerKind(layer),
        tags = layer.Tags.Order(StringComparer.OrdinalIgnoreCase), text = layer.Text?.Text, visible = layer.Visible, opacity = layer.Opacity, blendMode = layer.Blend.ToString(),
        transform = new { x = layer.Transform.X, y = layer.Transform.Y, width = layer.Transform.Width, height = layer.Transform.Height, rotation = layer.Transform.Rotation }
    };

    private static string LayerKind(Layer layer) => layer.Text != null ? "text" : layer.Shape != null ? "shape"
        : layer.IsGroup ? "group" : layer.IsAdjustment ? "adjustment" : "raster";

    private static object? SelectionData(EditorSession session)
    {
        if (session.Selection == null) return null;
        var bounds = SelectionMask.Bounds(session.Selection);
        return new { x = bounds.Left, y = bounds.Top, width = bounds.Width, height = bounds.Height };
    }

    private static IEnumerable<object> ContextLayers(IEnumerable<Layer> layers, string parent)
    {
        foreach (var layer in layers)
        {
            var path = string.IsNullOrEmpty(parent) ? layer.Name : parent + "/" + layer.Name;
            yield return new
            {
                id = layer.Id.ToString(), name = layer.Name, path,
                kind = LayerKind(layer),
                tags = layer.Tags.Order(StringComparer.OrdinalIgnoreCase),
                text = layer.Text?.Text is { Length: > 500 } longText ? longText[..500] + "…" : layer.Text?.Text,
                fontSize = layer.Text?.Size, fontFamily = layer.Text?.FontFamily,
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
        return Object.preventExtensions({
          get id() { return id; },
          get name() { return current().name; }, set name(value) { __rename(id, String(value)); },
          get kind() { return current().kind; },
          get blendMode() { return current().blendMode; }, set blendMode(value) { __blend(id, String(value)); },
          get tags() { return current().tags; }, set tags(value) { __tags(id, JSON.stringify(Array.from(value))); },
          get text() { return current().text; }, set text(value) { __text(id, String(value)); },
          get visible() { return current().visible; }, set visible(value) { __visible(id, Boolean(value)); },
          get opacity() { return current().opacity; }, set opacity(value) { __opacity(id, Number(value)); },
          get transform() {
            const value = {};
            for (const key of ['x','y','width','height','rotation'])
              Object.defineProperty(value,key,{enumerable:true,get:()=>current().transform[key],set:next=>{
                const old=current().transform; old[key]=Number(next);
                __transform(id,old.x,old.y,old.width,old.height,old.rotation);
              }});
            return value;
          },
          set transform(value) {
            const old = current().transform; value = value || {};
            __transform(id, value.x ?? old.x, value.y ?? old.y, value.width ?? old.width, value.height ?? old.height, value.rotation ?? old.rotation);
          },
          select() { __selectLayer(id); },
          fill(color) { __fillLayer(id, String(color)); },
          duplicate() { return wrap(parse(__duplicateLayer(id))); },
          moveBy(dx,dy) { __moveLayer(id,Number(dx),Number(dy)); },
          remove() { __deleteLayer(id); }
        });
      };
      globalThis.__composaWrap = wrap;
      const doc = {
        get info() { return parse(__documentJson()); },
        get width() { return this.info.width; }, get height() { return this.info.height; },
        get selection() { return this.info.selection; },
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
