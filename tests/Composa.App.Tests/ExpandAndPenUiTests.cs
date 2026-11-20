using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class ExpandAndPenUiTests
{
    [AvaloniaFact]
    public void Pen_edits_selected_live_shapes_directly_and_undo_restores_the_primitive()
    {
        var window = new MainWindow { Width = 1280, Height = 820 };
        window.Show();
        var session = EditorSession.NewCanvas(320, 220);
        window.AddSession(session);
        var rectangle = session.AddShape(new ShapeStyle(ShapeKind.Rectangle, 0xff2580aa, 0), new SKRect(30, 40, 120, 110))!;
        var ellipse = session.AddShape(new ShapeStyle(ShapeKind.Ellipse, 0xff30945a, 0), new SKRect(150, 60, 270, 160))!;
        window.SelectTool(Tool.Pen);

        Point At(SKPoint p) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(p), window)!.Value;
        void EditAnchor(Layer original, SKPoint anchor)
        {
            Dispatcher.UIThread.RunJobs();
            var state = session.History.CurrentId;
            Assert.Equal(original.Id, session.ActiveLayer!.Id);
            Assert.Same(original.Pixels, session.ActiveLayer.Pixels);
            Assert.Null(session.ActiveLayer.Shape!.Path);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Edit points"));
            var start = At(anchor);
            window.MouseDown(start, MouseButton.Left);
            window.MouseUp(start, MouseButton.Left);
            Assert.Equal(state, session.History.CurrentId);
            Assert.Null(session.ActiveLayer!.Shape!.Path);
            window.MouseDown(start, MouseButton.Left);
            var end = At(new SKPoint(anchor.X + 8, anchor.Y + 10));
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(original.Id, session.ActiveLayer!.Id);
            Assert.Equal(ShapeKind.Path, session.ActiveLayer.Shape!.Kind);
            Assert.Equal(4, session.ActiveLayer.Shape.Path!.Nodes.Length);
            session.Undo();
            Assert.Equal(state, session.History.CurrentId);
            Assert.Null(session.ActiveLayer!.Shape!.Path);
        }

        Screenshots.Save(window, "pen-selected-ellipse");
        EditAnchor(ellipse, new SKPoint(210, 60));
        session.SelectLayer(rectangle.Id);
        EditAnchor(rectangle, new SKPoint(30, 40));
        window.Close();
    }

    [AvaloniaFact]
    public void Alt_on_the_last_anchor_keeps_the_incoming_curve_and_next_edge_is_straight()
    {
        var w=new MainWindow { Width=1280,Height=820 }; w.Show(); var s=EditorSession.NewCanvas(320,220);
        w.AddSession(s); w.SelectTool(Tool.Pen); Dispatcher.UIThread.RunJobs();
        Point At(float x,float y) => w.Canvas.TranslatePoint(w.Canvas.ToScreen(new SKPoint(x,y)),w)!.Value;
        void Click(float x,float y,RawInputModifiers keys=RawInputModifiers.None)
        { w.MouseDown(At(x,y),MouseButton.Left,keys);w.MouseUp(At(x,y),MouseButton.Left,keys); }
        Click(30,160); w.MouseDown(At(130,70),MouseButton.Left);
        w.MouseMove(At(165,50),RawInputModifiers.LeftMouseButton); w.MouseUp(At(165,50),MouseButton.Left);
        Click(130,70,RawInputModifiers.Alt); Click(265,150); w.Canvas.FinishPen();
        var layer=s.ActiveLayer!; var path=layer.Shape!.Path!; var n=path.Nodes[1];
        Assert.NotEqual(n.X,n.InX); Assert.Equal(n.X,n.OutX); Assert.Equal(n.Y,n.OutY);
        Click(197.5f,110,RawInputModifiers.Shift); Assert.Equal(4,s.ActiveLayer!.Shape!.Path!.Nodes.Length);
        w.KeyPress(Key.Delete,RawInputModifiers.None,PhysicalKey.Delete,null); Dispatcher.UIThread.RunJobs();
        Assert.Equal(3,s.ActiveLayer!.Shape!.Path!.Nodes.Length);
        Click(231.25f,130); w.KeyPress(Key.Delete,RawInputModifiers.None,PhysicalKey.Delete,null);
        Dispatcher.UIThread.RunJobs(); Assert.Equal(2,s.ActiveLayer!.Shape!.Path!.Nodes.Length);
        s.Undo(); Assert.Equal(3,s.ActiveLayer!.Shape!.Path!.Nodes.Length); w.Close();
    }

    [AvaloniaFact]
    public void Crop_has_expand_and_photoshop_is_only_in_scripts()
    {
        var w = new MainWindow { Width = 1280, Height = 850 }; w.Show(); w.AddSession(EditorSession.NewCanvas(300, 200)); w.SelectTool(Tool.Crop);
        w.AiTasks.SetConnectedForTests(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(w.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Generative expand" && b.IsEnabled);
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
