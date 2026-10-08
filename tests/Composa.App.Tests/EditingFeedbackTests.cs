using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Tests;

public class EditingFeedbackTests
{
    [AvaloniaFact]
    public void Collapsed_selection_panel_stays_collapsed_across_selections_and_tabs_until_clicked()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        window.AiTasks.SetConnectedForTests();
        var first = EditorSession.NewCanvas(600, 400, SKColors.White); window.AddSession(first);
        first.SelectRect(new SKRect(80, 80, 200, 200)); Dispatcher.UIThread.RunJobs();
        var toggle = window.AiFloatingPanel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiFloatingCollapse");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.Settings.AiFloatingCollapsed); Assert.True(window.AiFloatingVisible);
        first.Deselect(); first.SelectRect(new SKRect(90, 90, 210, 210));
        var second = EditorSession.NewCanvas(600, 400, SKColors.White); window.AddSession(second); second.SelectAll();
        Dispatcher.UIThread.RunJobs(); Assert.Equal(210, window.AiFloatingPanel.Width);
        Assert.False(window.AiFloatingPanel.GetVisualDescendants().OfType<TextBox>().Single(t => t.AcceptsReturn).IsEffectivelyVisible);
        Assert.True(Settings.FromJson(System.Text.Json.JsonSerializer.Serialize(window.Settings)).AiFloatingCollapsed);
        Screenshots.Save(window, "feedback-collapsed-selection-panel");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.False(window.Settings.AiFloatingCollapsed); Assert.Equal(520, window.AiFloatingPanel.Width); window.Close();
    }

    [AvaloniaFact]
    public void Flip_buttons_follow_alignment_and_apply_undoable_layer_transforms()
    {
        var window = new MainWindow { Width = 1600, Height = 900 }; window.Show();
        var session = EditorSession.NewCanvas(600, 400, SKColors.White); window.AddSession(session); Dispatcher.UIThread.RunJobs();
        Button Named(string name) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
        var horizontal = Named("FlipHorizontal"); var vertical = Named("FlipVertical"); var align = Named("DistributeVertical");
        Assert.True(horizontal.TranslatePoint(default, window)!.Value.X >= align.TranslatePoint(default, window)!.Value.X + align.Bounds.Width + 10);
        horizontal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(session.ActiveLayer!.Transform.FlipHorizontal);
        vertical.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(session.ActiveLayer.Transform.FlipVertical);
        session.Undo(); Assert.False(session.ActiveLayer!.Transform.FlipVertical); Assert.True(session.ActiveLayer.Transform.FlipHorizontal);
        Screenshots.Save(window, "feedback-alignment-and-flips");
        window.SelectTool(Tool.ObjectSelectionAi); session.SelectAll(); Dispatcher.UIThread.RunJobs();
        Assert.True(Named("SelectionModify").IsEnabled);
        window.SelectTool(Tool.SelectionBrush); Dispatcher.UIThread.RunJobs(); Assert.True(Named("SelectionModify").IsEnabled); window.Close();
    }

    [AvaloniaFact]
    public void Curve_black_and_white_points_move_horizontally_without_jumping_to_the_grab_position()
    {
        var curve = new CurveEditor { Width = 300, Height = 300 };
        var window = new Window { Width = 340, Height = 340, Content = curve }; window.Show(); Dispatcher.UIThread.RunJobs();
        Point At(double x, double y) => curve.TranslatePoint(new Point(x, y), window)!.Value;
        window.MouseDown(At(3, curve.Bounds.Height - 3), MouseButton.Left);
        window.MouseMove(At(53, curve.Bounds.Height - 3)); window.MouseUp(At(53, curve.Bounds.Height - 3), MouseButton.Left);
        Assert.InRange(curve.Curves.Channels[0][0].X, 44, 46); Assert.Equal(0, curve.Curves.Channels[0][0].Y);
        var right = curve.Curves.Channels[0][1];
        window.MouseDown(At(curve.Bounds.Width - 3, 3), MouseButton.Left);
        window.MouseMove(At(curve.Bounds.Width - 53, 3)); window.MouseUp(At(curve.Bounds.Width - 53, 3), MouseButton.Left);
        Assert.True(curve.Curves.Channels[0][1].X < right.X); Assert.Equal(255, curve.Curves.Channels[0][1].Y);
        Screenshots.Save(window, "feedback-curve-endpoints"); window.Close();
    }
}
