using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class SceneLightingMatrixLiveTests
{
    public static bool Enabled=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_LIGHT_MATRIX_URL"));

    [Fact(Skip="Set COMPOSA_LIGHT_MATRIX_URL, INPUT and OUTPUT for the indoor/outdoor lighting matrix.",SkipUnless=nameof(Enabled))]
    public async Task Indoor_and_outdoor_sun_and_diffuse_scenes_preserve_png_and_record_cast_direction()
    {
        var token=TestContext.Current.CancellationToken;
        var root=Environment.GetEnvironmentVariable("COMPOSA_LIGHT_MATRIX_INPUT")!;
        var output=Environment.GetEnvironmentVariable("COMPOSA_LIGHT_MATRIX_OUTPUT")!;Directory.CreateDirectory(output);
        var metrics=new List<object>();
        using var client=new ComfyClient(Environment.GetEnvironmentVariable("COMPOSA_LIGHT_MATRIX_URL")!);
        var (server,caps)=await client.TestConnectionAsync(token);
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai/engines"));var engine=catalog.Find("flux2-klein-intel-xpu")!;
        foreach(var name in (Environment.GetEnvironmentVariable("COMPOSA_LIGHT_MATRIX_CASES")??"indoor-sun,indoor-diffuse,outdoor-sun,outdoor-overcast").Split(','))
        {
            var document=new Document(768,576);
            using var photo=ImageFiles.Load(Path.Combine(root,name+".png"));
            var background=Pixels.NewColor(768,576);
            using(var draw=new SKCanvas(background))draw.DrawImage(Pixels.ImageOf(photo),SKRect.Create(768,576),new SKSamplingOptions(SKCubicResampler.Mitchell));
            Pixels.Invalidate(background);document.Layers.Add(Layer.Raster("Lighting reference",background));
            var png=ImageFiles.Load(Path.Combine(root,"subject.png"));
            var subject=Layer.Raster("Original PNG",png);
            // Use actual alpha bounds so transparent margins cannot make the bottle float.
            using(var alpha=SceneEditInput.Alpha(png))
            {
                var bounds=Composa.Selections.SelectionMask.Bounds(alpha,16);var scale=210d/bounds.Height;
                subject.Transform=new() {X=384-(bounds.Left+bounds.Width/2d)*scale,Y=440-bounds.Bottom*scale,Width=png.Width*scale,Height=png.Height*scale};
            }
            document.Layers.Add(subject);document.SetActive(subject.Id);
            var request=new AiTaskRequest {Task=AiTaskKind.Harmonize,Settings=new() {Seed=73,Width=768,Height=576,Values=new() {["imageOriginalSize"]=false}}};
            using var input=SceneEditInput.Prepare(document.Clone(),request,token);using var native=new SceneEditingWorkflow(input,request);
            var folder=Path.Combine(output,name);Directory.CreateDirectory(folder);
            Save(input.Before,"before");Save(input.Background,"background");Save(input.ObjectAlpha,"source-alpha");Save(native.Mask,"generation-mask");
            var files=new Dictionary<string,string> {{"sourceImage",await client.UploadPngAsync("lighting-matrix",native.Source,token)},
                {"sceneMask",await client.UploadPngAsync("lighting-mask",native.Mask,token)},
                {"sceneBackground",await client.UploadPngAsync("lighting-reference",native.BackgroundReference!,token)}};
            var graph=catalog.ReadWorkflow(engine,engine.Workflow("sceneEdit"));native.Bind(graph,files["sourceImage"],files["sceneMask"],request);SceneEditingWorkflow.References(graph,files);
            graph["model"]!["inputs"]!["unet_name"]=Environment.GetEnvironmentVariable("COMPOSA_LIGHT_MATRIX_MODEL")??"flux-2-klein-9b-int8-convrot.safetensors";
            graph["clip"]!["inputs"]!["clip_name"]="qwen_3_8b_fp8mixed.safetensors";
            WorkflowMemory.Apply(graph,"reduced",server,caps);
            File.WriteAllText(Path.Combine(folder,"workflow.json"),graph.ToJsonString(new() {WriteIndented=true}));
            var replay=Environment.GetEnvironmentVariable("COMPOSA_LIGHT_MATRIX_REPLAY");
            var clock=System.Diagnostics.Stopwatch.StartNew();var execution=string.IsNullOrEmpty(replay)?await client.ExecuteAsync(graph,cancellationToken:token):null;
            using(execution?.History)
            using(var generated=execution==null?ImageFiles.Load(Path.Combine(replay!,name,"raw.png")):native.Finish(await client.DownloadAsync(execution.Images.Single(i=>i.NodeId=="save"),token)))
            using(var baseline=execution==null?ImageFiles.Load(Path.Combine(replay!,name,"baseline.png")):native.Finish(await client.DownloadAsync(execution.Images.Single(i=>i.NodeId=="baseline"),token)))
            {
                Save(generated,"raw");Save(baseline,"baseline");
                var result=SceneEditResult.Extract(input,generated,request,token,baseline);
                try
                {
                    var edited=Assert.Single(result.Layers,l=>l.Tags!.Contains("scene-object")).Pixels;
                    for(var y=0;y<576;y++)for(var x=0;x<768;x++)Assert.Equal(input.ObjectAlpha.GetPixel(x,y).Alpha,edited.GetPixel(x,y).Alpha);
                    using var composite=Pixels.Clone(input.Background);
                    using(var draw=new SKCanvas(composite))foreach(var layer in result.Layers)draw.DrawImage(Pixels.ImageOf(layer.Pixels),0,0);
                    Pixels.Invalidate(composite);Save(composite,"after");
                    var shadow=result.Layers.SingleOrDefault(l=>l.Tags!.Contains("scene-shadow"))?.Pixels;
                    double mass=0,mx=0,my=0;var contacts=0;var gaps=0;var strong=0;var farStrong=0;
                    var center=input.SupportBounds.Left+input.SupportBounds.Width/2d;
                    var contactRadius=Math.Max(16,input.SupportBounds.Width*1.5);
                    if(shadow!=null)
                    {
                        Save(shadow,"shadow");
                        for(var y=0;y<576;y++)for(var x=0;x<768;x++)
                        {
                            var a=shadow.GetPixel(x,y).Alpha*(1-input.ObjectAlpha.GetPixel(x,y).Alpha/255d);
                            mass+=a;mx+=a*x;my+=a*y;
                            if(a>=32)
                            {
                                strong++;
                                if((x-center)*(x-center)+(y-input.SupportBounds.Bottom)*(y-input.SupportBounds.Bottom)>contactRadius*contactRadius)farStrong++;
                            }
                        }
                        for(var x=input.SupportBounds.Left;x<input.SupportBounds.Right;x++)
                        {
                            var foot=input.SupportY[x]+1;if(foot<=0||foot+8>=576)continue;
                            if(Enumerable.Range(foot,8).Any(y=>shadow.GetPixel(x,y).Alpha>=8))
                            {contacts++;if(shadow.GetPixel(x,foot).Alpha==0)gaps++;}
                        }
                    }
                    var dx=mass>0?mx/mass-(input.SupportBounds.Left+input.SupportBounds.Width/2d):0;
                    var dy=mass>0?my/mass-input.SupportBounds.Bottom:0;
                    var farStrongFraction=farStrong/(double)Math.Max(1,strong);
                    metrics.Add(new {name,seconds=clock.Elapsed.TotalSeconds,input.LightHint.SoftContactOnly,result.ShadowPixels,dx,dy,contacts,gaps,strong,farStrongFraction,result.ContactCorrectionX,result.ContactCorrectionY});
                    File.WriteAllText(Path.Combine(output,"metrics.json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions {WriteIndented=true}));
                    var saved=document.Clone();saved.Find(subject.Id)!.Visible=false;
                    foreach(var layer in result.Layers)saved.Layers.Add(Layer.Raster(layer.Name,Pixels.Clone(layer.Pixels)));
                    using(var project=File.Create(Path.Combine(folder,"result.cmps")))ProjectFile.Write(saved,project);
                    Assert.True(result.ShadowPixels>0,$"No shadow in {name}");Assert.Equal(0,gaps);
                    Assert.Equal(name is "indoor-diffuse" or "outdoor-overcast",input.LightHint.SoftContactOnly);
                    if(input.LightHint.SoftContactOnly)Assert.True(farStrongFraction<.5,$"Diffuse light has a dominant far cast: {farStrongFraction:0.000}");
                    if(name=="indoor-sun")Assert.True(dx>0,$"Indoor sunlight direction: dx={dx}");
                    if(name=="outdoor-sun")Assert.True(dx<0,$"Outdoor sunlight direction: dx={dx}");
                }
                finally {foreach(var layer in result.Layers)layer.Pixels.Dispose();}
            }
            void Save(SKBitmap image,string label)=>ImageFiles.Save(image,Path.Combine(folder,label+".png"),ExportFormat.Png);
        }
    }
}
