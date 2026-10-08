using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class ExpandAndPenUiTests
{
    [AvaloniaFact]
    public void Crop_has_expand_and_photoshop_is_only_in_scripts()
    {
        var w = new MainWindow { Width = 1280, Height = 850 }; w.Show(); w.AddSession(EditorSession.NewCanvas(300, 200)); w.SelectTool(Tool.Crop);
        w.AiTasks.SetConnectedForTests(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(w.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Gen Expand" && b.IsEnabled);
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<MenuItem>(), m => m.Header as string == "Send Image to Photoshop");
        Screenshots.Save(w, "crop-restored-gen-expand"); w.Close();
    }

    [AvaloniaFact]
    public void Double_click_closes_draft_and_ctrl_edits_points_with_one_undo_step()
    {
        var w = new MainWindow { Width = 1280, Height = 850 }; w.Show(); var s = EditorSession.NewCanvas(300, 200, SKColors.White); w.AddSession(s); w.SelectTool(Tool.Pen);
        Dispatcher.UIThread.RunJobs(); var canvas = w.GetVisualDescendants().OfType<CanvasView>().Single();
        Point At(float x, float y) => canvas.TranslatePoint(canvas.ToScreen(new SKPoint(x, y)), w)!.Value;
        void Click(float x, float y) { w.MouseDown(At(x, y), MouseButton.Left); w.MouseUp(At(x, y), MouseButton.Left); }
        Click(35, 30); Click(230, 30); Click(200, 155); Click(200, 155);
        Assert.True(s.ActiveLayer!.Shape!.Path!.Closed); Assert.Equal(3, s.ActiveLayer.Shape.Path.Nodes.Length);
        var layer = s.ActiveLayer; var original = layer.Shape.Path.Nodes[0]; var p = layer.Matrix.MapPoint((float)original.X * layer.Pixels!.Width, (float)original.Y * layer.Pixels.Height);
        var state = s.History.CurrentId;
        w.MouseDown(At(p.X, p.Y), MouseButton.Left, RawInputModifiers.Control); w.MouseMove(At(p.X + 16, p.Y + 12), RawInputModifiers.Control); w.MouseUp(At(p.X + 16, p.Y + 12), MouseButton.Left, RawInputModifiers.Control);
        Assert.NotEqual(original, s.ActiveLayer!.Shape!.Path!.Nodes[0]); Assert.False(s.IsInteracting); s.Undo(); Assert.Equal(state, s.History.CurrentId);
        Assert.Equal(original, s.ActiveLayer!.Shape!.Path!.Nodes[0]); w.Close();
    }
}
