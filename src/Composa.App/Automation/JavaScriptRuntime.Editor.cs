using System.Text.Json;
using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Text;
using Jint;
using SkiaSharp;

namespace Composa.App.Automation;

public sealed partial class JavaScriptRuntime
{
    private static readonly JsonSerializerOptions ScriptJson = new() { PropertyNameCaseInsensitive = true };

    private static void AddEditorApi(Engine engine, EditorSession editor)
    {
        engine.SetValue("__shape", (Func<string, string>)(json =>
        {
            var options = JsonSerializer.Deserialize<ShapeOptions>(json, ScriptJson) ?? throw new ArgumentException("Shape options are required.");
            var kind = options.Kind.ToLowerInvariant() switch
            { "rectangle" => ShapeKind.Rectangle, "ellipse" or "circle" => ShapeKind.Ellipse,
                "rounded" or "roundedrectangle" => ShapeKind.RoundedRectangle, _ => throw new ArgumentException("Shape kind is rectangle, rounded or ellipse.") };
            var rect = Frame(options.X, options.Y, options.Width, options.Height);
            CheckRaster(editor, options.Width, options.Height);
            var layer = editor.AddShape(new ShapeStyle(kind, (uint)Color(options.Color), options.CornerRadius), rect)
                ?? throw new InvalidOperationException("The shape could not be created.");
            if (options.Name != null) editor.Rename(layer, options.Name);
            return LayerJson(layer);
        }));
        engine.SetValue("__line", (Func<double, double, double, double, string, double, string>)((x1, y1, x2, y2, color, width) =>
        {
            if (!double.IsFinite(width) || width <= 0 || width > DocumentLimits.MaxSide) throw new ArgumentException("Line width must be positive and finite.");
            var box = Frame(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + width, Math.Abs(y2 - y1) + width);
            CheckRaster(editor, box.Width, box.Height);
            return LayerJson(editor.AddLine(new SKPoint((float)x1, (float)y1), new SKPoint((float)x2, (float)y2), Color(color), width)
                ?? throw new InvalidOperationException("The line could not be created."));
        }));
        engine.SetValue("__textLayer", (Func<string, double, double, string, string>)((text, x, y, json) =>
        {
            Frame(x, y, 1, 1);
            var options = JsonSerializer.Deserialize<TextOptions>(json, ScriptJson) ?? new();
            if (text.Length > TextStyle.MaxLength) throw new ArgumentException($"Text must be at most {TextStyle.MaxLength} characters.");
            var style = new TextStyle
            { Text = text, Size = Math.Clamp(options.Size, 1, 5000), Color = (uint)Color(options.Color),
                FontFamily = options.FontFamily, Bold = options.Bold, Italic = options.Italic };
            CheckText(editor, style);
            var layer = editor.AddText(new SKPoint((float)x, (float)y), style);
            if (options.Name != null) editor.Rename(layer, options.Name);
            return LayerJson(layer);
        }));
        engine.SetValue("__fill", (Action<string>)(color => { RequirePaintable(editor); editor.Fill(Color(color)); }));
        engine.SetValue("__fillLayer", (Action<string, string>)((id, color) =>
        { editor.SelectLayer(Find(editor, id).Id); RequirePaintable(editor); editor.Fill(Color(color)); }));
        engine.SetValue("__duplicateLayer", (Func<string, string>)(id =>
        {
            editor.SelectLayer(Find(editor, id).Id); editor.DuplicateSelectedLayers();
            return LayerJson(editor.ActiveLayer!);
        }));
        engine.SetValue("__blend", (Action<string, string>)((id, value) =>
        {
            if (!Enum.TryParse<BlendMode>(value.Replace(" ", ""), true, out var blend) || !Enum.IsDefined(blend))
                throw new ArgumentException("Unknown blend mode: " + value);
            editor.SetBlend(Find(editor, id), blend);
        }));
        engine.SetValue("__moveLayer", (Action<string, double, double>)((id, dx, dy) =>
        {
            Frame(dx, dy, 1, 1); editor.SelectLayer(Find(editor, id).Id);
            var transform = editor.BeginTransform("Move Layer") ?? throw new InvalidOperationException("The layer has no movable content.");
            transform.MoveBy((float)dx, (float)dy); editor.CommitTransform();
        }));
        engine.SetValue("__paint", (Action<string, string>)((pointsJson, optionsJson) =>
        {
            RequirePaintable(editor);
            var points = JsonSerializer.Deserialize<StrokePoint[]>(pointsJson, ScriptJson) ?? [];
            if (points.Length is < 1 or > 10000) throw new ArgumentException("A stroke needs 1–10,000 points.");
            foreach (var point in points) Frame(point.X, point.Y, 1, 1);
            var options = JsonSerializer.Deserialize<PaintOptions>(optionsJson, ScriptJson) ?? new();
            var problem = editor.PaintStroke(points.Select(point => new SKPoint((float)point.X, (float)point.Y)).ToArray(),
                new BrushSettings { Size = Math.Clamp(options.Size, 1, 5000), Hardness = Math.Clamp(options.Hardness, 0, 1), Opacity = Math.Clamp(options.Opacity, 0, 1) }, Color(options.Color));
            if (problem != null) throw new InvalidOperationException(problem);
        }));
        engine.SetValue("__ellipseSelection", (Action<double, double, double, double>)((x, y, width, height) => editor.SelectEllipse(Frame(x, y, width, height))));
        engine.SetValue("__selectAll", (Action)editor.SelectAll);
        engine.SetValue("__inverse", (Action)editor.InvertSelection);
        engine.SetValue("__resize", (Action<int, int, string>)((width, height, anchor) =>
        {
            if (width < 1 || height < 1 || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide || (long)width * height > DocumentLimits.MaxSurfacePixels)
                throw new ArgumentException($"Canvas dimensions exceed the {DocumentLimits.MaxSide} px / {DocumentLimits.MaxSurfaceMegapixels} MP limit.");
            if (anchor == "image") editor.ResizeImage(width, height);
            else if (Enum.TryParse<Anchor>(anchor, true, out var parsed) && Enum.IsDefined(parsed)) editor.ResizeCanvas(width, height, parsed);
            else throw new ArgumentException("Unknown canvas anchor: " + anchor);
        }));
        engine.SetValue("__group", (Func<string, string, string>)((idsJson, name) =>
        {
            var ids = JsonSerializer.Deserialize<string[]>(idsJson) ?? [];
            if (ids.Length == 0) throw new ArgumentException("Choose layers to group.");
            var selected = ids.Distinct().Select(id => Find(editor, id)).ToArray();
            editor.SelectLayer(selected[0].Id);
            foreach (var layer in selected.Skip(1)) editor.SelectLayer(layer.Id, extend: true);
            editor.GroupSelectedLayers(); editor.Rename(editor.ActiveLayer!, name);
            return LayerJson(editor.ActiveLayer!);
        }));
    }

