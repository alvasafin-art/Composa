using System.ComponentModel;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "refine_selection")]
    [Description("Refines the current selection with a local trimap. method: ai (local ViTMatte), detail (additional original-scale inference for fine mesh; slower), classic (RGB matting), edges, keep. foreground/background/unknown are arrays of canvas [x,y] brush centers; brushSize is the stamp diameter. radius is the automatic uncertain edge band in pixels. output: selection, mask (active raster layer), layer (new editable cutout with mask). decontaminate is 0..1 and forces layer output, keeping the original. sampleAllLayers uses the visible canvas; otherwise the active raster is read before its old mask/styles. Runs in the background and refuses to apply after the document changes.")]
    public async Task<string> RefineSelection(string method = "ai", int radius = 8, string output = "selection", bool sampleAllLayers = false,
        double decontaminate = 0, double smooth = 0, double feather = 0, double contrast = 0, int shift = 0,
        double[][]? foreground = null, double[][]? background = null, double[][]? unknown = null, double brushSize = 30, int? document = null, CancellationToken cancellationToken = default)
    {
        if (method is not ("ai" or "detail" or "classic" or "edges" or "keep") || output is not ("selection" or "mask" or "layer")) throw new McpException("Use method ai/detail/classic/edges/keep and output selection/mask/layer.");
        if (radius is < 0 or > 100 || shift is < -100 or > 100 || !double.IsFinite(decontaminate) || decontaminate is < 0 or > 1
            || !double.IsFinite(smooth) || smooth is < 0 or > 20 || !double.IsFinite(feather) || feather is < 0 or > 100 || !double.IsFinite(contrast) || contrast is < 0 or > 100
            || !double.IsFinite(brushSize) || brushSize is < 1 or > 500) throw new McpException("Refinement parameters are outside their supported range.");
        if (method is "edges" or "keep" && (foreground?.Length > 0 || background?.Length > 0 || unknown?.Length > 0)) throw new McpException("Local trimap marks require ai or classic matting.");
        if (decontaminate > 0) output = "layer";
        var capture = await OnUi(() =>
        {
            var s = Editable(document); if (s.Selection == null) throw new McpException("Create a selection first.");
            if (output == "mask" && (s.ActiveLayer?.Pixels == null || s.PixelsLocked(s.ActiveLayer))) throw new McpException("Choose an unlocked raster layer for mask output.");
            return (Session: s, Snapshot: s.Document.Clone(), s.Revision, Selection: s.Selection, Source: !sampleAllLayers && s.ActiveLayer?.Pixels != null ? s.ActiveLayer.Id : (Guid?)null);
        });
        using var marks = Pixels.NewMask(capture.Snapshot.Width, capture.Snapshot.Height);
        void Paint(double[][]? centers, MattingMark mark)
        {
            if (centers == null) return; if (centers.Length > 4096) throw new McpException("Use at most 4096 centers per local brush.");
            using var canvas = new SKCanvas(marks); using var paint = new SKPaint { Color = new SKColor(0, 0, 0, (byte)mark), BlendMode = SKBlendMode.Src, IsAntialias = false };
            foreach (var p in centers)
            {
                if (p.Length != 2 || !p.All(double.IsFinite) || p[0] < 0 || p[1] < 0 || p[0] >= capture.Snapshot.Width || p[1] >= capture.Snapshot.Height) throw new McpException("A local brush center must be an [x,y] point inside the canvas.");
                canvas.DrawCircle((float)p[0], (float)p[1], (float)brushSize / 2, paint);
            }
        }
        Paint(foreground, MattingMark.Foreground); Paint(background, MattingMark.Background); Paint(unknown, MattingMark.Unknown);
        using var source = await Task.Run(() => MaskRefinement.Source(capture.Snapshot, sampleAllLayers), cancellationToken);
        SKBitmap? result = null, colors = null;
        try
        {
            result = await Task.Run(() =>
            {
                using var matte = method is "ai" or "detail" ? TrimapMatting.Refine(source, capture.Selection, radius, marks, cancellationToken, fineDetail: method == "detail")
                    : method == "classic" ? AlphaMatting.Refine(source, capture.Selection, radius, marks, cancellationToken) : null;
                return MaskRefinement.Refine(matte ?? capture.Selection, source, new(smooth, feather, contrast, shift, method == "edges", radius), cancellationToken);
            }, cancellationToken);
            if (decontaminate > 0) output = "layer";
            if (output == "layer") colors = await Task.Run(() => MaskRefinement.DecontaminateColors(source, result, decontaminate, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await OnUi(() =>
            {
                if (!window.Sessions.Contains(capture.Session)) throw new McpException("The document closed while refining; the computed mask was not applied.");
                var live = Editable(document); // Finishing newly typed text may itself change the revision.
                if (live != capture.Session || live.Revision != capture.Revision || live.Document.ActiveLayerId != capture.Snapshot.ActiveLayerId)
                    throw new McpException("The document changed while refining; the computed mask was not applied.");
                var s = capture.Session;
                if (output == "layer") { s.AddRefinedCutout(colors!, result, capture.Source); result = colors = null; }
                else if (output == "mask") s.RefinedSelectionToMask(result);
                else { var empty = SelectionMask.IsEmpty(result); s.Select(result, SelectionMode.Replace, "Select and Mask"); if (!empty) result = null; }
                return output == "selection" ? Selected(s) : output == "mask" ? "Refined the active layer mask." : "Created an editable refined cutout layer.";
            });
        }
        finally { result?.Dispose(); colors?.Dispose(); }
    }
}
