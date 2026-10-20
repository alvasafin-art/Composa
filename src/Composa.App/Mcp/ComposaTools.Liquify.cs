using System.ComponentModel;
using Composa.Painting;
using Composa.Rendering;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace Composa.App.Mcp;

public sealed record LiquifyStrokeSpec(string Mode, double[][] Points, double Size = 150, double Strength = .5, double Hardness = .5);

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "liquify_layer")]
    [Description("Liquifies an editable raster layer with an original-source displacement map. strokes are ordered operations: mode push/reconstruct/freeze/thaw with canvas [x,y] points, brush size in canvas pixels and strength/hardness 0..1; restoreAll and thawAll need no points. Freeze protects the current shape including from reconstruction; the current selection protects unselected pixels. All operations share one field, so reconstruction restores coordinates without ghosted color blending. Renders off-thread, commits one Undo, preserves the person's active layer and channels, and rejects a changed document. Live/smart content must be edited inside its source or explicitly rasterized first.")]
    public async Task<string> LiquifyLayer(string layer, LiquifyStrokeSpec[] strokes, int? document = null, CancellationToken cancellationToken = default)
    {
        if (strokes == null || strokes.Length is < 1 or > 256 || strokes.Sum(s => (long)(s?.Points?.Length ?? 0)) > 4096)
            throw new McpException("Use 1..256 operations and at most 4096 total points.");
        foreach (var s in strokes)
            if (s == null || s.Mode is not ("push" or "reconstruct" or "freeze" or "thaw" or "restoreAll" or "thawAll")
                || !double.IsFinite(s.Size) || s.Size is < 1 or > 2000 || !double.IsFinite(s.Strength) || s.Strength is < 0 or > 1
                || !double.IsFinite(s.Hardness) || s.Hardness is < 0 or > 1 || s.Points == null
                || s.Points.Any(p => p == null || p.Length != 2 || !p.All(double.IsFinite) || p.Any(v => Math.Abs(v) > Model.DocumentLimits.MaxSide * 2))
                || s.Mode is not ("restoreAll" or "thawAll") && s.Points.Length == 0)
                throw new McpException("Each operation needs a valid mode, finite canvas points, size 1..2000 and strength/hardness 0..1.");
        var capture = await OnUi(() =>
        {
            var session = Editable(document); var target = Find(session, layer); var active = session.ActiveLayer; var oldTarget = CapturePaintTarget(session);
            try
            {
                session.SelectLayer(target.Id); SetPaintTarget(session, "pixels");
                if (!session.CanLiquify || target.Pixels == null) throw new McpException("Select an unlocked editable raster layer; transparency locks, masks, live text and smart content cannot be liquified here.");
                if (target.Matrix.Persp0 != 0 || target.Matrix.Persp1 != 0 || !target.Matrix.TryInvert(out var inverse)) throw new McpException("Liquify needs an invertible affine layer placement.");
                var brushScale = Math.Sqrt(Math.Abs(inverse.ScaleX * inverse.ScaleY - inverse.SkewX * inverse.SkewY));
                if (strokes.Any(s => s.Size * brushScale > Model.DocumentLimits.MaxSide)) throw new McpException("The source-grid brush would exceed the supported size.");
                return (Session: session, session.Revision, Active: active?.Id, Layer: target.Id, Source: target.Pixels, Selection: session.SelectionInLayerSpace(target), Inverse: inverse, BrushScale: brushScale,
                    Depth: session.Document.WorkingDepth, Profile: session.Document.Profile);
            }
            finally { if (active != null) session.SelectLayer(active.Id); else session.ClearLayerSelection(); RestorePaintTarget(session, oldTarget); }
        });
        using var selection = capture.Selection;
        SKBitmap? result = null;
        try
        {
            var field = await Task.Run(() =>
            {
                var f = new LiquifyField(capture.Source.Width, capture.Source.Height);
                foreach (var s in strokes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (s.Mode == "restoreAll") { f = f.RestoreAll(); continue; }
                    if (s.Mode == "thawAll") { f = f.ThawAll(); continue; }
                    var tool = s.Mode switch { "push" => LiquifyTool.Push, "freeze" => LiquifyTool.Freeze, "thaw" => LiquifyTool.Thaw, _ => LiquifyTool.Reconstruct };
                    var points = s.Points.Select(p => capture.Inverse.MapPoint((float)p[0], (float)p[1])).ToArray();
                    var from = points[0];
                    foreach (var to in points) { f = f.Stroke(from, to, tool, Math.Max(1, s.Size * capture.BrushScale), s.Strength, s.Hardness, selection, cancellationToken); from = to; }
                }
                return f;
            }, cancellationToken);
            if (!field.HasDeformation) return "No remaining Liquify distortion; document unchanged.";
            result = await Task.Run(() =>
            {
                using var converted = Pixels.DepthOf(capture.Source) != capture.Depth || !Pixels.ProfileOf(capture.Source).Equals(capture.Profile)
                    ? Pixels.ConvertWorking(capture.Source, capture.Depth, capture.Profile) : null;
                return field.Render(converted ?? capture.Source, token: cancellationToken);
            }, cancellationToken);
            return await OnUi(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!window.Sessions.Contains(capture.Session)) throw new McpException("The document closed while liquifying; no result was applied.");
                var session = Editable(document);
                if (session != capture.Session || session.Revision != capture.Revision || session.ActiveLayer?.Id != capture.Active)
                    throw new McpException("The document or active layer changed while liquifying; no result was applied.");
                var oldTarget = CapturePaintTarget(session);
                try
                {
                    session.SelectLayer(capture.Layer); SetPaintTarget(session, "pixels");
                    if (!session.ApplyLiquify(capture.Source, field, result!)) throw new McpException("The captured layer is no longer editable; no result was applied.");
                    result = null; return $"Liquified \"{layer}\" with {strokes.Length} operations in one Undo step.";
                }
                finally { if (capture.Active is { } id) session.SelectLayer(id); else session.ClearLayerSelection(); RestorePaintTarget(session, oldTarget); }
            });
        }
        finally { result?.Dispose(); }
    }
}
