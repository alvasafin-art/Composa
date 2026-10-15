using System.Text.Json;
using Composa.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class SceneShadowReplayTests
{
    public static bool Enabled=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_SHADOW_REPLAY"));

    [Fact(Skip="Set COMPOSA_SHADOW_REPLAY, PROJECT and OUTPUT to re-extract saved photographic model outputs without regenerating them.",SkipUnless=nameof(Enabled))]
    public void Saved_photographic_model_outputs_keep_their_shadow_and_original_png_geometry()
    {
        var token=TestContext.Current.CancellationToken;
        var folder=Environment.GetEnvironmentVariable("COMPOSA_SHADOW_REPLAY")!;
        var output=Environment.GetEnvironmentVariable("COMPOSA_SHADOW_OUTPUT")!;Directory.CreateDirectory(output);
        using var file=File.OpenRead(Environment.GetEnvironmentVariable("COMPOSA_SHADOW_PROJECT")!);
        var document=ProjectFile.Read(file);var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};
        using var input=SceneEditInput.Prepare(document.Clone(),request,token);
        var metrics=new List<object>();
        foreach(var name in new[] {"short","legacy"})
        {
            using var raw=ImageFiles.Load(Path.Combine(folder,name+"-raw.png"));
            using var baseline=ImageFiles.Load(Path.Combine(folder,name+"-baseline.png"));
            var result=SceneEditResult.Extract(input,raw,request,token,reconstruction:baseline);
            var subject=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-object"));
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow"));
            var solid=0;long mass=0;
            for(var y=0;y<raw.Height;y++)for(var x=0;x<raw.Width;x++)
            {
                Assert.Equal(input.ObjectAlpha.GetPixel(x,y).Alpha,subject.Pixels.GetPixel(x,y).Alpha);
                var a=shadow.Pixels.GetPixel(x,y).Alpha;mass+=a;if(a>=8)solid++;
            }
            var session=new EditorSession(document.Clone());
            session.InsertSceneEdit(request.Task,input.SourceLayerId,[result],true,false,input.PreviousResultIds,input.SourceFrame);
            using var composite=session.Flatten();ImageFiles.Save(composite,Path.Combine(output,name+"-composite.png"),ExportFormat.Png);
            ImageFiles.Save(shadow.Pixels,Path.Combine(output,name+"-shadow.png"),ExportFormat.Png);
            using var preview=Pixels.NewColor(raw.Width,raw.Height);preview.Erase(SKColors.White);
            using(var draw=new SKCanvas(preview))draw.DrawImage(Pixels.ImageOf(shadow.Pixels),0,0);
            Pixels.Invalidate(preview);ImageFiles.Save(preview,Path.Combine(output,name+"-shadow-on-white.png"),ExportFormat.Png);
            metrics.Add(new {name,result.ShadowPixels,solid,mass,result.ContactCorrectionX,result.ContactCorrectionY});
        }
        File.WriteAllText(Path.Combine(output,"metrics.json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions {WriteIndented=true}));
    }
}
