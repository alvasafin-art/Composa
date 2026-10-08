using Avalonia.Headless.XUnit;
using Composa.AI;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class SceneMatchingUiTests
{
    [AvaloniaFact]
    public async Task Local_match_is_visible_editable_and_preserves_subject_geometry()
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
            await window.AiTasks.RunAsync(new EditorCommandService(session),new(){Task=AiTaskKind.MatchToScene},TestContext.Current.CancellationToken);
            Assert.Equal(original,subject.Bytes); Assert.Equal(4,session.Document.Layers.Count);
            Assert.True(Screenshots.Save(window,"match-scene-after"));
            Assert.Equal(before.GetPixel(0,0),session.Flatten().GetPixel(0,0));
            Assert.NotEqual(before.GetPixel(300,220),session.Flatten().GetPixel(300,220));
            session.Undo(); Assert.Equal(before.Bytes,session.Flatten().Bytes);
        }
        finally {window.Close();}
    }
}
