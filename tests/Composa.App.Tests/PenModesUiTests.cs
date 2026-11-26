using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class PenModesUiTests
{
    [AvaloniaFact]
    public void Pen_modes_select_move_bend_cut_and_erase_vectors_with_undo()
    {
        var window = new MainWindow { Width = 1440, Height = 960 };
        window.Show();
        try
        {
            var session = EditorSession.NewCanvas(600, 400, SKColors.White);
            window.AddSession(session);
            session.AddPath(new VectorPath { Nodes = [BezierNode.Corner(130, 100), BezierNode.Corner(260, 100), BezierNode.Corner(420, 155)] });
            window.SelectTool(Tool.Pen);
            Dispatcher.UIThread.RunJobs();

            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
            void Pick(string mode)
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PenMode" + mode);
                Assert.True(button.IsEnabled);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(mode, window.Canvas.PenMode.ToString());
            }
            void Drag(Point from, params Point[] points)
            {
                window.MouseDown(from, MouseButton.Left);
                foreach (var point in points) window.MouseMove(point, RawInputModifiers.LeftMouseButton);
                window.MouseUp(points[^1], MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }
            VectorPath Path() => session.ActiveLayer!.Shape!.Path!;
            SKPoint Anchor(int index)
            {
                var layer = session.ActiveLayer!;
                var node = Path().Nodes[index];
                return layer.Matrix.MapPoint((float)(node.X * layer.Pixels!.Width), (float)(node.Y * layer.Pixels.Height));
            }

            Pick("Lasso");
            Drag(At(100, 80), At(280, 80), At(280, 120), At(100, 120), At(100, 80));
            Assert.Null(session.Selection);
            Pick("Move");
            Drag(At(130, 100), At(140, 128));
            Assert.Equal(128, Anchor(0).Y, 0);
            Assert.Equal(128, Anchor(1).Y, 0);
            Assert.Equal(155, Anchor(2).Y, 0);

            Pick("Bend");
            Drag(At(205, 128), At(205, 171));
            Assert.NotEqual(Path().Nodes[0].Y, Path().Nodes[0].OutY);
            Assert.Equal(128, Anchor(0).Y, 0);

            Pick("Cut");
            Drag(At(346, 141), At(346, 141));
            Assert.Equal(5, Path().Nodes.Length);
            Assert.Contains(Path().Nodes, node => node.StartsContour);
            Assert.Equal("Cut Path", session.History.UndoName);

            Pick("Erase");
            Drag(At(210, 168), At(210, 168));
            Assert.True(Path().Nodes.Length < 5);
            Assert.Equal("Erase Path", session.History.UndoName);
            session.Undo();
            Assert.Equal(5, Path().Nodes.Length);
            session.Redo();
            Assert.True(Path().Nodes.Length < 5);
            Assert.False(session.IsInteracting);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Line_properties_show_dash_controls_and_order_filled_markers_before_hollow_markers()
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        try
        {
            var session = EditorSession.NewCanvas(600, 400, SKColors.White);
            window.AddSession(session);
            session.AddLine(new SKPoint(140, 105), new SKPoint(430, 260), SKColors.DarkSlateBlue, 6);
            Dispatcher.UIThread.RunJobs();
            T Control<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            var pattern = Control<ComboBox>("VectorStrokePattern");
            Assert.False(Control<NumericUpDown>("VectorDashLength").IsEffectivelyVisible);
            pattern.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Control<NumericUpDown>("VectorDashLength").IsEffectivelyVisible);
            Control<NumericUpDown>("VectorDashLength").Value = 21;
            Assert.Equal(21, session.ActiveLayer!.Shape!.Dash!.Intervals[0]);
            var end = Control<ComboBox>("VectorEndMarker");
            Assert.Equal(11, end.ItemCount);
            end.SelectedIndex = (int)StrokeMarker.HollowSquare;
            Assert.Equal(StrokeMarker.HollowSquare, session.ActiveLayer!.Shape!.StrokeOptions!.End);
            end.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            Screenshots.Save(window, "vector-stroke-properties");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Swatch_edge_depends_on_its_displayed_contrast_and_not_its_paint_value()
    {
        var field = Avalonia.Media.Color.Parse("#35383D");
        Assert.True(ColorSwatch.NeedsEdge(field, field));
        Assert.True(ColorSwatch.NeedsEdge(Avalonia.Media.Color.Parse("#253041"), field));
        Assert.False(ColorSwatch.NeedsEdge(Avalonia.Media.Colors.Black, field));
        Assert.False(ColorSwatch.NeedsEdge(Avalonia.Media.Colors.Orange, field));
    }

    [AvaloniaFact]
    public void The_standard_palette_uses_the_same_swatches_and_can_still_select_a_color()
    {
        var window = new MainWindow { Width = 1100, Height = 800 };
        window.Show();
        try
        {
            var picking = Prompts.Color(window, "Color", SKColors.Black);
            Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.Single();
            var view = dialog.GetVisualDescendants().OfType<ColorView>().Single();
            view.SelectedIndex = (int)ColorViewTab.Palette;
            Dispatcher.UIThread.RunJobs();
            var palette = view.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.NotEmpty(palette.GetVisualDescendants().OfType<ColorSwatch>());
            var selected = view.PaletteColors.First(c => c != view.Color);
            palette.SelectedItem = selected;
            Assert.Equal(selected, view.Color);
            dialog.Close(false);
            Dispatcher.UIThread.RunJobs();
            Assert.True(picking.IsCompleted);
        }
        finally { window.Close(); }
    }
}
