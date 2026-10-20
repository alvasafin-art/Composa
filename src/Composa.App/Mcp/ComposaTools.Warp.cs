using System.ComponentModel;
using Composa.Editing;
using Composa.Model;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "warp_layer")]
    [Description("Non-destructively deforms a layer with an interpolating Warp grid. points contains [column,row,canvasX,canvasY] for the nodes to move; untouched nodes retain their positions. Indices start at zero. New grids default to 3 by 3 divisions (4 by 4 nodes); existing grids keep their dimensions. inspect_document/get_document_state reports existing node positions. reset removes Warp. The source stays editable; pre-Warp masks are retained inside a smart object. One Undo step, without changing the person's active layer.")]
    public Task<string> WarpLayer(string layer, double[][]? points = null, int? columns = null, int? rows = null, bool reset = false, int? document = null) => OnUi(() =>
    {
        var s = Editable(document); var target = Find(s, layer);
        if (reset)
        {
            if (points is { Length: > 0 }) throw new McpException("Give either reset or points.");
            s.SetWarp(target, null); return $"Warp removed from \"{target.Name}\"; source pixels retained.";
        }
        if (points == null || points.Length == 0 || points.Length > (DocumentLimits.MaxWarpGridDivisions + 1) * (DocumentLimits.MaxWarpGridDivisions + 1))
            throw new McpException("Give a bounded list of [column,row,canvasX,canvasY] nodes, or reset=true.");
        if (target.Warp is { } existing && (columns != null && columns != existing.Columns || rows != null && rows != existing.Rows))
            throw new McpException("Reset the existing Warp before choosing another grid size.");
        var mesh = EditorSession.WarpGridFor(target, columns ?? 3, rows ?? 3);
        if (!target.Matrix.TryInvert(out var inverse)) throw new McpException("The layer transform is singular.");
        foreach (var point in points)
        {
            if (point == null || point.Length != 4 || !point.All(double.IsFinite) || point[0] != Math.Floor(point[0]) || point[1] != Math.Floor(point[1])
                || point[0] < 0 || point[0] > mesh.Columns || point[1] < 0 || point[1] > mesh.Rows)
                throw new McpException("Each node is [integer column,integer row,canvasX,canvasY], within the grid.");
            var local = inverse.MapPoint((float)point[2], (float)point[3]);
            mesh = mesh.WithPoint((int)point[1] * (mesh.Columns + 1) + (int)point[0], new((double)local.X / target.Pixels!.Width, (double)local.Y / target.Pixels.Height));
        }
        s.SetWarp(target, mesh);
        return $"\"{target.Name}\": Warp grid {mesh.Columns}×{mesh.Rows}, {points.Length} nodes placed; source retained.";
    });
}
