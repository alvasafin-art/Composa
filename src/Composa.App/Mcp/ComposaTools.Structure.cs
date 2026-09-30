using System.ComponentModel;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "set_text")]
    [Description("Edits an existing LIVE text layer without flattening it. Omitted fields keep their values. The layer can be addressed by name or id.")]
    public Task<string> SetText(string layer, string? text = null, double? size = null, string? color = null,
        string? font = null, bool? bold = null, bool? italic = null, int? document = null) => OnUi(() =>
    {
        var s = Editable(document); var target = Find(s, layer);
        var style = target.Text ?? throw new McpException("This is not a live text layer.");
        if (text != null)
        {
            if (text.Length > TextStyle.MaxLength) throw new McpException($"Text is limited to {TextStyle.MaxLength} characters.");
            style = style.WithReplacedCharacters(0, style.Text.Length, text.Length) with { Text = text };
        }
        if (size is { } pt && (!double.IsFinite(pt) || pt < 1 || pt > 2000)) throw new McpException("Text size must be 1 to 2000.");
        style = style with { Size = size ?? style.Size };
        if (font != null || bold != null || italic != null)
            style = style.WithFace(face => face with { FontFamily = font ?? face.FontFamily, Bold = bold ?? face.Bold, Italic = italic ?? face.Italic }, 0, style.Text.Length);
        if (color != null) style = style.WithColor((uint)ParseColor(color), 0, style.Text.Length);
        var layout = new TextLayout(style);
        CheckRasterAllocation(s,layout.Width,layout.Height,(long)target.Pixels!.Width*target.Pixels.Height);
        s.Apply("Edit Text", () => s.SetText(target, style));
        return $"Updated live text [{Id(target)}]: {target.Text!.Text}";
    });

    [McpServerTool(Name = "group_layers")]
    [Description("Groups the named layers, preserving their visual stacking order. Returns the active folder's id. Do not include both a folder and its descendants.")]
    public Task<string> GroupLayers(string[] layers, string name = "Folder", int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        if (layers.Length == 0) throw new McpException("Give at least one layer.");
        var targets = layers.Select(layer => Find(s, layer)).Distinct().ToArray();
        if (targets.Any(parent => targets.Any(child => parent != child && parent.Children.SelectMany(Descendants).Contains(child))))
            throw new McpException("Do not group both a folder and its descendant.");
        s.RunTransaction("Group Layers", _ =>
        {
            s.SelectLayer(targets[0].Id);
            foreach (var target in targets.Skip(1)) s.SelectLayer(target.Id, extend: true);
            s.GroupSelectedLayers(); s.Rename(s.ActiveLayer!, name);
        });
        return $"Created group \"{s.ActiveLayer!.Name}\" [{Id(s.ActiveLayer)}].";
    });
    private static IEnumerable<Layer> Descendants(Layer layer) => new[] { layer }.Concat(layer.Children.SelectMany(Descendants));

    [McpServerTool(Name = "layer_mask")]
    [Description("Manages a layer's mask: add (uses current selection, otherwise reveals all), hide_all, delete, apply (bake into pixel alpha), enable or disable. Selection and layer masks are separate.")]
    public Task<string> LayerMask(string layer, string action, int? document = null) => OnUi(() =>
    {
        var s = Editable(document); var target = Find(s, layer);
        if (target.Mask == null && action.ToLowerInvariant() is "add" or "hide_all")
            CheckRasterAllocation(s,target.Pixels?.Width ?? s.Document.Width,target.Pixels?.Height ?? s.Document.Height);
        switch (action.ToLowerInvariant())
        {
            case "add": s.AddMask(target); break;
            case "hide_all":
                if (s.Selection != null) throw new McpException("Deselect first for hide_all; add uses the current selection.");
                s.AddMask(target, true); break;
            case "delete": s.DeleteMask(target); break;
            case "apply":
                if (target.Pixels == null) throw new McpException("A group/adjustment mask cannot be baked into pixels.");
                s.ApplyMask(target); break;
            case "enable": s.SetMaskEnabled(target, true); break;
            case "disable": s.SetMaskEnabled(target, false); break;
            default: throw new McpException("action: add, hide_all, delete, apply, enable or disable.");
        }
        s.EditingMask = false;
        return $"Mask {action}: \"{target.Name}\"; hasMask={target.Mask != null}, enabled={target.MaskEnabled}.";
    });

    [McpServerTool(Name = "rasterize_layer")]
    [Description("Converts live text/shape to pixels for painting; preserves appearance and frame. Undo restores editable text/shape.")]
    public Task<string> RasterizeLayer(string layer, int? document = null) => OnUi(() =>
    {
        var s = Editable(document); var target = Find(s, layer);
        if (!target.IsLive) throw new McpException("Rasterize requires live text or shape.");
        s.RasterizeShape(target); return $"Rasterized \"{target.Name}\".";
    });

    [McpServerTool(Name = "resize_document")]
    [Description("Resizes the image (scale all layers) or canvas (preserve layer pixels). mode: image or canvas. Canvas anchor: Center, TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight.")]
    public Task<string> ResizeDocument(int width, int height, string mode = "image", string anchor = "Center", int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        if (!DocumentLimits.FitsSurface(width, height)) throw new McpException($"Size exceeds {DocumentLimits.MaxSide}px / {DocumentLimits.MaxSurfaceMegapixels} MP.");
        if (mode == "image")
        {
            var sx=(double)width/s.Document.Width; var sy=(double)height/s.Document.Height; long projected=0;
            foreach (var layer in s.Document.AllLayers())
            {
                if (layer.Pixels is { } pixels)
                {
                    var w=(long)Math.Ceiling(Math.Max(pixels.Width,layer.Bounds.Width)*sx);
                    var h=(long)Math.Ceiling(Math.Max(pixels.Height,layer.Bounds.Height)*sy);
                    if (!DocumentLimits.FitsSurface(w,h)) throw new McpException($"A resized layer exceeds {DocumentLimits.MaxSide}px / {DocumentLimits.MaxSurfaceMegapixels} MP.");
                    projected+=w*h*(layer.Mask==null ? 1 : 2);
                }
                else if (layer.Mask != null) projected+=(long)width*height;
                if (projected>DocumentLimits.DocumentPixelBudget) throw new McpException($"Resizing exceeds the {DocumentLimits.DocumentBudgetMegapixels} MP document budget.");
            }
            s.ResizeImage(width, height);
        }
        else if (mode == "canvas" && Enum.TryParse<Anchor>(anchor, true, out var position) && Enum.IsDefined(position)) s.ResizeCanvas(width, height, position);
        else throw new McpException("mode is image or canvas; use a valid anchor.");
        return $"Document: {s.Document.Width}×{s.Document.Height}; {mode}.";
    });
}
