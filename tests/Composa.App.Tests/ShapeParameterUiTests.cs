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
using Composa.IO;
using SkiaSharp;

namespace Composa.App.Tests;

public class ShapeParameterUiTests
{
    [AvaloniaFact]
    public void Brush_controls_wrap_at_the_available_width_and_show_dynamics_in_Russian()
    {
        var window = new MainWindow { Width = 1750, Height = 850 };
        window.Show();
        var session = EditorSession.NewCanvas(640, 480);
        window.AddSession(session);
        try
        {
            Locale.Apply("ru", window, null);
            window.SelectTool(Tool.Brush);
            void CheckLayout(bool narrow)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var bar = window.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Classes.Contains("options"));
                Assert.Contains(bar.GetVisualDescendants().OfType<CheckBox>(), c => Equals(c.Content, "Размер от нажима"));
                var bounds = bar.Children.Where(c => c.IsVisible).Select(c => c.Bounds).ToArray();
                Assert.All(bounds, b => Assert.InRange(b.Right, 0, bar.Bounds.Width + 1));
                Assert.Equal(narrow, bounds.Last().Center.Y > bounds.First().Bottom);
                Assert.True(Screenshots.Save(window, narrow ? "brush-options-ru-narrow" : "brush-options-ru-wide"));
            }
            CheckLayout(false);
            window.Width = 900;
            CheckLayout(true);
        }
        finally { Locale.Apply("en", window, null); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Click_configures_star_then_canvas_handles_and_opacity_update_the_same_object()
    {
        var window=new MainWindow { Width=1550,Height=900 }; window.Show();
        var session=EditorSession.NewCanvas(640,480); window.AddSession(session);
        try
        {
            session.ShapeKind=ShapeKind.Star; window.SelectTool(Tool.Shape); Dispatcher.UIThread.RunJobs();
            Point At(SKPoint doc) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(doc),window)!.Value;
            var point=At(new(100,100)); window.MouseDown(point,MouseButton.Left); window.MouseUp(point,MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var dialog=Assert.Single(window.OwnedWindows);
            var count=dialog.GetVisualDescendants().OfType<SliderField>().Single(f=>f.Label=="Points");
            count.Focus();
            for(int i=0;i<2;i++) {dialog.KeyPressQwerty(PhysicalKey.ArrowRight,RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.ArrowRight,RawInputModifiers.None);}
            Assert.Equal(7,count.Value);
            var create=dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.IsDefault);
            create.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for(int i=0;i<80 && session.ActiveLayer?.Shape==null;i++) {await Task.Delay(10); Dispatcher.UIThread.RunJobs();}
            var layer=session.ActiveLayer!;
            Assert.Equal(7, layer.Shape!.Points); Assert.Equal(ShapeKind.Star, layer.Shape.Kind);
            var alpha=window.GetVisualDescendants().OfType<NumericUpDown>().Single(c=>c.Name=="LayerOpacity");
            alpha.Value=37; Assert.Equal(.37,layer.Opacity,4);
            var mid=At(layer.Matrix.MapPoint(layer.Pixels!.Width,layer.Pixels.Height/2));
            var handle=mid-new Vector(14,0); var end=handle+new Vector(16,0);
            window.MouseDown(handle,MouseButton.Left); window.MouseMove(end,RawInputModifiers.LeftMouseButton); window.MouseUp(end,MouseButton.Left);
            Assert.Equal(9,layer.Shape.Points);
            using var file=new MemoryStream(); ProjectFile.Write(session.Document,file); file.Position=0;
            Assert.Equal(layer.Shape,ProjectFile.Read(file).Layers.Last().Shape);
            window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
            Assert.True(Screenshots.Save(window,"shape-star-properties"));
        }
        finally {session.MarkSaved("shape.cmps",session.History.CurrentId);window.Close();}
    }
}
