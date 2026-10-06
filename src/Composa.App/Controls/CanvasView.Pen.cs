using Avalonia.Input;
using Composa.Model;
using SkiaSharp;
namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private readonly List<BezierNode> penDraft = [];
    private Layer? penLayer;
    private VectorPath? penOriginal;
    private int penNode, penHandle;
    private Guid? penSelectedLayer;
    private int penSelectedNode = -1;
    private bool penDraftDrag;
    public bool EditVectorMask { get; set; }
    private VectorPath? CurrentPenPath => EditVectorMask ? session?.ActiveLayer?.VectorMask : session?.ActiveLayer?.Shape?.Path;
    public void FinishPen(bool closed = false)
    {
        if (penDraft.Count >= 2 && session != null) session.AddPath(new VectorPath { Nodes = penDraft.ToArray(), Closed = closed && penDraft.Count >= 3 });
        penDraft.Clear(); InvalidateVisual(); ToolStateChanged?.Invoke();
    }
    private void PenPress(bool alt, bool newPath, bool insert)
    {
        if (penDraft.Count > 2 && SKPoint.Distance(new SKPoint((float)penDraft[0].X, (float)penDraft[0].Y), pressDocument) * UnitsPerPixel < 9)
        { FinishPen(closed: true); return; }
        if (penDraft.Count == 0 && !newPath && CurrentPenPath is { } path && session?.ActiveLayer is { Pixels: { } pixels } layer && !session.PixelsLocked(layer))
        {
            var nearest = 9.0; var found = -1; var which = 0;
            for (var i = 0; i < path.Nodes.Length; i++)
            {
                var n = path.Nodes[i]; var handles = new[] { new SKPoint((float)n.X * pixels.Width, (float)n.Y * pixels.Height), new SKPoint((float)n.InX * pixels.Width, (float)n.InY * pixels.Height), new SKPoint((float)n.OutX * pixels.Width, (float)n.OutY * pixels.Height) };
                for (var h = 0; h < handles.Length; h++)
                {
                    if (h > 0 && handles[h] == handles[0]) continue;
                    var distance = SKPoint.Distance(layer.Matrix.MapPoint(handles[h]), pressDocument) * UnitsPerPixel;
                    if (distance < nearest) { nearest = distance; found = i; which = h; }
                }
            }
            if (found >= 0)
            {
                penSelectedLayer = layer.Id; penSelectedNode = found;
                session.Begin("Edit Path"); penLayer = layer; penOriginal = path; penNode = found; penHandle = alt && which == 0 ? 2 : which;
                penDraftDrag = false; drag = Drag.PenNode; return;
            }
            if (insert)
            {
                var segment = -1; var parameter = .5; nearest = 9;
                for (var i = 0; i < path.Nodes.Length - (path.Closed ? 0 : 1); i++)
                {
                    var a = path.Nodes[i]; var b = path.Nodes[(i + 1) % path.Nodes.Length];
                    for (var step = 1; step < 100; step++)
                    {
                        var t = step / 100.0; var u = 1 - t;
                        var x = u * u * u * a.X + 3 * u * u * t * a.OutX + 3 * u * t * t * b.InX + t * t * t * b.X;
                        var y = u * u * u * a.Y + 3 * u * u * t * a.OutY + 3 * u * t * t * b.InY + t * t * t * b.Y;
                        var distance = SKPoint.Distance(layer.Matrix.MapPoint((float)x * pixels.Width, (float)y * pixels.Height), pressDocument) * UnitsPerPixel;
                        if (distance < nearest) { nearest = distance; segment = i; parameter = t; }
                    }
                }
                if (segment >= 0)
                {
                    session.Begin("Edit Path"); session.PreviewVectorPath(layer, path.SplitSegment(segment, parameter), EditVectorMask); session.Commit();
                    penSelectedLayer = layer.Id; penSelectedNode = segment + 1; return;
                }
                return;
            }
        }
        penSelectedLayer = null; penSelectedNode = -1;
        penDraft.Add(BezierNode.Corner(pressDocument.X, pressDocument.Y)); penDraftDrag = true; drag = Drag.PenNode;
    }
    private bool DeletePenNode()
    {
        if (session?.ActiveLayer is not { } layer || penSelectedLayer != layer.Id || CurrentPenPath is not { } path || session.PixelsLocked(layer)) return false;
        var next = path.RemoveNode(penSelectedNode); if (ReferenceEquals(path, next)) return true;
        session.Begin("Edit Path"); session.PreviewVectorPath(layer, next, EditVectorMask);
        if (!EditVectorMask) session.CompletePathEdit(layer); session.Commit(); penSelectedNode = -1; return true;
    }
    private void PenMove(bool alt)
    {
        if (penDraftDrag)
        {
            if (penDraft.Count == 0) return; var n = penDraft[^1];
            penDraft[^1] = n with { OutX = currentDocument.X, OutY = currentDocument.Y, InX = 2 * n.X - currentDocument.X, InY = 2 * n.Y - currentDocument.Y }; return;
        }
        if (penLayer?.Pixels == null || penOriginal == null || !penLayer.Matrix.TryInvert(out var inverse)) return;
        var p = inverse.MapPoint(currentDocument); var nodes = penOriginal.Nodes.ToArray(); var node = nodes[penNode];
        double x = p.X / penLayer.Pixels.Width, y = p.Y / penLayer.Pixels.Height;
        nodes[penNode] = penHandle switch
        {
            1 => node with { InX = x, InY = y, OutX = alt ? node.OutX : 2 * node.X - x, OutY = alt ? node.OutY : 2 * node.Y - y },
            2 => node with { OutX = x, OutY = y, InX = alt ? node.InX : 2 * node.X - x, InY = alt ? node.InY : 2 * node.Y - y },
            _ => node with { X = x, Y = y, InX = node.InX + x - node.X, InY = node.InY + y - node.Y, OutX = node.OutX + x - node.X, OutY = node.OutY + y - node.Y }
        };
        session!.PreviewVectorPath(penLayer, penOriginal with { Nodes = nodes }, EditVectorMask);
    }
    private void CapturePenOverlay(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        if (session?.Tool != Composa.Editing.Tool.Pen) return;
        var draft = penDraft.Count > 0; var geometry = draft ? new VectorPath { Nodes = penDraft.ToArray() } : CurrentPenPath;
        if (geometry == null) return;
        var pixels = session.ActiveLayer?.Pixels; var width = draft ? 1 : pixels?.Width ?? 1; var height = draft ? 1 : pixels?.Height ?? 1;
        var matrix = draft ? view : SKMatrix.Concat(view, session.ActiveLayer!.Matrix);
        steps.Add(canvas =>
        {
            using var path = geometry.Build(width, height); path.Transform(matrix);
            using var stroke = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = hair };
            using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawPath(path, stroke);
            foreach (var n in geometry.Nodes)
            {
                var anchor = matrix.MapPoint((float)n.X * width, (float)n.Y * height);
                foreach (var point in new[] { matrix.MapPoint((float)n.InX * width, (float)n.InY * height), matrix.MapPoint((float)n.OutX * width, (float)n.OutY * height) })
                { if (point == anchor) continue; canvas.DrawLine(anchor, point, stroke); canvas.DrawCircle(point, 3 * hair, fill); canvas.DrawCircle(point, 3 * hair, stroke); }
                var rect = SKRect.Create(anchor.X - 4 * hair, anchor.Y - 4 * hair, 8 * hair, 8 * hair); canvas.DrawRect(rect, fill); canvas.DrawRect(rect, stroke);
            }
        });
    }
}
