using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

public class IndependentMaskUiTests
{
    [AvaloniaFact]
    public void Link_button_and_real_canvas_drag_edit_the_mask_and_inspector_without_moving_the_layer()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(400, 300, SKColors.White); window.AddSession(session);
            var image = Pixels.NewColor(200, 140); image.Erase(new SKColor(40, 140, 220));
            var layer = session.AddImageLayer("Product", image, fit: false); var frame = layer.Transform;
            session.AddMask(layer); var mask = SelectionMask.FromRect(200, 140, new(40, 15, 190, 130));
            session.RunTransaction("Test mask", _ => layer.Mask = mask); session.NotifyLayersChanged();
            Dispatcher.UIThread.RunJobs();
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b)?.ToString()?.StartsWith("Unlink mask:") == true);
            var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.False(layer.MaskLinked); Assert.True(session.IsEditingMask);
            window.SelectTool(Tool.Move); window.Canvas.ShowTransformControls = true; window.Canvas.AutoSelect = true;
            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
            var origin = layer.MaskMatrix.MapPoint(90, 65);
            var start = At(origin.X, origin.Y); var end = At(origin.X + 25, origin.Y + 10);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(frame, layer.Transform); Assert.Same(image, layer.Pixels); Assert.Same(mask, layer.Mask);
            var moved = layer.MaskMatrix.MapPoint(90, 65);
            Assert.InRange(Math.Abs(moved.X - origin.X - 25), 0, .1); Assert.InRange(Math.Abs(moved.Y - origin.Y - 10), 0, .1);
            Assert.True(Screenshots.Save(window, "independent-mask-transform"));
            var x = window.GetVisualDescendants().OfType<NumericUpDown>().First(b => b.Value == (decimal)session.EditingControlTransform(layer).X);
            x.Value += 5; Dispatcher.UIThread.RunJobs();
            Assert.Equal(frame, layer.Transform); Assert.InRange(Math.Abs(layer.MaskMatrix.MapPoint(90, 65).X - moved.X - 5), 0, .1);
            session.Undo(); Assert.Equal(frame, session.ActiveLayer!.Transform);
        }
        finally { window.Close(); }
    }
}
