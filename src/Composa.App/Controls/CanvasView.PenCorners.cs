using Avalonia;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private const int PenCornerHandle = 4;
    private (VectorCorner Corner, float Travel)? penCornerDrag;

    private bool ShowPenCorners() => !EditVectorMask && session?.ActiveLayer is { Shape.Kind: ShapeKind.Path } layer
        && session.Document.SelectedLayerIds.Count == 1 && !session.PixelsLocked(layer)
        && (session.Tool == Tool.Shape || PenMode is PenEditMode.Draw or PenEditMode.Move);

    private IEnumerable<(int Node, VectorCorner Corner, SKPoint Position, float Travel)> PenCornerHandles(
        VectorPath path, SKMatrix matrix, int width, int height)
    {
        for (int i = 0; i < path.Nodes.Length; i++)
        {
            if (path.CornerAt(i, width, height) is not { } corner) continue;
            var travel = corner.MaximumRadius / MathF.Tan(corner.Angle / 2);
            var scale = SKPoint.Distance(matrix.MapPoint(corner.Anchor), matrix.MapPoint(corner.AlongBisector(1))) * UnitsPerPixel;
            if (!(travel > 0 && scale * travel >= 10)) continue;
            var offset = (float)Math.Min(14 / scale, travel);
            var position = corner.AlongBisector(offset + travel * corner.Radius / corner.MaximumRadius);
            yield return (i, corner, position, travel);
        }
    }

    private bool BeginPenCorner(Layer layer, VectorPath path)
    {
        if (!ShowPenCorners() || session == null) return false;
        foreach (var handle in PenCornerHandles(path, penPathFrame.Matrix, penPathFrame.Width, penPathFrame.Height))
        {
            if (Distance(ToScreen(layer.Matrix.MapPoint(handle.Position)), pressScreen) > 6) continue;
            ClearPenSelection();
            penSelectedLayer = layer.Id;
            penSelectedNode = penNode = handle.Node;
            penHandle = PenCornerHandle;
            penCornerDrag = (handle.Corner, handle.Travel);
            penLayer = layer;
            penOriginal = path;
            penDraftDrag = false;
            session.Begin("Edit Path");
            drag = Drag.PenNode;
            return true;
        }
        return false;
    }

    private void MovePenCorner(Layer layer, VectorPath path, SKPoint delta)
    {
        if (penCornerDrag is not { } edit) return;
        var (corner, travel) = edit;
        var bisector = corner.Bisector;
        var distance = delta.X * bisector.X + delta.Y * bisector.Y;
        var radius = Math.Clamp(corner.Radius + distance * corner.MaximumRadius / travel, 0, corner.MaximumRadius);
        var nodes = path.Nodes.ToArray();
        nodes[penNode] = nodes[penNode] with { CornerRadius = radius };
        PreviewPenOutline(layer, path with { Nodes = nodes });
    }
}
