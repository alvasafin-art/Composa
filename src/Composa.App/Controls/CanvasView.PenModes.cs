using Avalonia;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

internal enum PenEditMode { Draw, Move, Lasso, Bend, Cut, Erase }

public sealed partial class CanvasView
{
    private readonly HashSet<int> penNodes = [];
    private VectorPath? eraseDraftBefore;
    private SKPoint erasePrevious;
    private bool eraseTransaction;
    internal PenEditMode PenMode { get; private set; }
    internal bool HasPenOutline => penDraft.Count > 0 || CurrentPenPath is { Nodes.Length: > 0 };
    internal bool CanCutPenOutline => penDraft.Count > 1 || !EditVectorMask && session?.ActiveLayer?.Shape is not null && HasPenOutline;

    internal void SwitchPenMode(PenEditMode mode)
    {
        if (IsDragging) return;
        PenMode = mode;
        Focus();
        InvalidateCanvas();
        ToolStateChanged?.Invoke();
    }

    private void PenModePress(bool alt, bool shift)
    {
        bool editingDraft = penDraft.Count > 0;
        if (!editingDraft && (session?.ActiveLayer is not { } active || session.PixelsLocked(active))) return;
        if (PenMode is PenEditMode.Move or PenEditMode.Bend)
        {
            var bend = PenMode == PenEditMode.Bend;
            var hit = editingDraft ? BeginDraftPenEdit(bend) : TryEditSelectedPath(alt, false, 1, nodesOnly: !bend, segmentsOnly: bend);
            if (!hit && !shift && !alt) ClearPenSelection();
            return;
        }

        var path = editingDraft ? new VectorPath { Nodes = penDraft.ToArray() } : CurrentPenPath;
        if (path == null) return;
        if (PenMode == PenEditMode.Lasso)
        {
            polygon.Clear(); polygon.Add(pressDocument);
            drag = Drag.PenLasso;
            return;
        }
        if (!CanCutPenOutline) return;
        ClearPenSelection();
        if (PenMode == PenEditMode.Cut)
        {
            var (segment, t) = path.NearestSegment(PenMapping(editingDraft), pressDocument, 9 / UnitsPerPixel);
            var cut = path.CutSegment(segment, t);
            if (ReferenceEquals(path, cut)) return;
            if (editingDraft) ReplaceDraft(cut);
            else if (session?.ActiveLayer is { } target)
            {
                var editor = session;
                editor.Apply("Cut Path", () => { editor.PreviewVectorPath(target, cut, false); editor.CompletePathEdit(target); });
            }
            return;
        }
        eraseDraftBefore = editingDraft ? path : null;
        penLayer = editingDraft ? null : session!.ActiveLayer;
        erasePrevious = pressDocument;
        drag = Drag.PenErase;
        ErasePenAt(pressDocument);
    }

    private bool BeginDraftPenEdit(bool bend)
    {
        var path = new VectorPath { Nodes = penDraft.ToArray() };
        if (bend)
        {
            (penNode, bendParameter) = path.NearestSegment(SKMatrix.Identity, pressDocument, 9 / UnitsPerPixel);
            if (penNode < 0) return false;
            bendParameter = Math.Clamp(bendParameter, .02, .98); penHandle = 3;
            penNodes.Clear(); draftSelection = -1;
        }
        else
        {
            var nearest = 9d;
            int index = -1, handle = 0;
            for (int i = 0; i < path.Nodes.Length; i++)
            {
                var node = path.Nodes[i];
                SKPoint[] handles = [new((float)node.X, (float)node.Y), new((float)node.InX, (float)node.InY), new((float)node.OutX, (float)node.OutY)];
                for (int h = 0; h < handles.Length; h++)
                {
                    if (h > 0 && handles[h] == handles[0]) continue;
                    var distance = SKPoint.Distance(handles[h], pressDocument) * UnitsPerPixel;
                    if (distance < nearest) { nearest = distance; index = i; handle = h; }
                }
            }
            if (index < 0) return false;
            if (!penNodes.Contains(index)) penNodes.Clear();
            draftSelection = penNode = index; penHandle = handle;
        }
        penOriginal = path;
        penConverted = endpointCorner = false;
        penDraftEditing = penDraftDrag = true;
        drag = Drag.PenNode;
        return true;
    }

    private SKMatrix PenMapping(bool draft)
    {
        if (draft || session?.ActiveLayer is not { Pixels: { } pixels } layer) return SKMatrix.Identity;
        return SKMatrix.Concat(layer.Matrix, SKMatrix.CreateScale(pixels.Width, pixels.Height));
    }

