using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Opt-in, reproducible runs through the installed FLUX model; never calls a paid API.</summary>
public class FluxLiveDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_flux_preserves_surroundings_and_records_mask_edge_quality()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_FLUX_DIAGNOSTIC_URL");
        if (string.IsNullOrWhiteSpace(url)) return;
        var folder = Environment.GetEnvironmentVariable("COMPOSA_FLUX_DIAGNOSTIC_OUTPUT")
            ?? Path.Combine(Screenshots.Folder, "flux-diagnostics");
        Directory.CreateDirectory(folder);
        using var client = new ComfyClient(url);
        var (server, caps) = await client.TestConnectionAsync(TestContext.Current.CancellationToken);
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var engine = catalog.Find("flux2-klein-intel-xpu")!;
        var binding = engine.Binding(AiTaskKind.GenerativeFill)!;
        var cases = (Environment.GetEnvironmentVariable("COMPOSA_FLUX_DIAGNOSTIC_CASES")
            ?? "native-small,native-full,mp-small,mp-full,mp-schedule").Split(',');
        foreach (var name in cases)
        {
            var session = EditorSession.NewCanvas(641, 423, new SKColor(200, 200, 200));
            session.SelectRect(new SKRect(250, 160, 330, 240));
            var request = new AiTaskRequest { Task = AiTaskKind.GenerativeFill,
                Prompt = "Keep the image unchanged. A perfectly clean uniform light gray background, no objects, no texture, no noise.",
                Settings = new() { Width = 512, Height = 512, Seed = 19,
                    Values = new() { ["imageOriginalSize"] = name.StartsWith("native"),
                        ["maskGrow"] = 4, ["maskBlend"] = 8, ["maskBlur"] = 4,
                        ["maskContext"] = 1.2, ["colorMatch"] = "off" } } };
            using var inputs = AiTaskInputPreparer.Prepare(session, request);
            var files = new Dictionary<string, string>();
            foreach (var (key, image) in inputs.Images())
                if (binding.Inputs.ContainsKey(key)) files[key] = await client.UploadPngAsync(key, image, TestContext.Current.CancellationToken);
            var values = inputs.Values(files);
            foreach (var (key, value) in request.Settings.Values) values[key] = value;
            var graph = WorkflowBinder.Bind(catalog.ReadWorkflow(engine, engine.Workflow(binding.Workflow)), binding, values);
            graph["vae"]!["inputs"]!["vae_name"] = name.Contains("small") ? "full_encoder_small_decoder.safetensors" : "flux2-vae.safetensors";
            WorkflowExecution.MaskedEdit(graph, inputs, request, caps);
            using var editable = new EditableMaskedWorkflow(inputs, request); editable.Bind(graph);
            WorkflowMemory.Apply(graph,"auto",server,caps);
            if (name.EndsWith("schedule")) NativeSchedule(graph, editable.GenerationSize);
            if (name.Contains("binary"))
            {
                graph["diagnostic_binary_mask"] = new JsonObject { ["class_type"] = "ThresholdMask", ["inputs"] = new JsonObject
                    { ["mask"] = new JsonArray("composa_edit_padded_mask", 0), ["value"] = 0.001 } };
                graph["composa_condition"]!["inputs"]!["mask"] = new JsonArray("diagnostic_binary_mask", 0);
            }
            if (name.Contains("roundtrip")) graph["decode"]!["inputs"]!["samples"] = new JsonArray("sourceEncode", 0);
            if (name.Contains("match")) request.Settings.Values["colorMatch"] = "strong";
            WorkflowModels.ResolvePaths(graph, caps);
            File.WriteAllText(Path.Combine(folder, name + "-graph.json"), graph.ToJsonString(new() { WriteIndented = true }));
            var execution = await client.ExecuteAsync(graph, cancellationToken: TestContext.Current.CancellationToken);
            using (execution.History)
            {
                using var decoded = await client.DownloadAsync(Assert.Single(execution.Images), TestContext.Current.CancellationToken);
                ImageFiles.Save(decoded, Path.Combine(folder, name + "-decoded.png"), ExportFormat.Png);
                using var raw = editable.Finish(Pixels.Clone(decoded));
                using var result = AiResultPostprocessor.Constrain(raw, inputs.ContextImage, editable.Mask);
                ImageFiles.Save(result, Path.Combine(folder, name + "-result.png"), ExportFormat.Png);
                ImageFiles.Save(editable.Mask, Path.Combine(folder, name + "-mask.png"), ExportFormat.Png);
                Assert.Equal(inputs.ContextImage.GetPixel(50, 90), result.GetPixel(50, 90));
                var tone = new List<double>(); var differences = new List<double>();
                for (var y = 170; y < 230; y++) for (var x = 260; x < 320; x++)
                {
                    tone.Add(result.GetPixel(x, y).Red);
                    differences.Add(Math.Abs(result.GetPixel(x, y).Red - result.GetPixel(x + 1, y).Red));
                }
                var mean = tone.Average();
                var metrics = new { name, generation = new[] { editable.GenerationSize.Width, editable.GenerationSize.Height },
                    bounds = new[] { editable.Bounds.Left, editable.Bounds.Top, editable.Bounds.Right, editable.Bounds.Bottom }, mean,
                    deviation = Math.Sqrt(tone.Select(x => (x - mean) * (x - mean)).Average()),
                    neighborDifference = differences.Average(),
                    edge = Enumerable.Range(245, 15).Select(x => (int)result.GetPixel(x, 200).Red).ToArray() };
                var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true });
                File.WriteAllText(Path.Combine(folder, name + "-metrics.json"), json); output.WriteLine(json);
                Assert.True(double.IsFinite(mean));
            }
        }
    }

    [Fact]
    public async Task Stock_resize_nodes_preserve_image_mask_registration_on_odd_and_edge_crops()
    {
        var url=Environment.GetEnvironmentVariable("COMPOSA_FLUX_DIAGNOSTIC_URL"); if(string.IsNullOrWhiteSpace(url)) return;
        using var client=new ComfyClient(url); var (_,caps)=await client.TestConnectionAsync(TestContext.Current.CancellationToken);
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai","engines")); var engine=catalog.Find("flux2-klein-intel-xpu")!;
        foreach(var original in new[] { true,false }) foreach(var edge in new[] { true,false })
        {
            var source=Pixels.NewColor(301,189);
            for(var y=0;y<189;y++) for(var x=0;x<301;x++) source.SetPixel(x,y,new SKColor((byte)(x/2),(byte)y,100));
            Pixels.Invalidate(source);
            var document=new Composa.Model.Document(301,189); var layer=Composa.Model.Layer.Raster("Coordinate ramp",source);
            document.Layers.Add(layer); document.SetActive(layer.Id); var session=new EditorSession(document);
            session.SelectRect(edge?new SKRect(0,1,31,54):new SKRect(110,65,141,118));
            var request=new AiTaskRequest { Task=AiTaskKind.GenerativeFill,Settings=new() { Width=96,Height=160,
                Values=new() { ["imageOriginalSize"]=original,["maskGrow"]=4,["maskBlend"]=8,["maskBlur"]=64,["maskContext"]=1.2 } } };
            using var inputs=AiTaskInputPreparer.Prepare(session,request); var binding=engine.Binding(request.Task)!;
            var files=new Dictionary<string,string>();
            foreach(var (key,bitmap) in inputs.Images()) if(binding.Inputs.ContainsKey(key)) files[key]=await client.UploadPngAsync(key,bitmap,TestContext.Current.CancellationToken);
            var values=inputs.Values(files); foreach(var (key,value) in request.Settings.Values) values[key]=value;
            var graph=WorkflowBinder.Bind(catalog.ReadWorkflow(engine,engine.Workflow(binding.Workflow)),binding,values);
            WorkflowExecution.MaskedEdit(graph,inputs,request,caps); using var edit=new EditableMaskedWorkflow(inputs,request); edit.Bind(graph);
            // Exercise actual stock resize/pad/blur nodes without model inference. Two outputs
            // let us check mask padding and pixel registration independently of model quality.
            graph["save"]!["inputs"]!["images"]=new JsonArray("composa_edit_size",0);
            graph["diagnostic_mask_image"]=new JsonObject { ["class_type"]="MaskToImage",["inputs"]=new JsonObject { ["mask"]=new JsonArray("composa_edit_padded_mask",0) } };
            graph["save_mask"]=new JsonObject { ["class_type"]="SaveImage",["inputs"]=new JsonObject { ["images"]=new JsonArray("diagnostic_mask_image",0),["filename_prefix"]="Composa/registration-mask" } };
            var executed=await client.ExecuteAsync(graph,cancellationToken:TestContext.Current.CancellationToken);
            using(executed.History)
            {
                using var decoded=await client.DownloadAsync(Assert.Single(executed.Images,image=>image.NodeId=="save"),TestContext.Current.CancellationToken);
                using var mask=await client.DownloadAsync(Assert.Single(executed.Images,image=>image.NodeId=="save_mask"),TestContext.Current.CancellationToken);
                Assert.Equal(edit.GenerationSize,(decoded.Width,decoded.Height)); Assert.Equal(edit.GenerationSize,(mask.Width,mask.Height));
                if(edit.ContentSize.Width<mask.Width) Assert.Equal(0,mask.GetPixel(mask.Width-1,mask.Height/2).Red);
                if(edit.ContentSize.Height<mask.Height) Assert.Equal(0,mask.GetPixel(mask.Width/2,mask.Height-1).Red);
                using var raw=edit.Finish(Pixels.Clone(decoded));
                for(var y=edit.Bounds.Top+2;y<edit.Bounds.Bottom-2;y+=7) for(var x=edit.Bounds.Left+2;x<edit.Bounds.Right-2;x+=7)
                {
                    Assert.InRange(Math.Abs(source.GetPixel(x,y).Red-raw.GetPixel(x,y).Red),0,original?0:2);
                    Assert.InRange(Math.Abs(source.GetPixel(x,y).Green-raw.GetPixel(x,y).Green),0,original?0:2);
                }
            }
        }
    }

    internal static void NativeSchedule(JsonObject graph, (int Width, int Height) size)
    {
        var old = graph["sampler"]!["inputs"]!.AsObject();
        static JsonObject Node(string type, JsonObject inputs) => new() { ["class_type"] = type, ["inputs"] = inputs };
        graph["diagnostic_sigmas"] = Node("Flux2Scheduler", new() { ["steps"] = old["steps"]!.DeepClone(), ["width"] = size.Width, ["height"] = size.Height });
        graph["diagnostic_sampler"] = Node("KSamplerSelect", new() { ["sampler_name"] = "euler" });
        graph["sampler"] = Node("SamplerCustom", new() { ["model"] = old["model"]!.DeepClone(), ["positive"] = old["positive"]!.DeepClone(),
            ["negative"] = old["negative"]!.DeepClone(), ["cfg"] = old["cfg"]!.DeepClone(), ["add_noise"] = true,
            ["noise_seed"] = old["seed"]!.DeepClone(), ["latent_image"] = old["latent_image"]!.DeepClone(),
            ["sampler"] = new JsonArray("diagnostic_sampler", 0), ["sigmas"] = new JsonArray("diagnostic_sigmas", 0) });
    }

    [Fact]
    public async Task Real_photos_cover_fill_removal_soft_relight_and_both_expansion_modes()
    {
        var url=Environment.GetEnvironmentVariable("COMPOSA_FLUX_PHOTO_URL"); if(string.IsNullOrWhiteSpace(url)) return;
        var folder=Environment.GetEnvironmentVariable("COMPOSA_FLUX_PHOTO_OUTPUT")!; Directory.CreateDirectory(folder);
        var sourceFolder=Environment.GetEnvironmentVariable("COMPOSA_FLUX_PHOTO_INPUT")!;
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai","engines")); var engine=catalog.Find("flux2-klein-intel-xpu")!;
        var cases=new[] { (AiTaskKind.GenerativeFill,false,false), (AiTaskKind.RemoveObject,false,true),
            (AiTaskKind.Relight,true,true),(AiTaskKind.Harmonize,true,false),
            (AiTaskKind.GenerativeExpand,false,true),(AiTaskKind.GenerativeExpand,true,false) };
        foreach(var (task,native,smallVae) in cases)
        {
            var chosenVae=Environment.GetEnvironmentVariable("COMPOSA_FLUX_PHOTO_VAE");
            if(chosenVae=="small"&&!smallVae || chosenVae=="full"&&smallVae) continue;
            var name=$"{task}-{(native?"native":"mp")}-{(smallVae?"small":"full")}";
            using var loaded=ImageFiles.Load(Path.Combine(sourceFolder,task is AiTaskKind.Relight or AiTaskKind.Harmonize ? "portrait.jpeg":"car-interior.jpeg"));
            var source=Pixels.NewColor(384,688);
            using(var canvas=new SKCanvas(source)) canvas.DrawImage(Pixels.ImageOf(loaded),SKRect.Create(source.Width,source.Height),new SKSamplingOptions(SKCubicResampler.Mitchell));
            Pixels.Invalidate(source);
            var document=new Composa.Model.Document(source.Width,source.Height); var layer=Composa.Model.Layer.Raster("Photo",source);
            document.Layers.Add(layer); document.SetActive(layer.Id); var session=new EditorSession(document);
            var region=new SKRect(190,100,330,240);
            if(task is AiTaskKind.Relight or AiTaskKind.Harmonize) { session.SelectEllipse(new SKRect(115,120,290,335)); session.FeatherSelection(8); }
            else if(task!=AiTaskKind.GenerativeExpand) session.SelectRect(region);
            if(task==AiTaskKind.RemoveObject)
            {
                var subject=Pixels.NewColor(48,36); subject.Erase(SKColors.Red);
                session.AddImageLayer("Removal target",subject,new SKPoint(260,170),fit:false);
            }
            var target=session.Selection==null?document.Bounds:SelectionMask.Bounds(session.Selection,1);
            var size=AiDimensions.FromMegapixels(task==AiTaskKind.Harmonize?1:0.5,target.Width,target.Height);
            var request=new AiTaskRequest { Task=task,
                Prompt=task==AiTaskKind.GenerativeFill?"A small glossy red apple resting on the white dashboard, realistic lighting and perspective."
                    : task==AiTaskKind.RemoveObject?"Remove the red rectangle. Restore the same original white dashboard."
                    : "Keep the identity, face, hat and clothes unchanged. Slightly warm the existing sunlight, retaining all photographic detail.",
                ExpansionBounds=task==AiTaskKind.GenerativeExpand?new(-16,-16,400,704):null,
                ExpansionMode=native?AiExpansionMode.WholeImage:AiExpansionMode.MaskedRegion,
                ExpansionMinimumSide=native?0:768,
                Settings=new() { Width=size.Width,Height=size.Height,Seed=31,
                    Values=new() { ["imageOriginalSize"]=native,["maskGrow"]=4,["maskBlend"]=16,["maskBlur"]=4,
                        ["maskContext"]=1.2,["colorMatch"]=task is AiTaskKind.Relight or AiTaskKind.Harmonize ? "strong" : "subtle" } } };
            var service=new AiTaskService(()=>url,catalog.Root) { SelectedEngine=engine };
            if(smallVae) service.ModelSelections=_=>WorkflowModels.Slots(catalog.ReadWorkflow(engine,engine.Workflow(engine.Binding(task)!.Workflow)),engine.Id)
                .Where(slot=>slot.Kind==EngineAssetKind.Vae).ToDictionary(slot=>slot.Key,_=>"full_encoder_small_decoder.safetensors");
            using var before=session.Flatten(); var state=session.History.CurrentId;
            ImageFiles.Save(before,Path.Combine(folder,name+"-before.png"),ExportFormat.Png);
            await service.RunAsync(new EditorCommandService(session),request,TestContext.Current.CancellationToken);
            using var result=session.Flatten();
            ImageFiles.Save(result,Path.Combine(folder,name+"-after.png"),ExportFormat.Png);
            Assert.Equal(AiOperationStatus.Completed,service.Operation!.Status);
            if(task!=AiTaskKind.GenerativeExpand)
            {
                var mask=session.ActiveLayer!.Mask!; var count=0;
                for(var y=0;y<before.Height;y++) for(var x=0;x<before.Width;x++) if(mask.GetPixel(x,y).Alpha==0)
                { Assert.Equal(before.GetPixel(x,y),result.GetPixel(x,y)); count++; }
                Assert.True(count>before.Width*before.Height/2);
            }
            else if(!native) Assert.Equal(before.GetPixel(192,344),result.GetPixel(208,360));
            session.Undo(); Assert.Equal(state,session.History.CurrentId);
            using var restored=session.Flatten(); Assert.Equal(before.GetPixelSpan().ToArray(),restored.GetPixelSpan().ToArray());
            session.Redo(); using var redone=session.Flatten(); Assert.Equal(result.GetPixelSpan().ToArray(),redone.GetPixelSpan().ToArray());
            output.WriteLine(name+": generated, mask coverage and undo/redo checked.");
        }
    }

    [Theory]
    [InlineData(255)]
    [InlineData(1)]
    public async Task A_single_edge_pixel_preserves_its_conditioning_coverage_and_zero_technical_padding(byte coverage)
    {
        var url=Environment.GetEnvironmentVariable("COMPOSA_FLUX_DIAGNOSTIC_URL"); if(string.IsNullOrWhiteSpace(url)) return;
        using var client=new ComfyClient(url); var (_,caps)=await client.TestConnectionAsync(TestContext.Current.CancellationToken);
        var session=EditorSession.NewCanvas(17,17,SKColors.White); session.SelectRect(new SKRect(0,0,1,1));
        var request=new AiTaskRequest { Task=AiTaskKind.GenerativeFill,Settings=new() { Values=new()
            { ["imageOriginalSize"]=true,["maskGrow"]=0,["maskBlend"]=0,["maskBlur"]=0,["maskContext"]=1.0 } } };
        using var inputs=AiTaskInputPreparer.Prepare(session,request);
        inputs.SelectionMask!.GetPixelSpan()[0]=coverage; Pixels.Invalidate(inputs.SelectionMask);
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai","engines")); var engine=catalog.Find("flux2-klein-intel-xpu")!; var binding=engine.Binding(request.Task)!;
        var files=new Dictionary<string,string>();
        foreach(var (key,bitmap) in inputs.Images()) if(binding.Inputs.ContainsKey(key)) files[key]=await client.UploadPngAsync(key,bitmap,TestContext.Current.CancellationToken);
        var values=inputs.Values(files); foreach(var (key,value) in request.Settings.Values) values[key]=value;
        var graph=WorkflowBinder.Bind(catalog.ReadWorkflow(engine,engine.Workflow(binding.Workflow)),binding,values);
        WorkflowExecution.MaskedEdit(graph,inputs,request,caps); using var edit=new EditableMaskedWorkflow(inputs,request); edit.Bind(graph);
        graph["diagnostic_noise"]=new JsonObject { ["class_type"]="MaskToImage",["inputs"]=new JsonObject { ["mask"]=new JsonArray("composa_edit_padded_mask",0) } };
        graph["save"]!["inputs"]!["images"]=new JsonArray("diagnostic_noise",0);
        var execution=await client.ExecuteAsync(graph,cancellationToken:TestContext.Current.CancellationToken);
        using(execution.History)
        using(var noise=await client.DownloadAsync(Assert.Single(execution.Images),TestContext.Current.CancellationToken))
        {
            Assert.Equal((64,64),(noise.Width,noise.Height)); Assert.Equal(coverage,noise.GetPixel(0,0).Red);
            for(var y=0;y<64;y++) for(var x=0;x<64;x++) if(x!=0 || y!=0) Assert.Equal(0,noise.GetPixel(x,y).Red);
        }
        Assert.Equal(coverage,edit.Mask.GetPixel(0,0).Alpha); // denoising never changes editable coverage
    }
}
