using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class WarpUiTests
{
    [AvaloniaFact]
    public void Mesh_nodes_follow_pointer_local_undo_redo_stays_open_and_Enter_commits_one_step()
    {
        var window = new MainWindow { Width = 1360, Height = 860 }; window.Show();
        var session = EditorSession.NewCanvas(600, 420, SKColors.White);
        try
        {
            session.ChangeColorDepth(ColorDepth.Sixteen); session.AssignColorProfile(RgbProfile.DisplayP3);
            var image = Pixels.NewWorkingColor(360, 270, ColorDepth.Sixteen, RgbProfile.DisplayP3);
            using (var canvas = new SKCanvas(image))
            {
                using var paint = new SKPaint { Color = SKColors.CornflowerBlue }; canvas.DrawRect(12, 12, 336, 246, paint);
                paint.Color = SKColors.Gold;
                for (var y = 24; y < 240; y += 36) for (var x = 24; x < 330; x += 36) canvas.DrawRect(x, y, 18, 18, paint);
            }
            var layer = session.AddImageLayer("Grid material", image, fit: false); var bytes = layer.Pixels!.Bytes;
            window.AddSession(session); window.StartWarp(); Dispatcher.UIThread.RunJobs();
            var history = session.History.Count; var edit = session.WarpEdit!; var node = Node(edit.Layer, edit.Mesh, 5); var target = node + new SKPoint(-32, -25);
            Drag(window, node, target); Assert.NotNull(session.WarpEdit); Assert.False(window.IsDragging); Assert.Equal(history, session.History.Count);
            var changed = edit.Mesh; var actual = Node(edit.Layer, changed, 5); Assert.InRange(SKPoint.Distance(actual, target), 0, .001f);
            Assert.False(window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "WarpGrid").IsEnabled);
            window.Canvas.Focus(); window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs(); Assert.True(edit.Mesh.IsIdentity);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift); Dispatcher.UIThread.RunJobs(); Assert.Equal(changed, edit.Mesh);
            Assert.Equal(bytes, layer.Pixels.Bytes); Assert.True(Screenshots.Save(window, "warp-grid-rgb16"));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.Null(session.WarpEdit); Assert.Equal(changed, layer.Warp); Assert.Equal(history + 1, session.History.Count);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs(); Assert.Null(session.ActiveLayer!.Warp);
        }
        finally { session.MarkSaved("warp-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Save_and_tab_change_finish_the_mesh_and_pre_Warp_mask_stays_editable_in_the_source()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-warp-" + Guid.NewGuid().ToString("N") + ".cmps");
        var window = new MainWindow { Width = 1280, Height = 820 }; window.Show();
        var session = EditorSession.NewCanvas(420, 300, SKColors.Coral);
        try
        {
            var layer = session.ActiveLayer!; session.AddMask(layer); var mask = layer.Mask; session.EditingMask = false;
            window.AddSession(session); window.StartWarp(); Dispatcher.UIThread.RunJobs(); var edit = session.WarpEdit!;
            Drag(window, Node(layer, edit.Mesh, 5), new(105, 65)); var expected = edit.Mesh;
            Assert.Null(await window.SaveTo(session, path)); Assert.Null(session.WarpEdit); Assert.False(session.IsModified);
            Assert.Equal(expected, ProjectFile.Load(path).ActiveLayer!.Warp); Assert.Same(mask, layer.SmartObject!.OpenDocument().ActiveLayer!.Mask);
            window.StartWarp(); Dispatcher.UIThread.RunJobs(); Drag(window, Node(layer, session.WarpEdit!.Mesh, 6), new(290, 75));
            var tabMesh = session.WarpEdit!.Mesh; window.AddSession(EditorSession.NewCanvas(80, 60));
            Assert.Null(session.WarpEdit); Assert.Equal(tabMesh, layer.Warp); Assert.True(session.IsModified);
        }
        finally { session.MarkSaved(path, session.History.CurrentId); window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public void Escape_during_a_node_drag_restores_source_and_grid_can_change_only_after_Reset()
    {
        var window = new MainWindow { Width = 1280, Height = 820 }; window.Show();
        var session = EditorSession.NewCanvas(420, 300, SKColors.CornflowerBlue);
        try
        {
            window.AddSession(session); var original = session.ActiveLayer!.Pixels; var history = session.History.Count;
            window.StartWarp(); Dispatcher.UIThread.RunJobs(); var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "WarpGrid");
            combo.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); Assert.Equal(4, session.WarpEdit!.Mesh.Columns);
            var from = Node(session.ActiveLayer!, session.WarpEdit.Mesh, 6); var to = from + new SKPoint(-25, -20);
            window.MouseDown(At(window, from), MouseButton.Left); window.MouseMove(At(window, to), RawInputModifiers.LeftMouseButton); Assert.True(window.IsDragging);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.MouseUp(At(window, to), MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsDragging); Assert.Null(session.WarpEdit); Assert.Null(session.ActiveLayer!.Warp); Assert.Same(original, session.ActiveLayer.Pixels); Assert.Equal(history, session.History.Count);
        }
        finally { session.MarkSaved("warp-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Filter_mask_brush_and_reopened_grid_use_visible_document_coordinates_after_filter_padding()
    {
        var window = new MainWindow { Width = 1280, Height = 820 }; window.Show();
        var document = new Document(360, 260) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var image = Pixels.NewWorkingColor(100, 80, document.Depth, document.Profile); image.Erase(SKColors.CornflowerBlue);
        var layer = Layer.Raster("Warped object", image, 80, 80); document.InsertAboveActive(layer); var session = new EditorSession(document);
        try
        {
            var mesh = WarpMesh.Create(); for (var i = 0; i < mesh.Points.Length; i++) mesh = mesh.WithPoint(i, mesh.Points[i] + new WarpPoint(.4, 0));
            session.SetWarp(layer, mesh); session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 3 });
            session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = 65, Seed = 17 });
            using var full = session.Flatten(); window.AddSession(session); session.AddSmartFilterMask(layer, hideAll: true);
            session.Foreground = SKColors.White; session.Brush = new() { Size = 14, Hardness = 1, Opacity = 1 }; window.SelectTool(Tool.Brush);
            Dispatcher.UIThread.RunJobs(); window.Canvas.Fit(); window.Canvas.ZoomTo(2); Dispatcher.UIThread.RunJobs(); Drag(window, new(200, 120), new(201, 120));
            using var painted = session.Flatten(); Assert.Equal(full.GetPixel(200, 120), painted.GetPixel(200, 120));
            window.MouseMove(At(window, new(290, 200))); Assert.True(Screenshots.Save(window, "warp-filter-mask-pointer-rgb16")); Assert.Equal(2, window.Canvas.Zoom);
            session.EditRgbChannels(); window.StartWarp(); Dispatcher.UIThread.RunJobs(); var expected = layer.WarpNodePosition(mesh.Points[5]) + new SKPoint(4, -3);
            Drag(window, layer.WarpNodePosition(mesh.Points[5]), expected);
            Assert.InRange(SKPoint.Distance(expected, layer.WarpNodePosition(session.WarpEdit!.Mesh.Points[5])), 0, .001f);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Assert.Null(session.WarpEdit);
            Assert.Equal(mesh, session.ActiveLayer!.Warp); Assert.NotNull(session.ActiveLayer.SmartFilterMask);
        }
        finally { session.MarkSaved("warp-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    private static SKPoint Node(Layer layer, WarpMesh mesh, int index)
    { var p = mesh.Points[index]; return layer.Matrix.MapPoint((float)(p.X * layer.Pixels!.Width), (float)(p.Y * layer.Pixels.Height)); }
    private static Point At(MainWindow window, SKPoint point) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(point), window)!.Value;
    private static void Drag(MainWindow window, SKPoint from, SKPoint to)
    {
        window.MouseDown(At(window, from), MouseButton.Left);
        for (var i = 1; i <= 6; i++) window.MouseMove(At(window, new(from.X + (to.X - from.X) * i / 6, from.Y + (to.Y - from.Y) * i / 6)), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(window, to), MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
}