    private void ClearPenSelection()
    {
        penNodes.Clear(); penSelectedNode = penSelectedSegment = draftSelection = -1; penSelectedLayer = null;
    }

    private void EndPenLasso(bool shift, bool alt)
    {
        bool draft = penDraft.Count > 0;
        var current = draft ? penDraft.ToArray() : CurrentPenPath?.Nodes;
        var target = draft ? null : session?.ActiveLayer?.Id;
        if (penSelectedLayer != target || !shift && !alt) ClearPenSelection();
        else if (penSelectedNode >= 0) penNodes.Add(penSelectedNode);
        penSelectedLayer = target;
        penSelectedNode = penSelectedSegment = draftSelection = -1;
        if (current != null && polygon.Count >= 3)
        {
            var matrix = PenMapping(draft);
            var region = polygon.Select(p => new Point(p.X, p.Y)).ToArray();
            for (int i = 0; i < current.Length; i++)
            {
                var anchor = matrix.MapPoint((float)current[i].X, (float)current[i].Y);
                if (!Contains(region, new Point(anchor.X, anchor.Y))) continue;
                if (alt) penNodes.Remove(i);
                else penNodes.Add(i);
            }
        }
        polygon.Clear();
    }

    private VectorPath TranslatePenNodes(VectorPath original, SKPoint delta, double width, double height) =>
        original with { Nodes = original.Nodes.Select((node, i) => penNodes.Contains(i)
            ? MovedNode(node, node.X + delta.X / width, node.Y + delta.Y / height, 0, false) : node).ToArray() };

    private void ErasePenTo(SKPoint point)
    {
        if (SKPoint.Distance(erasePrevious, point) * UnitsPerPixel < 1) return;
        var steps = Math.Clamp((int)Math.Ceiling(SKPoint.Distance(erasePrevious, point) * UnitsPerPixel / 8), 1, 48);
        for (int i = 1; i <= steps; i++)
        {
            var fraction = (float)i / steps;
            ErasePenAt(new SKPoint(erasePrevious.X + (point.X - erasePrevious.X) * fraction,
                erasePrevious.Y + (point.Y - erasePrevious.Y) * fraction));
        }
        erasePrevious = point;
    }

    private void ErasePenAt(SKPoint point)
    {
        bool draft = eraseDraftBefore != null;
        if (!draft && (penLayer == null || session?.ActiveLayer != penLayer)) return;
        var path = draft ? new VectorPath { Nodes = penDraft.ToArray() } : CurrentPenPath;
        if (path == null) return;
        var matrix = PenMapping(draft);
        float radius = (float)(9 / UnitsPerPixel);
        int index = -1;
        for (int i = 0; i < path.Nodes.Length; i++)
        {
            var node = path.Nodes[i];
            var distance = SKPoint.Distance(matrix.MapPoint((float)node.X, (float)node.Y), point);
            if (distance < radius) { index = i; radius = distance; }
        }
        var changed = index >= 0 ? path.RemoveNode(index, penLayer?.Pixels?.Width ?? 1, penLayer?.Pixels?.Height ?? 1)
            : path.RemoveSegment(path.NearestSegment(matrix, point, 9 / UnitsPerPixel).Segment);
        if (ReferenceEquals(changed, path)) return;
        if (draft) ReplaceDraft(changed);
        else if (session is { } editor && penLayer is { } layer)
        {
            if (!eraseTransaction)
            {
                if (editor.IsInteracting) return;
                editor.Begin("Erase Path");
                eraseTransaction = true;
            }
            editor.PreviewVectorPath(layer, changed, false);
        }
    }

    private void EndPenErase(bool keep)
    {
        if (eraseDraftBefore is { } draft && !keep) ReplaceDraft(draft);
        if (eraseTransaction && penLayer != null && session is { IsInteracting: true } editor)
        {
            if (!keep) editor.Cancel();
            else
            {
                try { editor.CompletePathEdit(penLayer); editor.Commit(); }
                catch (InvalidOperationException error) { editor.Cancel(); Problem?.Invoke(error.Message); }
            }
        }
        eraseTransaction = false; eraseDraftBefore = null; penLayer = null;
    }

    private void ReplaceDraft(VectorPath next)
    {
        penDraft.Clear(); penDraft.AddRange(next.Nodes);
        InvalidateCanvas();
    }
}
