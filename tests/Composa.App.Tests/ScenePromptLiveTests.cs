using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class ScenePromptLiveTests
{
    public static bool Enabled=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_URL"));
    public static bool BackgroundEnabled=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_BACKGROUND_AB_LAYOUT"));
    [Fact(Skip="Set COMPOSA_BACKGROUND_AB_LAYOUT, SCENE_AB_URL, INPUT and OUTPUT for cleanup conditioning comparison.",SkipUnless=nameof(BackgroundEnabled))]
    public async Task Background_cleanup_keeps_neighboring_objects_as_context_and_targets_only_the_product()
    {
        var token=TestContext.Current.CancellationToken;var folder=Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_OUTPUT")!;Directory.CreateDirectory(folder);
        using var file=File.OpenRead(Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_INPUT")!);
        var document=ProjectFile.Read(file);var request=new AiTaskRequest {Task=AiTaskKind.ChangeBackground,Settings=new() {Width=1008,Height=736,Seed=73,Values=new() {["imageOriginalSize"]=false}}};
        using var input=SceneEditInput.Prepare(document.Clone(),request,token);using var first=new SceneEditingWorkflow(input,request);
        using var layout=first.Finish(ImageFiles.Load(Environment.GetEnvironmentVariable("COMPOSA_BACKGROUND_AB_LAYOUT")!));
        using var plate=new SceneBackgroundPlate(input,layout,16);ImageFiles.Save(layout,Path.Combine(folder,"layout.png"),ExportFormat.Png);
        using var client=new ComfyClient(Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_URL")!);var (server,caps)=await client.TestConnectionAsync(token);
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai/engines"));var engine=catalog.Find("flux2-klein-intel-xpu")!;
        foreach(var name in new[] {"actual-reference","actual-latent"})
        {
            using var native=new SceneEditingWorkflow(input,request,layout,plate.Mask);
            var source=await client.UploadPngAsync("cleanup-source",native.Source,token);var mask=await client.UploadPngAsync("cleanup-mask",native.Mask,token);
            var graph=catalog.ReadWorkflow(engine,engine.Workflow("sceneEdit"));native.Bind(graph,source,mask,request,cleanup:true);
            graph["removal"]=new JsonObject {["class_type"]="ImageToMask",["inputs"]=new JsonObject {["image"]=new JsonArray("maskImage",0),["channel"]="red"}};
            graph["prompt"]!["inputs"]!["text"]="Remove the main foreground object. Keep the surrounding objects, background, perspective and lighting.";
            if(name=="actual-reference")
            {
                graph["sourceEncode"]!["class_type"]="VAEEncodeForInpaint";graph["sourceEncode"]!["inputs"]!["mask"]=new JsonArray("removal",0);graph["sourceEncode"]!["inputs"]!["grow_mask_by"]=0;
                graph["cleanupReferenceEncode"]=new JsonObject {["class_type"]="VAEEncode",["inputs"]=new JsonObject {["pixels"]=new JsonArray("source",0),["vae"]=new JsonArray("vae",0)}};
                graph["positive"]!["inputs"]!["latent"]=new JsonArray("cleanupReferenceEncode",0);graph["negativeReference"]!["inputs"]!["latent"]=new JsonArray("cleanupReferenceEncode",0);
            }
            else
            {
                graph["sourceEncode"]!["class_type"]="VAEEncode";graph["sourceEncode"]!["inputs"]!.AsObject().Remove("mask");graph["sourceEncode"]!["inputs"]!.AsObject().Remove("grow_mask_by");
            }
            WorkflowMemory.Apply(graph,"auto",server,caps);File.WriteAllText(Path.Combine(folder,name+"-workflow.json"),graph.ToJsonString(new() {WriteIndented=true}));
            var execution=await client.ExecuteAsync(graph,cancellationToken:token);
            using(execution.History)using(var clean=native.Finish(await client.DownloadAsync(execution.Images.Single(image=>image.NodeId=="save"),token)))
                ImageFiles.Save(clean,Path.Combine(folder,name+"-background.png"),ExportFormat.Png);
        }
    }
    [Fact(Skip="Set COMPOSA_SCENE_AB_URL, INPUT and OUTPUT for photographic SAM edge validation.",SkipUnless=nameof(Enabled))]
    public async Task Photographic_SAM_selection_has_solid_coverage_and_deletes_without_a_translucent_fringe()
    {
        var token=TestContext.Current.CancellationToken;var folder=Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_OUTPUT")!;Directory.CreateDirectory(folder);
        using var file=File.OpenRead(Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_INPUT")!);
        var document=ProjectFile.Read(file);using var input=SceneEditInput.Prepare(document.Clone(),new() {Task=AiTaskKind.MatchToScene},token);
        var model=Composa.Vision.PromptModels.EfficientSamS;
        var samSession=new EditorSession(document.Clone()) {SampleAllLayers=true};
        await samSession.SelectPromptObjectAsync(model,null,input.SubjectBounds,Composa.Selections.SelectionMode.Replace,token);
        using var selection=Pixels.Clone(samSession.Selection!);
        ImageFiles.Save(selection,Path.Combine(folder,"sam-selection.png"),ExportFormat.Png);
        var session=EditorSession.NewCanvas(input.Before.Width,input.Before.Height);session.AddImageLayer("Photographic scene",Pixels.Clone(input.Before),new(input.Before.Width/2f,input.Before.Height/2f),fit:false);
        session.ApplyAiSelection(AiTaskKind.SelectSubject,Pixels.Clone(selection));session.ClearSelection();
        var intersection=0;var union=0;var selected=0;
        for(var y=0;y<selection.Height;y++)for(var x=0;x<selection.Width;x++)
        {
            var alpha=selection.GetPixel(x,y).Alpha;Assert.True(alpha is 0 or 255);
            var expected=input.ObjectAlpha.GetPixel(x,y).Alpha>=128;var actual=alpha==255;
            if(expected&&actual)intersection++;if(expected||actual)union++;
            if(actual){selected++;Assert.Equal(0,session.ActiveLayer!.Pixels!.GetPixel(x,y).Alpha);}
        }
        var iou=intersection/(double)union;Assert.True(iou>.85,$"SAM photographic mask IoU {iou:0.000}");
        ImageFiles.Save(session.ActiveLayer!.Pixels!,Path.Combine(folder,"sam-cleared.png"),ExportFormat.Png);
        File.WriteAllText(Path.Combine(folder,"sam-metrics.json"),System.Text.Json.JsonSerializer.Serialize(new {iou,selected,partialSelectionPixels=0,residualSelectedPixels=0}));
    }
    [Fact(Skip="Set COMPOSA_SCENE_AB_URL, INPUT project and OUTPUT for same-input prompt comparison.",SkipUnless=nameof(Enabled))]
    public async Task Same_photographic_composition_compares_short_and_legacy_prompt_and_saves_raw_shadow_evidence()
    {
        var token=TestContext.Current.CancellationToken;
        var folder=Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_OUTPUT")!;Directory.CreateDirectory(folder);
        using var file=File.OpenRead(Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_INPUT")!);
        var document=ProjectFile.Read(file);var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene,Settings=new() {Width=1008,Height=736,Seed=73,Values=new() {["imageOriginalSize"]=false}}};
        using var input=SceneEditInput.Prepare(document.Clone(),request,token);using var native=new SceneEditingWorkflow(input,request);
        ImageFiles.Save(native.Source,Path.Combine(folder,"model-source.png"),ExportFormat.Png);ImageFiles.Save(native.Mask,Path.Combine(folder,"model-mask.png"),ExportFormat.Png);
        using var client=new ComfyClient(Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_URL")!);
        var (server,capabilities)=await client.TestConnectionAsync(token);
        var files=new Dictionary<string,string> {{"sourceImage",await client.UploadPngAsync("scene-ab",native.Source,token)},{"sceneMask",await client.UploadPngAsync("scene-ab-mask",native.Mask,token)}};
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai/engines"));var engine=catalog.Find("flux2-klein-intel-xpu")!;
        foreach(var name in (Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_CASES")??"short,legacy").Split(','))
        {
            var graph=catalog.ReadWorkflow(engine,engine.Workflow("sceneEdit"));native.Bind(graph,files["sourceImage"],files["sceneMask"],request);SceneEditingWorkflow.References(graph,files);
            if(name=="legacy")graph["prompt"]!["inputs"]!["text"]=SceneEditingWorkflow.Prompt(request)+SceneEditingWorkflow.LayoutInstruction(input)+"\nLighting guidance: "+input.LightHint.Instruction;
            if(name.Contains("four"))graph["sampler"]!["inputs"]!["steps"]=4;
            if(name.Contains("full"))graph["sampler"]!["inputs"]!["denoise"]=1;
            if(name.Contains("free"))
            {
                using var free=Pixels.NewMask(native.PaddedSize.Width,native.PaddedSize.Height);
                using(var draw=new SKCanvas(free))using(var paint=new SKPaint {Color=SKColors.White})draw.DrawRect(SKRect.Create(native.ContentSize.Width,native.ContentSize.Height),paint);
                Pixels.Invalidate(free);graph["maskImage"]!["inputs"]!["image"]=await client.UploadPngAsync("free-contour",free,token);
            }
            WorkflowMemory.Apply(graph,"auto",server,capabilities);
            File.WriteAllText(Path.Combine(folder,name+"-workflow.json"),graph.ToJsonString(new() {WriteIndented=true}));
            var replay=Environment.GetEnvironmentVariable("COMPOSA_SCENE_AB_REPLAY");
            var execution=string.IsNullOrWhiteSpace(replay)?await client.ExecuteAsync(graph,cancellationToken:token):null;
            using(execution?.History)
            using(var generated=execution==null?ImageFiles.Load(Path.Combine(replay!,name+"-raw.png")):native.Finish(await client.DownloadAsync(execution.Images.Single(image=>image.NodeId=="save"),token)))
            using(var baseline=execution==null?ImageFiles.Load(Path.Combine(replay!,name+"-baseline.png")):native.Finish(await client.DownloadAsync(execution.Images.Single(image=>image.NodeId=="baseline"),token)))
            {
                ImageFiles.Save(generated,Path.Combine(folder,name+"-raw.png"),ExportFormat.Png);ImageFiles.Save(baseline,Path.Combine(folder,name+"-baseline.png"),ExportFormat.Png);
                var result=SceneEditResult.Extract(input,generated,request,token,baseline,input.Before.Width/(double)native.ContentSize.Width);
                var session=new EditorSession(document.Clone());session.InsertSceneEdit(request.Task,input.SourceLayerId,[result],true,false,input.PreviousResultIds,input.SourceFrame);
                using var composite=session.Flatten();ImageFiles.Save(composite,Path.Combine(folder,name+"-composite.png"),ExportFormat.Png);
                foreach(var layer in result.Layers)
                {
                    var shadow=layer.Tags!.Contains("scene-shadow");ImageFiles.Save(layer.Pixels,Path.Combine(folder,name+(shadow?"-shadow.png":"-subject.png")),ExportFormat.Png);
                    if(shadow)
                    {
                        using var preview=Pixels.NewColor(layer.Pixels.Width,layer.Pixels.Height);preview.Erase(SKColors.White);
                        using(var draw=new SKCanvas(preview))draw.DrawImage(Pixels.ImageOf(layer.Pixels),0,0);
                        Pixels.Invalidate(preview);ImageFiles.Save(preview,Path.Combine(folder,name+"-shadow-on-white.png"),ExportFormat.Png);
                    }
                }
                using var saved=File.Create(Path.Combine(folder,name+".cmps"));ProjectFile.Write(session.Document.Clone(),saved);
            }
        }
    }
}
