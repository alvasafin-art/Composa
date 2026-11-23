using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class SceneMatchingUiTests
{
    [AvaloniaFact]
    public void Local_match_runs_from_the_generation_panel_and_preserves_subject_geometry()
    {
        var window=new MainWindow {Width=1280,Height=900}; window.Settings.CheckForUpdates=false; window.Show();
        try
        {
            var session=EditorSession.NewCanvas(600,400,new SKColor(167,155,140)); window.AddSession(session);
            var subject=Pixels.NewColor(140,140);
            using(var canvas=new SKCanvas(subject))
            using(var shader=SKShader.CreateRadialGradient(new(48,36),150,[new(170,184,209),new(45,55,80)],null,SKShaderTileMode.Clamp))
            using(var paint=new SKPaint {Shader=shader,IsAntialias=true}) canvas.DrawCircle(70,70,68,paint);
            session.AddImageLayer("Subject",subject,new(300,220),fit:false);
            var original=subject.Bytes; using var before=Pixels.Clone(session.Flatten());
            Assert.True(Screenshots.Save(window,"match-scene-before"));
            const string engine = "chatgpt-image-2.5";
            const AiTaskKind task = AiTaskKind.MatchToScene;
            window.Settings.AiTaskEngineIds[task.ToString()] = engine;
            var profile = window.Settings.OperationFor(engine, task, paid: true);
            window.Settings.SetOperation(engine, task, profile with { Scene = profile.Scene with { MatchMode = SceneMatchMode.ToneAndColor } });
            var ai = window.GetVisualDescendants().OfType<MenuItem>().Single(item => item.Header?.ToString()?.Replace("_", "") == "AI");
            ai.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Match to Scene")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetVisualDescendants().OfType<ScrollViewer>().Single(view => view.Name == "AiGenerationDock");
            Assert.Empty(window.OwnedWindows);
            Assert.False(panel.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn).IsEnabled);
            panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AiRun").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(AiOperationStatus.Completed, window.AiTasks.Operation?.Status);
            Assert.Equal(original,subject.Bytes); Assert.Equal(4,session.Document.Layers.Count);
            Assert.True(Screenshots.Save(window,"match-scene-after"));
            Assert.Equal(before.GetPixel(0,0),session.Flatten().GetPixel(0,0));
            Assert.NotEqual(before.GetPixel(300,220),session.Flatten().GetPixel(300,220));
            session.Undo(); Assert.Equal(before.Bytes,session.Flatten().Bytes);
        }
        finally {window.Close();}
    }
}
