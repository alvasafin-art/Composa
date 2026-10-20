using System.Text.Json;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.App.Tests;

public class SceneProductLiveTests
{
    public static bool Enabled=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSA_PRODUCT_TEST_URL"));
    [Fact(Skip="Set COMPOSA_PRODUCT_TEST_URL, INPUT and OUTPUT for photographic product validation.",SkipUnless=nameof(Enabled))]
    public async Task Photographic_product_checks_contact_geometry_edges_and_background_conditioning()
    {
        var input=Environment.GetEnvironmentVariable("COMPOSA_PRODUCT_TEST_INPUT")!;
        var output=Environment.GetEnvironmentVariable("COMPOSA_PRODUCT_TEST_OUTPUT")!; Directory.CreateDirectory(output);
        var cases=(Environment.GetEnvironmentVariable("COMPOSA_PRODUCT_TEST_CASES")??"match-odd,match-aligned,relight-odd,background-odd").Split(',');
        var metrics=new List<object>();
        var cleanPath=Path.Combine(input,"product-shoe-sam.png");
        var photoPath=File.Exists(Path.Combine(input,"product-shoe.png"))?Path.Combine(input,"product-shoe.png"):Path.Combine(input,"product-photo.jpg");
        if(Environment.GetEnvironmentVariable("COMPOSA_PRODUCT_CLEAN")=="1" && !File.Exists(cleanPath))
        {
            using var photo=ImageFiles.Load(photoPath);
            using var white=Pixels.NewColor(900,(int)Math.Round(photo.Height*900d/photo.Width)); white.Erase(SKColors.White);
            using(var draw=new SKCanvas(white)) draw.DrawImage(Pixels.ImageOf(photo),SKRect.Create(white.Width,white.Height),new SKSamplingOptions(SKCubicResampler.Mitchell));
            Pixels.Invalidate(white); using var matte=ProductMask(white);
            var cutout=Pixels.NewColor(white.Width,white.Height);
            using(var draw=new SKCanvas(cutout)) {draw.DrawImage(Pixels.ImageOf(photo),SKRect.Create(cutout.Width,cutout.Height),new SKSamplingOptions(SKCubicResampler.Mitchell));using var paint=new SKPaint {BlendMode=SKBlendMode.DstIn};draw.DrawImage(Pixels.ImageOf(matte),0,0,paint);}
            Pixels.Invalidate(cutout); using(cutout) ImageFiles.Save(cutout,cleanPath,ExportFormat.Png);
        }
        foreach(var item in cases)
        {
            var aligned=item.EndsWith("aligned"); var w=aligned?1008:1003; var h=aligned?736:731;
            var document=new Document(w,h);
            using var photograph=ImageFiles.Load(Path.Combine(input,"background-sunset.jpg"));
            var background=Pixels.NewColor(w,h);
            var scale=Math.Max(w/(double)photograph.Width,h/(double)photograph.Height);
            using(var draw=new SKCanvas(background))
            {
                if(item.Contains("mirrored")) {draw.Translate(w,0);draw.Scale(-1,1);}
                draw.DrawImage(Pixels.ImageOf(photograph),new SKRect((float)(w-photograph.Width*scale)/2,0,(float)(w+photograph.Width*scale)/2,(float)(photograph.Height*scale)),new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
            Pixels.Invalidate(background); document.Layers.Add(Layer.Raster("Photographic background",background));
            var product=ImageFiles.Load(Environment.GetEnvironmentVariable("COMPOSA_PRODUCT_CLEAN")=="1"?cleanPath:Path.Combine(input,"product-shoe.png")); var layer=Layer.Raster("Photographic PNG product",product);
            layer.Transform=item.Contains("grounded")
                ? new() {X=350.25,Y=345.5,Width=320,Height=320*product.Height/(double)product.Width}
                : new() {X=240.25,Y=207.5,Width=530,Height=530*product.Height/(double)product.Width};
            document.Layers.Add(layer); document.SetActive(layer.Id); var session=new EditorSession(document);
            var original=product.Bytes; using var before=session.Flatten(); ImageFiles.Save(before,Path.Combine(output,item+"-before.png"),ExportFormat.Png);
            using(var project=File.Create(Path.Combine(output,item+"-before.cmps"))) ProjectFile.Write(document.Clone(),project);
            var task=item.StartsWith("background")?AiTaskKind.ChangeBackground:item.StartsWith("relight")?AiTaskKind.Relight:AiTaskKind.MatchToScene;
            var request=new AiTaskRequest {Task=task,Prompt=task==AiTaskKind.ChangeBackground ? item.Contains("neighbors")
                    ? "A warm beige product photography studio, matte stone tabletop, a complete small potted plant to the left of the shoe and a complete ceramic cup to its right, soft window light from the upper left."
                    : "A clean warm beige product photography studio with a matte stone tabletop, soft window light from the upper left. The shoe rests directly on the tabletop at its existing position." : "",
                Scene=new() {Denoise=task==AiTaskKind.ChangeBackground?.95:.8,Preset=RelightPreset.SoftboxRight},Settings=new() {Width=1008,Height=736,Seed=73,Values=new() {["imageOriginalSize"]=false}}};
            using var prepared=SceneEditInput.Prepare(document.Clone(),request);
            var captured=new Capture(output,item);
            var service=new AiTaskService(()=>Environment.GetEnvironmentVariable("COMPOSA_PRODUCT_TEST_URL")!,Path.Combine(AppContext.BaseDirectory,"ai/engines"),url=>new CapturedConnection(url,captured)); service.SelectedEngine=service.Engines.Find("flux2-klein-intel-xpu");
            var started=System.Diagnostics.Stopwatch.StartNew(); await service.RunAsync(new EditorCommandService(session),request,TestContext.Current.CancellationToken);
            var operations=1;
            if(item.Contains("repeat"))
            {
                for(var iteration=1;iteration<3;iteration++)
                {
                    using var again=SceneEditInput.Prepare(document.Clone(),request);
                    Assert.Equal(prepared.Before.Bytes,again.Before.Bytes);Assert.Equal(prepared.Background.Bytes,again.Background.Bytes);
                    ImageFiles.Save(again.Before,Path.Combine(output,item+"-input-"+(iteration+1)+".png"),ExportFormat.Png);
                    await service.RunAsync(new EditorCommandService(session),request with {Settings=request.Settings with {Seed=73+iteration}},TestContext.Current.CancellationToken);
                    operations++;
                }
            }
            using var after=session.Flatten(); ImageFiles.Save(after,Path.Combine(output,item+"-after.png"),ExportFormat.Png);
            using(var project=File.Create(Path.Combine(output,item+"-after.cmps"))) ProjectFile.Write(document.Clone(),project);
            var subject=document.AllLayers().Single(candidate=>candidate.Tags.Contains("scene-object")&&document.IsEffectivelyVisible(candidate));
            var shadow=document.AllLayers().FirstOrDefault(candidate=>candidate.Tags.Contains("scene-shadow")&&document.IsEffectivelyVisible(candidate));
            foreach(var result in document.AllLayers().Where(candidate=>candidate.Tags.Contains("ai-generated") && candidate.Pixels!=null&&document.IsEffectivelyVisible(candidate))) ImageFiles.Save(result.Pixels!,Path.Combine(output,item+"-"+(result.Tags.Contains("scene-shadow")?"shadow":result.Tags.Contains("scene-background")?"background":"subject")+".png"),ExportFormat.Png);
            var changedOutside=0; for(var y=0;y<h;y++) for(var x=0;x<w;x++)
            {
                Assert.Equal(prepared.ObjectAlpha.GetPixel(x,y).Alpha,subject.Pixels!.GetPixel(x,y).Alpha);
                if(task==AiTaskKind.ChangeBackground) Assert.Equal(prepared.Foreground.GetPixel(x,y),subject.Pixels.GetPixel(x,y));
                else if(prepared.ObjectAlpha.GetPixel(x,y).Alpha==0 && prepared.ShadowMask.GetPixel(x,y).Alpha==0 && before.GetPixel(x,y)!=after.GetPixel(x,y)) changedOutside++;
            }
            Assert.Equal(0,changedOutside); Assert.Equal(original,product.Bytes);
            for(var step=0;step<operations;step++)session.Undo(); using(var undone=session.Flatten()) Assert.Equal(before.Bytes,undone.Bytes);
            for(var step=0;step<operations;step++)session.Redo(); using(var redone=session.Flatten()) Assert.Equal(after.Bytes,redone.Bytes);
            var gap=shadow==null?-1:ContactGap(prepared.ObjectAlpha,shadow.Pixels!,prepared.SubjectBounds,16);
            var opaqueGap=shadow==null?-1:ContactGap(prepared.ObjectAlpha,shadow.Pixels!,prepared.SubjectBounds,128);
            metrics.Add(new {item,w,h,seconds=started.Elapsed.TotalSeconds,contactGap=gap,opaqueGap,changedOutside,stage=service.Operation!.Stage});
            File.WriteAllText(Path.Combine(output,"metrics.json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions {WriteIndented=true}));
        }
    }
    private static SKBitmap ProductMask(SKBitmap photo)
    {
        var token=TestContext.Current.CancellationToken;
        if(Environment.GetEnvironmentVariable("COMPOSA_PRODUCT_SAM_BOX") is {Length:>0} box)
        {
            var coordinates=box.Split(',').Select(value=>int.Parse(value,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if(coordinates.Length!=4)throw new ArgumentException("SAM box needs left,top,right,bottom.");
            var model=PromptModels.EfficientSamS;var image=PromptSegmentation.Encode(photo,model,token);
            using var decoded=PromptSegmentation.Decode(image,model,photo.Width,photo.Height,null,new SKRectI(coordinates[0],coordinates[1],coordinates[2],coordinates[3]),token);
            return Pixels.Clone(decoded);
        }
        return SubjectFinder.Matte(photo,SubjectDetect.Any,token)??throw new InvalidOperationException("Product mask failed");
    }
    private sealed class Capture(string folder,string item)
    {
        private int next;
        public int Next()=>Interlocked.Increment(ref next);
        public void Graph(int pass,System.Text.Json.Nodes.JsonObject graph)=>File.WriteAllText(Path.Combine(folder,$"{item}-pass-{pass}-workflow.json"),graph.ToJsonString(new() {WriteIndented=true}));
        public void Image(int pass,string node,SKBitmap image)=>ImageFiles.Save(image,Path.Combine(folder,$"{item}-pass-{pass}-{node}.png"),ExportFormat.Png);
    }
    private sealed class CapturedConnection(string url,Capture capture):IComfyConnection
    {
        private readonly ComfyClient client=new(url);private int pass;
        public ComfyServerAddress Address=>client.Address;
        public TimeSpan ConnectionTimeout {get=>client.ConnectionTimeout;set=>client.ConnectionTimeout=value;}
        public Task<(ComfyServerInfo,ComfyServerCapabilities)> TestConnectionAsync(CancellationToken token=default)=>client.TestConnectionAsync(token);
        public Task<string> UploadPngAsync(string semantic,SKBitmap image,CancellationToken token=default)=>client.UploadPngAsync(semantic,image,token);
        public Task<ComfyExecutionResult> ExecuteAsync(System.Text.Json.Nodes.JsonObject graph,IProgress<AiOperationState>? progress=null,CancellationToken cancellationToken=default)
        {pass=capture.Next();capture.Graph(pass,graph);return client.ExecuteAsync(graph,progress,cancellationToken);}
        public async Task<SKBitmap> DownloadAsync(ComfyImageReference image,CancellationToken token=default)
        {var pixels=await client.DownloadAsync(image,token);try{capture.Image(pass,image.NodeId??"image",pixels);return pixels;}catch{pixels.Dispose();throw;}}
        public void Dispose()=>client.Dispose();
    }
    internal static double ContactGap(SKBitmap alpha,SKBitmap shadow,SKRectI bounds,byte threshold=128)
    {
        double best=double.PositiveInfinity;
        for(var x=bounds.Left;x<bounds.Right;x++)
        {
            var bottom=-1; for(var y=bounds.Bottom-1;y>=Math.Max(bounds.Top,bounds.Bottom-bounds.Height/8);y--) if(alpha.GetPixel(x,y).Alpha>threshold) {bottom=y;break;}
            if(bottom<0) continue;
            for(var dy=0;dy<=16;dy++) for(var dx=-16;dx<=16;dx++)
            {var sx=x+dx; var sy=bottom+dy; if(sx<0||sy<0||sx>=shadow.Width||sy>=shadow.Height||shadow.GetPixel(sx,sy).Alpha<8||alpha.GetPixel(sx,sy).Alpha>threshold)continue;best=Math.Min(best,Math.Sqrt(dx*dx+dy*dy));}
        }
        return double.IsFinite(best)?best:-1;
    }
}
