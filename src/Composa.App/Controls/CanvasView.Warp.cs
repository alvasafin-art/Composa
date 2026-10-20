using Avalonia;
using Avalonia.Input;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private int warpNode = -1;
    private WarpMesh? warpDragStart;
    private SKPoint warpNodeStart;
    private readonly Stack<WarpMesh> warpUndo = [], warpRedo = [];

    public void StartWarp()
    {
        if (session?.CanWarp != true) return;
        CancelInteraction(); session.BeginWarp(); warpNode = -1; warpUndo.Clear(); warpRedo.Clear();
        Focus(); UpdateCursor(); InvalidateVisual(); ToolStateChanged?.Invoke();
    }

    public void FinishWarp(bool keep)
    {
        if (session?.WarpEdit == null) return;
        if (drag == Drag.WarpNode) drag = Drag.None;
        warpDragStart = null;
        try { if (keep) session.CommitWarp(); else session.CancelWarp(); }
        catch (InvalidOperationException error) { Problem?.Invoke(error.Message); }
        UpdateCursor(); InvalidateVisual(); ToolStateChanged?.Invoke();
    }

    public void ResetWarp()
    {
        if (session?.WarpEdit is not { } edit || IsDragging) return;
        var before = edit.Mesh; edit.Reset(); RememberWarp(before); ToolStateChanged?.Invoke();
    }

    public void ChangeWarpGrid(int columns, int rows)
    {
        if (session?.WarpEdit is not { } edit || IsDragging) return;
        var before = edit.Mesh; edit.ChangeGrid(columns, rows); RememberWarp(before); warpNode = -1; ToolStateChanged?.Invoke();
    }

    private void RememberWarp(WarpMesh before)
    {
        if (session?.WarpEdit is not { } edit || before.Equals(edit.Mesh)) return;
        warpUndo.Push(before); warpRedo.Clear();
    }

    public void UndoWarpPoint(bool redo = false)
    {
        if (session?.WarpEdit is not { } edit || IsDragging) return;
        var from = redo ? warpRedo : warpUndo; var to = redo ? warpUndo : warpRedo;
        if (!from.TryPop(out var mesh)) return;
        to.Push(edit.Mesh); edit.SetMesh(mesh); warpNode = -1; ToolStateChanged?.Invoke();
    }

    private void NudgeWarpPoint(KeyEventArgs e)
    {
        if (session?.WarpEdit is not { } edit || warpNode < 0 || IsDragging) return;
        var before = edit.Mesh; var node = before.Points[warpNode];
        var point = edit.Layer.WarpNodePosition(node);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        point.X += e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        point.Y += e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        try { edit.MovePoint(warpNode, point); RememberWarp(before); }
        catch (ArgumentException error) { Problem?.Invoke(error.Message); }
        catch (InvalidOperationException error) { Problem?.Invoke(error.Message); }
        ToolStateChanged?.Invoke();
    }

    private void BeginWarpPoint(Point screen)
    {
        var edit = session!.WarpEdit!; var layer = edit.Layer; var mesh = edit.Mesh; var closest = 9.0;
        warpNode = -1;
        for (var i = 0; i < mesh.Points.Length; i++)
        {
            var node = mesh.Points[i]; var document = layer.WarpNodePosition(node);
            var distance = Distance(ToScreen(document), screen);
            if (distance >= closest) continue;
            closest = distance; warpNode = i; warpNodeStart = document;
        }
        if (warpNode < 0) return;
        warpDragStart = mesh; drag = Drag.WarpNode; InvalidateVisual();
    }

    private void DragWarpPoint(bool shift)
    {
        if (session?.WarpEdit is not { } edit || warpNode < 0) return;
        var point = currentDocument;
        if (shift)
        {
            if (Math.Abs(point.X - warpNodeStart.X) > Math.Abs(point.Y - warpNodeStart.Y)) point.Y = warpNodeStart.Y;
            else point.X = warpNodeStart.X;
        }
        try { edit.MovePoint(warpNode, point); }
        catch (ArgumentException error) { Problem?.Invoke(error.Message); }
        catch (InvalidOperationException error) { Problem?.Invoke(error.Message); }
    }

    private void EndWarpPoint(bool keep)
    {
        if (session?.WarpEdit is { } edit && warpDragStart is { } before)
        {
            if (keep) RememberWarp(before); else edit.SetMesh(before);
        }
        warpDragStart = null; ToolStateChanged?.Invoke();
    }

    private void CaptureWarpOverlay(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        var edit = session!.WarpEdit!; var mesh = edit.Mesh; var layer = edit.Layer;
        var matrix = layer.WarpMatrix.PostConcat(view); var size = layer.WarpSourceSize; var width = size.Width; var height = size.Height;
        SKPoint Map(WarpPoint point) => matrix.MapPoint((float)(point.X * width), (float)(point.Y * height));
        var lines = new List<SKPoint[]>();
        for (var axis = 0; axis < 2; axis++)
        {
            var count = axis == 0 ? mesh.Columns : mesh.Rows;
            for (var i = 0; i <= count; i++)
            {
                var points = new SKPoint[65];
                for (var j = 0; j < points.Length; j++) points[j] = Map(axis == 0 ? mesh.Evaluate((double)i / count, j / 64.0) : mesh.Evaluate(j / 64.0, (double)i / count));
                lines.Add(points);
            }
        }
        var nodes = mesh.Points.Select(Map).ToArray(); var selected = warpNode;
        steps.Add(canvas =>
        {
            using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(180), StrokeWidth = 3 * hair, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var light = new SKPaint { Color = Accent, StrokeWidth = hair, IsAntialias = true, Style = SKPaintStyle.Stroke };
            foreach (var points in lines)
            {
                using var path = new SKPath(); path.AddPoly(points, close: false); canvas.DrawPath(path, dark); canvas.DrawPath(path, light);
            }
            using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            for (var i = 0; i < nodes.Length; i++)
            {
                fill.Color = i == selected ? Accent : SKColors.White; var p = nodes[i];
                var box = SKRect.Create(p.X - 3.5f * hair, p.Y - 3.5f * hair, 7 * hair, 7 * hair);
                canvas.DrawRect(box, fill); canvas.DrawRect(box, dark);
            }
        });
    }
}
