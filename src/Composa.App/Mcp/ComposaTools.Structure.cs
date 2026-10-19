using System.ComponentModel;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using Composa.ColorManagement;
using Composa.App.Automation;
using Composa.IO;
using Composa.IO.Psd;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "set_color_profile")]
    [Description("Assign or convert the document's RGB ICC profile as one undoable edit. profile: sRGB, Display P3, RGB (1998), or an explicit ICC/ICM file path. Assign keeps raster numbers. Convert preserves color within gamut; flatten=true preserves the composite, while false keeps editable layers and may change blends/adjustments. Embedded smart contents retain their profiles.")]
    public Task<string> SetColorProfile(string profile, string mode = "convert", bool flatten = true, int? document = null) => OnUi(() =>
    {
        if (mode is not ("assign" or "convert")) throw new McpException("mode is assign or convert.");
        var destination = RgbProfile.BuiltIn.FirstOrDefault(p => p.Name.Equals(profile, StringComparison.OrdinalIgnoreCase));
        if (destination == null)
        {
            if (Path.GetExtension(profile).ToLowerInvariant() is not (".icc" or ".icm")) throw new McpException("Use sRGB, Display P3, RGB (1998), or an ICC/ICM file path.");
            var file = new FileInfo(profile);
            if (!file.Exists || file.Length > DocumentLimits.MaxIccBytes) throw new McpException("The ICC file is missing or exceeds the profile size limit.");
            destination = RgbProfile.FromIcc(File.ReadAllBytes(profile));
        }
        var session = Editable(document);
        if (mode == "assign") session.AssignColorProfile(destination); else session.ConvertColorProfile(destination, flatten);
        return $"RGB profile: {session.Document.Profile.Name}. {mode}.";
    });

    [McpServerTool(Name = "set_color_depth")]
    [Description("Converts the document to RGB 8 or 16 bits/channel as one undoable edit. Embedded smart-object originals retain their own precision. Conversion to 8 bits rounds working pixel values.")]
    public Task<string> SetColorDepth(int bitDepth, int? document = null) => OnUi(() =>
    {
        if (bitDepth is not (8 or 16)) throw new McpException("bitDepth must be 8 or 16.");
        var session = Editable(document); session.ChangeColorDepth((ColorDepth)bitDepth);
        return $"Document uses RGB {bitDepth} bits/channel.";
    });

    [McpServerTool(Name = "smart_object")]
    [Description("Smart objects: convert layer(s), copy one independent source, place a file (linked=true by default), replace contents, relink to a file, update from the linked file, embed or info. All except place/convert take one layer in layers. path is required for place/replace/relink. Ordinary duplicate_layer shares the source; updates affect every shared instance and preserve placement/masks/filters. Files remain usable from cache when offline. Source files are never written by this tool. PSD and RAW file operations need the File menu's review.")]
    public Task<string> SmartObject(string action, string[]? layers = null, int? document = null, string? path = null, bool linked = true,
        bool discardLocal = false) => OnUi(async () =>
    {
        var s = action == "info" ? Session(document) : Editable(document);
        if (action is not ("convert" or "copy" or "place" or "replace" or "relink" or "update" or "embed" or "info")) throw new McpException("Use convert, copy, place, replace, relink, update, embed or info.");
        var targets = (layers ?? []).Select(layer => Find(s, layer)).Distinct().ToArray();
        if (action != "place" && targets.Length == 0) throw new McpException("Give at least one layer.");
        if (action is not ("place" or "convert") && targets.Length != 1) throw new McpException("This action takes one smart object.");
        if (action is "place" or "replace" or "relink")
        {
            path = Absolute(path ?? throw new McpException("Give the source file's absolute path."));
            if (PsdImport.IsPsd(path) || RawImporter.IsRaw(path)) throw new McpException("Photoshop and camera RAW sources need review; use the File menu.");
        }
        Layer result;
        if (action == "place") result = await window.PlaceSmartObjectPath(path!, linked, s, review: false) ?? throw new McpException("Source was not placed.");
        else if (action == "convert")
        {
            s.SelectLayer(targets[0].Id);
            foreach (var target in targets.Skip(1)) s.SelectLayer(target.Id, extend: true);
            result = s.ConvertToSmartObject();
        }
        else if (action == "copy" && targets.Length == 1) result = s.DuplicateSmartObjectIndependent(targets[0]);
        else
        {
            result = targets[0]; var source = result.SmartObject ?? throw new McpException("Select a smart object.");
            if (action == "info")
            {
                var status = source.Link == null ? null : await Task.Run(() => LinkedSmartObjectFile.Probe(source.Link, verifyHash: true));
                return $"Smart source {source.Id}: {source.Width}×{source.Height} px. " + (source.Link == null ? "Embedded." : $"Linked: {source.Link.Path}. {status!.State}. {status.Detail}");
            }
            if (action == "embed") s.SetSmartObjectLink(source, null);
            else if (action is "replace" or "relink") await window.ReplaceSmartObjectPath(result, path!, action == "relink", s, review: false);
            else if (action == "update")
            {
                if (source.Link == null) throw new McpException("This smart object is embedded.");
                if (PsdImport.IsPsd(source.Link.Path) || RawImporter.IsRaw(source.Link.Path)) throw new McpException("Photoshop and camera RAW sources need review; use the File menu.");
                await window.UpdateLinkedSmartObject(s, source, review: false, discardLocal: discardLocal);
            }
        }
        return $"Smart object \"{result.Name}\" [{Id(result)}], content {result.SmartObject!.Width}×{result.SmartObject.Height} px, source {result.SmartObject.Id}.";
    });

    [McpServerTool(Name = "set_text")]
    [Description("Edits an existing LIVE text layer without flattening it. Omitted fields keep their values. The layer can be addressed by name or id.")]
    public Task<string> SetText(string layer, string? text = null, double? size = null, string? color = null,
        string? font = null, bool? bold = null, bool? italic = null, int? document = null, double? boxWidth = null, double? boxHeight = null, bool fitToCanvas = true) => OnUi(() =>
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
        style = style with { BoxWidth = boxWidth ?? style.BoxWidth, BoxHeight = boxHeight ?? style.BoxHeight };
        style = AutomationText.PrepareUpdate(s, target, style, fitToCanvas);
        var layout = new TextLayout(style);
        CheckRasterAllocation(s,layout.Width,layout.Height,(long)target.Pixels!.Width*target.Pixels.Height);
        s.RunTransaction("Edit Text", _ => { s.SetText(target, style); if (fitToCanvas) AutomationText.KeepInside(s, target); });
        return $"Updated live text [{Id(target)}], font {style.Size:0.##}, bounds {target.Bounds}: {target.Text!.Text}";
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
    [Description("Manages a layer's mask: add (uses current selection, otherwise reveals all), hide_all, delete, apply (bake into pixel alpha), enable, disable, link or unlink (preserves the mask's document position). Selection and layer masks are separate.")]
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
            case "link": s.SetMaskLinked(target, true); break;
            case "unlink": s.SetMaskLinked(target, false); break;
            default: throw new McpException("action: add, hide_all, delete, apply, enable, disable, link or unlink.");
        }
        s.EditingMask = false;
        return $"Mask {action}: \"{target.Name}\"; hasMask={target.Mask != null}, enabled={target.MaskEnabled}, linked={target.MaskLinked}.";
    });

    [McpServerTool(Name = "rasterize_layer")]
    [Description("Converts live text/shape/smart object to pixels for painting; preserves appearance and frame. Undo restores editable content.")]
    public Task<string> RasterizeLayer(string layer, int? document = null) => OnUi(() =>
    {
        var s = Editable(document); var target = Find(s, layer);
        if (!target.IsLive) throw new McpException("Rasterize requires live text, shape or smart object.");
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
