using System.Text.Json;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class SceneEditingLiveTests
{
    public static bool Enabled=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_SCENE_TEST_URL"));
    [Fact(Skip="Set COMPOSA_SCENE_TEST_URL, INPUT and OUTPUT to run real photographic scene editing.",SkipUnless=nameof(Enabled))]
    public async Task Real_PNG_scene_edits_preserve_sources_and_record_visual_results()
    {
        var input=Environment.GetEnvironmentVariable("COMPOSA_SCENE_TEST_INPUT")!;
        var folder=Environment.GetEnvironmentVariable("COMPOSA_SCENE_TEST_OUTPUT")!; Directory.CreateDirectory(folder);
        var cases=(Environment.GetEnvironmentVariable("COMPOSA_SCENE_TEST_CASES")??"sunset-0.55,sunset-0.8,sunset-1").Split(',');
        var metrics=new List<object>();
        foreach(var item in cases)
        {
            var parts=item.Split('-'); var scene=parts[0]; var denoise=double.Parse(parts[1],System.Globalization.CultureInfo.InvariantCulture);
            using var file=File.OpenRead(Path.Combine(input,(scene=="daylight"?"daylight":"sunset")+"-before.cmps"));
            var session=new EditorSession(ProjectFile.Read(file));
            var original=session.ActiveLayer!.Pixels!; var originalBytes=original.Bytes; var state=session.History.CurrentId;
            var task=scene=="background" ? AiTaskKind.ChangeBackground : scene is "relight" or "neon" or "golden" ? AiTaskKind.Relight : AiTaskKind.MatchToScene;
            using var before=session.Flatten(); ImageFiles.Save(before,Path.Combine(folder,item+"-before.png"),ExportFormat.Png);
            var service=new AiTaskService(()=>Environment.GetEnvironmentVariable("COMPOSA_SCENE_TEST_URL")!,Path.Combine(AppContext.BaseDirectory,"ai/engines"));
            service.SelectedEngine=service.Engines.Find("flux2-klein-intel-xpu");
            var request=new AiTaskRequest {Task=task,Prompt=task==AiTaskKind.ChangeBackground ? "An empty modern photography studio with a pale gray seamless floor and background, soft daylight, no people or objects." : "",
                Scene=new() {Denoise=denoise,Preset=scene=="neon" ? RelightPreset.Neon : scene=="golden" ? RelightPreset.GoldenHour : task==AiTaskKind.Relight ? RelightPreset.SoftboxLeft : RelightPreset.MatchScene},
                Settings=new() {Width=896,Height=580,Seed=27,Values=new() {["imageOriginalSize"]=false}}};
            var started=System.Diagnostics.Stopwatch.StartNew();
            using var prepared=SceneEditInput.Prepare(session.Document.Clone(),request);
            await service.RunAsync(new EditorCommandService(session),request,TestContext.Current.CancellationToken);
            using var after=session.Flatten(); ImageFiles.Save(after,Path.Combine(folder,item+"-after.png"),ExportFormat.Png);
            var subject=session.Document.AllLayers().Single(layer=>layer.Tags.Contains("scene-object"));
            for(var y=0;y<after.Height;y++) for(var x=0;x<after.Width;x++)
            {
                Assert.Equal(prepared.ObjectAlpha.GetPixel(x,y).Alpha,subject.Pixels!.GetPixel(x,y).Alpha);
                if(task==AiTaskKind.ChangeBackground) Assert.Equal(prepared.Foreground.GetPixel(x,y),subject.Pixels.GetPixel(x,y));
                else if(prepared.ObjectAlpha.GetPixel(x,y).Alpha==0 && prepared.ShadowMask.GetPixel(x,y).Alpha==0) Assert.Equal(before.GetPixel(x,y),after.GetPixel(x,y));
            }
            using(var project=File.Create(Path.Combine(folder,item+"-after.cmps"))) ProjectFile.Write(session.Document.Clone(),project);
            Assert.Equal(originalBytes,original.Bytes); Assert.Equal(AiOperationStatus.Completed,service.Operation!.Status);
            var complete=session.History.CurrentId;
            session.Undo(); using(var undone=session.Flatten()) Assert.Equal(before.Bytes,undone.Bytes); Assert.Equal(state,session.History.CurrentId);
            session.Redo(); using(var redone=session.Flatten()) Assert.Equal(after.Bytes,redone.Bytes); Assert.Equal(complete,session.History.CurrentId);
            metrics.Add(new {item,task=task.ToString(),seconds=started.Elapsed.TotalSeconds,stage=service.Operation.Stage,
                layers=session.Document.AllLayers().Where(layer=>layer.Tags.Contains("ai-generated")).Select(layer=>new {layer.Name,tags=layer.Tags.ToArray()})});
            File.WriteAllText(Path.Combine(folder,"metrics.json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions {WriteIndented=true}));
        }
    }
}