    private static void RequirePaintable(EditorSession editor)
    {
        if (!editor.CanEditPixels) throw new InvalidOperationException("Choose an editable raster layer before painting or filling.");
    }

    private static SKColor Color(string value) => SKColor.TryParse(value, out var color) ? color
        : throw new ArgumentException("Use a color such as #87CEEB or #FF87CEEB (alpha first).");

    private static SKRect Frame(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || Math.Abs(x) > DocumentLimits.MaxSide || Math.Abs(y) > DocumentLimits.MaxSide || width < 1 || height < 1
            || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide)
            throw new ArgumentException($"Use finite coordinates and dimensions from 1 to {DocumentLimits.MaxSide} pixels.");
        return SKRect.Create((float)x, (float)y, (float)width, (float)height);
    }

    private static void CheckRaster(EditorSession editor, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1
            || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide)
            throw new InvalidOperationException($"Layer dimensions exceed the {DocumentLimits.MaxSide} px limit.");
        var pixels = (long)Math.Ceiling(width) * (long)Math.Ceiling(height);
        if (pixels > DocumentLimits.MaxSurfacePixels || pixels > DocumentLimits.DocumentPixelBudget - editor.Document.RasterPixels())
            throw new InvalidOperationException("This edit exceeds the document's raster memory budget.");
    }

    private static void CheckText(EditorSession editor, TextStyle style)
    {
        var layout = new TextLayout(style);
        CheckRaster(editor, layout.Width, layout.Height);
    }

    private sealed record ShapeOptions(string Kind = "rectangle", double X = 0, double Y = 0, double Width = 100,
        double Height = 100, string Color = "#000000", double CornerRadius = 24, string? Name = null);
    private sealed record TextOptions(double Size = 48, string Color = "#000000", string FontFamily = "Inter", bool Bold = false, bool Italic = false, string? Name = null);
    private sealed record PaintOptions(string Color = "#000000", double Size = 20, double Hardness = 1, double Opacity = 1);
    private sealed record StrokePoint(double X, double Y);

    private const string EditorBootstrap = """
    (() => {
      const doc = app.activeDocument;
      const wrap = json => __composaWrap(JSON.parse(json));
      doc.addShape = options => wrap(__shape(JSON.stringify(options)));
      doc.addRectangle = (x,y,width,height,color='#000000',name) => doc.addShape({kind:'rectangle',x,y,width,height,color,name});
      doc.addEllipse = (x,y,width,height,color='#000000',name) => doc.addShape({kind:'ellipse',x,y,width,height,color,name});
      doc.addLine = (x1,y1,x2,y2,color='#000000',width=4) => wrap(__line(x1,y1,x2,y2,String(color),width));
      doc.addText = (text,x,y,options={}) => wrap(__textLayer(String(text),x,y,JSON.stringify(options)));
      doc.fill = color => __fill(String(color));
      doc.paintStroke = (points,options={}) => __paint(JSON.stringify(points),JSON.stringify(options));
      doc.selectEllipse = (x,y,w,h) => __ellipseSelection(x,y,w,h);
      doc.selectAll = () => __selectAll(); doc.invertSelection = () => __inverse();
      doc.resizeImage = (w,h) => __resize(w,h,'image');
      doc.resizeCanvas = (w,h,anchor='Center') => __resize(w,h,String(anchor));
      doc.groupLayers = (layers,name='Group') => wrap(__group(JSON.stringify(layers.map(layer => typeof layer === 'string' ? layer : layer.id)),String(name)));
    })();
    """;
}
