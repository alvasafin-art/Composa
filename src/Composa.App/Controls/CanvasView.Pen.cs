using Avalonia.Input;
using Composa.Editing;
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
    private int penSelectedSegment = -1;
    private int draftSelection = -1;
    private bool endpointCorner;
    private double bendParameter;
    private bool penDraftDrag;
    private bool penDraftEditing;
    private bool penConverted;
    private Guid? penMaskLayer;
    public bool EditVectorMask
    {
        get => session?.Tool == Composa.Editing.Tool.Pen && session.ActiveLayer is { VectorMask: not null } layer && penMaskLayer == layer.Id;
        set
        {
            var next = value ? session?.ActiveLayer?.Id : null;
            if (next != penMaskLayer) ClearPenSelection();
            penMaskLayer = next;
        }
    }
    private (SKMatrix Matrix, int Width, int Height, SKPoint Origin)? penTextFrame;
    private (SKMatrix Matrix, int Width, int Height) penPathFrame;

    private VectorPath? CurrentPenPath => session?.ActiveLayer is { } layer
        ? EditVectorMask ? layer.VectorMask : EditorSession.EditableTextCurve(layer) ?? EditorSession.EditableShapePath(layer)
        : null;
    public void FinishPen(bool closed = false)
    {
        if (penDraft.Count >= 2 && session != null) session.AddPath(new VectorPath { Nodes = penDraft.ToArray(), Closed = closed && penDraft.Count >= 3 });
        penDraft.Clear(); draftSelection = -1; penNodes.Clear(); InvalidateCanvas(); ToolStateChanged?.Invoke();
    }
    private void PenPress(bool alt, bool editPoints, bool insert, int clicks)
    {
        if (PenMode != PenEditMode.Draw) { PenModePress(alt, insert); return; }
        penNodes.Clear();
        penDraftEditing = false;
        penConverted = false; endpointCorner = false;
        if (insert && penDraft.Count >= 2)
        {
            var draft = new VectorPath { Nodes = penDraft.ToArray() };
            var (edge, t) = draft.NearestSegment(SKMatrix.Identity,pressDocument,9 / UnitsPerPixel);
            if (edge >= 0)
            {
                var split = draft.SplitSegment(edge,t);
                penDraft.Clear(); penDraft.AddRange(split.Nodes);
                draftSelection = edge+1;
                return;
            }
        }
        if ((editPoints || alt) && penDraft.Count > 0)
        {
            var nearest = 9.0; var found = -1; var which = 0;
            for (var i = 0; i < penDraft.Count; i++)
            {
                var node = penDraft[i]; var handles = new[] { new SKPoint((float)node.X, (float)node.Y), new SKPoint((float)node.InX, (float)node.InY), new SKPoint((float)node.OutX, (float)node.OutY) };
                for (var h = 0; h < handles.Length; h++)
                {
                    if (h > 0 && handles[h] == handles[0]) continue;
                    var distance = SKPoint.Distance(handles[h], pressDocument) * UnitsPerPixel;
                    if (distance < nearest) { nearest = distance; found = i; which = h; }
                }
            }
            if (found >= 0)
            {
                draftSelection = found; penNode = found; penHandle = alt && which == 0 ? 2 : which;
                if (alt && which == 0)
                {
                    endpointCorner = found == penDraft.Count - 1;
                    var anchor = penDraft[found];
                    penDraft[found] = endpointCorner ? anchor with { OutX = anchor.X, OutY = anchor.Y }
                        : ConvertedNode(penDraft.ToArray(), found, false, 1, 1);
                    penConverted = true;
                }
                penOriginal = new() { Nodes = penDraft.ToArray() }; penDraftEditing = penDraftDrag = true; drag = Drag.PenNode;
            }
            return;
        }
        if (!editPoints && clicks >= 2 && penDraft.Count >= 3)
        { FinishPen(closed: true); return; }
        if (penDraft.Count > 2 && SKPoint.Distance(new SKPoint((float)penDraft[0].X, (float)penDraft[0].Y), pressDocument) * UnitsPerPixel < 9)
        { FinishPen(closed: true); return; }
        if (penDraft.Count == 0 && TryEditSelectedPath(alt, insert, clicks)) return;
        if (editPoints || alt) return;
        penSelectedLayer = null; penSelectedNode = penSelectedSegment = -1;
        draftSelection = -1;
        var point = SnapLayoutHandle(pressDocument);
        penDraft.Add(BezierNode.Corner(point.X, point.Y)); penDraftDrag = true; drag = Drag.PenNode;
    }
    private bool TryEditSelectedPath(bool alt, bool insert, int clicks, bool nodesOnly = false, bool segmentsOnly = false)
    {
        penConverted = endpointCorner = false;
        if (session?.ActiveLayer is not { Pixels: { } pixels } layer || session.PixelsLocked(layer) || CurrentPenPath is not { } path)
            return false;
        penTextFrame = !EditVectorMask && layer.Text is { Path: not null } text
            ? (layer.Matrix, pixels.Width, pixels.Height, Composa.Text.TextLayout.PathOriginFor(text))
            : null;
        penPathFrame = (layer.Matrix, pixels.Width, pixels.Height);
        var nearest = 9.0; var found = -1; var which = 0;
        for (var i = 0; !segmentsOnly && i < path.Nodes.Length; i++)
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
            if (penSelectedLayer != layer.Id || !penNodes.Contains(found)) penNodes.Clear();
            penSelectedSegment = -1; penSelectedLayer = layer.Id; penSelectedNode = found;
            session.Begin(penTextFrame != null ? "Edit Text Curve" : "Edit Path"); penLayer = layer; penOriginal = path; penNode = found; penHandle = alt && which == 0 ? 2 : which;
            if (alt && which == 0)
            {
                var nodes = path.Nodes.ToArray(); nodes[found] = ConvertedNode(nodes, found, path.Closed, pixels.Width, pixels.Height);
                penOriginal = path with { Nodes = nodes }; PreviewPenOutline(layer, penOriginal); penConverted = true;
            }
            penDraftDrag = false; drag = Drag.PenNode; return true;
        }
        if (nodesOnly) return false;
        var transform = SKMatrix.Concat(layer.Matrix, SKMatrix.CreateScale(pixels.Width, pixels.Height));
        var (segment, t) = path.NearestSegment(transform, pressDocument, 9 / UnitsPerPixel);
        if (segment >= 0)
        {
            penSelectedLayer = layer.Id; penSelectedNode = -1; penSelectedSegment = segment;
            if (penTextFrame == null && (insert || clicks >= 2))
            {
                var next = path.SplitSegment(segment,t);
                if (next == path) return true;
                session.Begin("Insert Path Point"); session.PreviewVectorPath(layer, next, EditVectorMask); session.Commit();
                penSelectedNode = segment + 1; penSelectedSegment = -1;
            }
            else
            {
                penLayer=layer; penOriginal=path; penNode=segment; penHandle=3; bendParameter=Math.Clamp(t, .02, .98);
                penDraftDrag=false; session.Begin(penTextFrame != null ? "Edit Text Curve" : "Bend Path"); drag=Drag.PenNode;
            }
            return true;
        }
        return false;
    }
    private void DeleteDraftPoint()
    {
        if (penNodes.Count > 0)
        {
            ReplaceDraft(new VectorPath { Nodes = penDraft.ToArray() }.RemoveNodes(penNodes));
            ClearPenSelection();
            return;
        }
        int index = (uint)draftSelection < penDraft.Count ? draftSelection : penDraft.Count-1;
        var path = new VectorPath { Nodes=penDraft.ToArray() }.RemoveNode(index);
        penDraft.Clear(); penDraft.AddRange(path.Nodes); draftSelection=-1;
        InvalidateCanvas();
    }

    private bool DeletePenNode()
    {
        if ((penSelectedSegment < 0 && penSelectedNode < 0 && penNodes.Count == 0) || session?.ActiveLayer is not { } layer || penSelectedLayer != layer.Id || CurrentPenPath is not { } path || session.PixelsLocked(layer)) return false;
        if (!EditVectorMask && layer.Text?.Path != null) return false;
        var next = penSelectedSegment >= 0 ? path.RemoveSegment(penSelectedSegment) : penNodes.Count > 0
            ? path.RemoveNodes(penNodes)
            : path.RemoveNode(penSelectedNode, layer.Pixels!.Width, layer.Pixels.Height);
        if (ReferenceEquals(path, next)) return true;
        session.Begin("Edit Path"); session.PreviewVectorPath(layer, next, EditVectorMask);
        if (!EditVectorMask) session.CompletePathEdit(layer); session.Commit(); penNodes.Clear(); penSelectedNode = penSelectedSegment = -1; InvalidateCanvas(); return true;
    }
    private void PenMove(bool alt)
    {
        if (penConverted) alt = endpointCorner;
        guides.Clear();
        if (penDraftDrag)
        {
            if (penDraftEditing && penOriginal != null)
            {
                var point = penHandle == 0 ? SnapLayoutHandle(currentDocument) : currentDocument;
                if (penHandle == 3)
                {
                    var bent = penOriginal.BendSegment(penNode, bendParameter, point.X - pressDocument.X, point.Y - pressDocument.Y);
                    penDraft.Clear(); penDraft.AddRange(bent.Nodes);
                }
                else if (penHandle == 0 && penNodes.Count > 1)
                {
                    var moved = TranslatePenNodes(penOriginal, point - pressDocument, 1, 1);
                    penDraft.Clear(); penDraft.AddRange(moved.Nodes);
                }
                else penDraft[penNode] = MovedNode(penOriginal.Nodes[penNode], point.X, point.Y, penHandle, alt);
                return;
            }
            if (penDraft.Count == 0) return; var n = penDraft[^1];
            penDraft[^1] = n with { OutX = currentDocument.X, OutY = currentDocument.Y, InX = 2 * n.X - currentDocument.X, InY = 2 * n.Y - currentDocument.Y }; return;
        }
        if (penLayer?.Pixels == null || penOriginal == null || !penPathFrame.Matrix.TryInvert(out var inverse)) return;
        double width = penPathFrame.Width, height = penPathFrame.Height;
        var p = inverse.MapPoint(penHandle == 0 ? SnapLayoutHandle(currentDocument) : currentDocument);
        if (penHandle == 0 && penNodes.Count > 1)
        {
            PreviewPenOutline(penLayer, TranslatePenNodes(penOriginal, p - inverse.MapPoint(pressDocument), width, height));
            return;
        }
        if (penHandle == 3)
        {
            var start = inverse.MapPoint(pressDocument);
            var bent = penOriginal.BendSegment(penNode, bendParameter,
                (p.X-start.X)/width, (p.Y-start.Y)/height);
            PreviewPenOutline(penLayer, bent); return;
        }
        var nodes = penOriginal.Nodes.ToArray(); var node = nodes[penNode];
        double x = p.X / width, y = p.Y / height;
        nodes[penNode] = MovedNode(node, x, y, penHandle, alt);
        PreviewPenOutline(penLayer, penOriginal with { Nodes = nodes });
    }

    private void PreviewPenOutline(Layer layer, VectorPath path)
    {
        if (penTextFrame is { } frame)
        {
            var outline = new TextPath(path).MapOutline(frame.Width, frame.Height, frame.Origin.X, frame.Origin.Y);
            session!.PreviewTextCurve(layer, outline);
        }
        else session!.PreviewVectorPath(layer, path, EditVectorMask, fitToPath: true);
    }
    private static BezierNode ConvertedNode(BezierNode[] nodes, int index, bool closed, double width, double height)
    {
        var node = nodes[index];
        if (node.InX != node.X || node.InY != node.Y || node.OutX != node.X || node.OutY != node.Y)
            return node with { InX = node.X, InY = node.Y, OutX = node.X, OutY = node.Y };
        var before = nodes[index > 0 && !node.StartsContour ? index - 1 : closed ? nodes.Length - 1 : index];
        var after = nodes[index < nodes.Length - 1 && !nodes[index+1].StartsContour ? index + 1 : closed ? 0 : index];
        var dx = (after.X - before.X) * width; var dy = (after.Y - before.Y) * height;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var previous = Math.Sqrt(Math.Pow((node.X - before.X) * width, 2) + Math.Pow((node.Y - before.Y) * height, 2));
        var next = Math.Sqrt(Math.Pow((after.X - node.X) * width, 2) + Math.Pow((after.Y - node.Y) * height, 2));
        var arm = previous > 0 && next > 0 ? Math.Min(previous, next) / 3 : Math.Max(previous, next) / 3;
        if (length < .0001) { dx = 1; dy = 0; length = 1; } if (arm < .0001) arm = 16;
        dx = dx / length * arm / width; dy = dy / length * arm / height;
        return node with { InX = node.X - dx, InY = node.Y - dy, OutX = node.X + dx, OutY = node.Y + dy };
    }
    private static BezierNode MovedNode(BezierNode node, double x, double y, int handle, bool independent)
    {
        if (handle == 0)
            return node with { X=x, Y=y, InX=node.InX+x-node.X, InY=node.InY+y-node.Y,
                OutX=node.OutX+x-node.X, OutY=node.OutY+y-node.Y };
        bool incoming=handle==1;
        var changed=incoming ? node with { InX=x, InY=y } : node with { OutX=x, OutY=y };
        if (independent) return changed;
        double dx=x-node.X, dy=y-node.Y;
        double movedLength=Math.Sqrt(dx*dx+dy*dy);
        double ox=(incoming ? node.OutX : node.InX)-node.X, oy=(incoming ? node.OutY : node.InY)-node.Y;
        double hx=(incoming ? node.InX : node.OutX)-node.X, hy=(incoming ? node.InY : node.OutY)-node.Y;
        double previousLength=Math.Sqrt(hx*hx+hy*hy), oppositeLength=Math.Sqrt(ox*ox+oy*oy);
        // Preserve an asymmetric smooth join and leave an existing corner independent.
        if (movedLength==0 || previousLength==0 || oppositeLength==0 || hx*ox+hy*oy>=0 ||
            Math.Abs(hx*oy-hy*ox)>1e-6*previousLength*oppositeLength) return changed;
        var ratio=oppositeLength/movedLength;
        return incoming ? changed with { OutX=node.X-dx*ratio, OutY=node.Y-dy*ratio }
            : changed with { InX=node.X-dx*ratio, InY=node.Y-dy*ratio };
    }
    private void CapturePenOverlay(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        if (session?.Tool is not (Composa.Editing.Tool.Pen or Composa.Editing.Tool.Shape)) return;
        var draft = penDraft.Count > 0; var geometry = draft ? new VectorPath { Nodes = penDraft.ToArray() } : CurrentPenPath;
        if (geometry == null) return;
        var pixels = session.ActiveLayer?.Pixels; var width = draft ? 1 : pixels?.Width ?? 1; var height = draft ? 1 : pixels?.Height ?? 1;
        var matrix = draft ? view : SKMatrix.Concat(view, session.ActiveLayer!.Matrix);
        bool selectedLayer = !draft && penSelectedLayer == session.ActiveLayer?.Id;
        int selectedSegment = selectedLayer ? penSelectedSegment : -1, selectedNode = draft ? draftSelection : selectedLayer ? penSelectedNode : -1;
        var multiSelection = draft || selectedLayer ? penNodes.ToHashSet() : [];
        steps.Add(canvas =>
        {
            using var path = geometry.Build(width, height); path.Transform(matrix);
            using var stroke = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = hair };
            using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawPath(path, stroke);
            using var selected = new SKPaint { Color = Accent, IsAntialias = true };
            if (geometry.HasSegment(selectedSegment))
            {
                var edge = new VectorPath { Nodes = [geometry.Nodes[selectedSegment], geometry.Nodes[geometry.SegmentEnd(selectedSegment)] with { StartsContour = false }] };
                using var highlight = edge.Build(width,height); highlight.Transform(matrix);
                stroke.StrokeWidth = 3*hair; canvas.DrawPath(highlight,stroke); stroke.StrokeWidth=hair;
            }
            for (int i=0;i<geometry.Nodes.Length;i++)
            {
                var n = geometry.Nodes[i];
                var anchor = matrix.MapPoint((float)n.X * width, (float)n.Y * height);
                foreach (var point in new[] { matrix.MapPoint((float)n.InX * width, (float)n.InY * height), matrix.MapPoint((float)n.OutX * width, (float)n.OutY * height) })
                { if (point == anchor) continue; canvas.DrawLine(anchor, point, stroke); canvas.DrawCircle(point, 3 * hair, fill); canvas.DrawCircle(point, 3 * hair, stroke); }
                var rect = SKRect.Create(anchor.X - 4 * hair, anchor.Y - 4 * hair, 8 * hair, 8 * hair); canvas.DrawRect(rect, selectedNode == i || multiSelection.Contains(i) ? selected : fill); canvas.DrawRect(rect, stroke);
            }
        });
    }
}
