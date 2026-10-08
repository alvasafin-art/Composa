using System.ComponentModel;
using Composa.Editing;
using Composa.IO;
using Composa.IO.Psd;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

/// <summary>
/// Files: opening, saving and exporting. These go through the window's own file commands with the dialogs cut out, so
/// a save from an agent is written in the background exactly as Ctrl+S is and close and quit wait for it. A file the
/// document did not come from is never overwritten unless the agent says so.
/// </summary>
public sealed partial class ComposaTools
{
    [McpServerTool(Name = "open_document")]
    [Description("Opens a file in a new tab and makes it the active document: a Composa project (.cmps) or an image (PNG, JPEG, WebP, BMP, GIF, SVG, and HEIC, AVIF or TIFF when ImageMagick is available). A file already open just becomes the active document. Photoshop and camera RAW files need a dialog, so they are opened from the File menu instead.")]
    public async Task<string> OpenDocument([Description("Absolute path of the file")] string path)
    {
        path = Absolute(path);
        if (!File.Exists(path)) throw new McpException($"There is no file at {path}.");
        if (PsdImport.IsPsd(path) || RawImporter.IsRaw(path)) throw new McpException("Photoshop and camera RAW files need a dialog; open them from the File menu.");
        return await OnUi(async () =>
        {
            if (window.IsDragging) throw new McpException("The person is dragging on the canvas; try again in a moment.");
            var before = window.Sessions.Count;
            EditorSession s;
            try { s = await window.OpenPath(path) ?? throw new McpException($"{Path.GetFileName(path)} was not opened."); }
            catch (Exception error) when (error is not McpException) { throw new McpException($"Couldn't open {Path.GetFileName(path)}: {error.Message}"); }
            var number = window.Sessions.ToList().IndexOf(s) + 1;
            if (imageExchange) WindowActivation.Show(window);
            return window.Sessions.Count == before
                ? $"Document {number} \"{s.Title}\" was already open, now active."
                : $"Opened document {number}: \"{s.Title}\" {s.Document.Width}×{s.Document.Height} px, {s.Document.AllLayers().Count()} layers, now active.";
        });
    }

    [McpServerTool(Name = "save_document")]
    [Description("Saves the document as a Composa project (.cmps) or a layered Photoshop document (.psd), in the background as Ctrl+S does. Without a path it saves to the file the document came from.")]
    public Task<string> SaveDocument(
        [Description("Absolute path ending in .cmps or .psd; leave it out to save to the document's own file")] string? path = null,
        [Description("Replace a file that exists at a new path; the document's own file is always replaced")] bool overwrite = false,
        int? document = null,
        [Description("Allow PSD conversion of live content, effects and adjustments to pixels; save .cmps to keep all settings")] bool allowConversion = false) => OnUi(async () =>
    {
        var s = Editable(document);                             // Text being typed is committed first, so it is in what gets saved.
        if (path == null)
        {
            path = s.FilePath ?? throw new McpException($"\"{s.Title}\" has never been saved; give a path.");
        }
        else
        {
            path = Absolute(path);
            if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase) && !path.EndsWith(PsdExport.Extension, StringComparison.OrdinalIgnoreCase))
                throw new McpException($"A project file ends in {ProjectFile.Extension} or .psd; export_image writes an image file.");
            Fresh(path, s.FilePath, overwrite);
        }
        var conversions = Path.GetExtension(path).Equals(PsdExport.Extension, StringComparison.OrdinalIgnoreCase) ? PsdExport.Conversions(s.Document) : [];
        if (conversions.Count > 0 && !allowConversion)
            throw new McpException("PSD conversion requires allowConversion: true. " + string.Join(" ", conversions.Select(c => c.LayerName + ": " + c.Message)));
        if (await window.SaveTo(s, path) is { } error) throw new McpException($"Couldn't save {Path.GetFileName(path)}: {error.Message}");
        return $"Saved \"{s.Title}\" to {path}." + (conversions.Count == 0 ? "" : "\n" + string.Join("\n", conversions.Select(c => c.LayerName + ": " + c.Message)));
    });

    [McpServerTool(Name = "export_image")]
    [Description("Exports the document flattened to an image file: PNG, JPEG or WebP, by the path's extension. The document itself is untouched; save_document keeps the layers.")]
    public async Task<string> ExportImage(
        [Description("Absolute path ending in .png, .jpg or .webp")] string path,
        [Description("JPEG and WebP quality from 1 to 100")] int quality = 90,
        [Description("Replace a file that exists at the path")] bool overwrite = false,
        int? document = null)
    {
        path = Absolute(path);
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ExportFormat.Png,
            ".jpg" or ".jpeg" => ExportFormat.Jpeg,
            ".webp" => ExportFormat.Webp,
            _ => throw new McpException("The path must end in .png, .jpg or .webp; save_document writes a project file.")
        };
        if (quality is < 1 or > 100) throw new McpException("quality is from 1 to 100.");
        Fresh(path, null, overwrite);
        var (title, flat) = await OnUi(() =>
        {
            var s = Session(document);
            if (window.IsDragging || s.IsInteracting) throw new McpException("Finish the current edit before exporting the image.");
            return (s.Title, s.Flatten());
        });
        try
        {
            await Task.Run(() => ImageFiles.Save(flat, path, format, format == ExportFormat.Png ? 100 : quality));
            return $"Exported \"{title}\" as a {flat.Width}×{flat.Height} px {format.ToString().ToUpperInvariant()} to {path}.";
        }
        catch (Exception error) when (error is not McpException) { throw new McpException($"Couldn't export {Path.GetFileName(path)}: {error.Message}"); }
        finally { flat.Dispose(); }
    }

    [McpServerTool(Name = "export_psd")]
    [Description("Exports a layered PSD copy with groups, masks, text and vector shapes. Unsupported effects and adjustments retain their rendered appearance and are listed in the result. Does not change the document's file path or saved state.")]
    public async Task<string> ExportPsd(string path, bool overwrite = false, int? document = null)
    {
        path = Absolute(path);
        if (!path.EndsWith(PsdExport.Extension, StringComparison.OrdinalIgnoreCase)) throw new McpException("The path must end in .psd.");
        Fresh(path, null, overwrite);
        var (title, snapshot) = await OnUi(() =>
        {
            var s = Session(document);
            if (window.IsDragging || s.IsInteracting) throw new McpException("Finish the current edit before exporting the PSD.");
            return (s.Title, s.Document.Clone());
        });
        try
        {
            var conversions = PsdExport.Conversions(snapshot);
            await Task.Run(() => PsdExport.Save(snapshot, path));
            return $"Exported layered PSD \"{title}\" to {path}." + (conversions.Count == 0 ? "" : "\n" + string.Join("\n", conversions.Select(c => c.LayerName + ": " + c.Message)));
        }
        catch (Exception error) when (error is not McpException) { throw new McpException($"Couldn't export {Path.GetFileName(path)}: {error.Message}"); }
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new McpException("Give an absolute path.");
        return Path.GetFullPath(path);
    }

    /// <summary>Refuses a path that would replace a file the agent did not ask to replace; a document's own file is fair game.</summary>
    private static void Fresh(string path, string? own, bool overwrite)
    {
        if (overwrite || !File.Exists(path)) return;
        if (own != null && string.Equals(Path.GetFullPath(own), path, StringComparison.Ordinal)) return;
        throw new McpException($"There is already a file at {path}; pass overwrite: true to replace it.");
    }
}
