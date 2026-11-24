using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class AiPixelAlignmentTests(ITestOutputHelper output)
{
    [Fact]
    public void Gpt_edit_keeps_full_resolution_upload_and_fits_saved_model_pixels_once()
    {
        const int width = 1600, height = 1200;
        var session = EditorSession.NewCanvas(width, height, SKColors.DarkSlateGray);
        var source = session.ActiveLayer!.Pixels!;
        var request = new AiTaskRequest
        {
            Task = AiTaskKind.ImageEdit,
            Settings = new() { Width = 1024, Height = 768, Values = new() { ["apiQuality"] = "low" } }
        };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var engine = catalog.Find("chatgpt-image-2.5")!;
        var workflow = catalog.ReadWorkflow(engine, engine.Workflow(engine.Binding(request.Task)!.Workflow));
        using var partner = new PartnerImageInputs(inputs, request, engine.ApiModel);
        using var sent = SKBitmap.Decode(ComfyClient.EncodeUploadPng(partner.Images["apiSource"]));
        Assert.Equal(new SKSizeI(width, height), sent.Info.Size);
        using var decodedUpload = Pixels.DisplayCopy(sent);
        Assert.Equal(source.GetPixelSpan().ToArray(), decodedUpload.GetPixelSpan().ToArray());
        var node = partner.Bind(workflow, engine, new Dictionary<string, string> { ["apiSource"] = "source.png" }, 17)["gpt"]!["inputs"]!;
        Assert.Equal("gpt-image-2.5-sunburst", node["model"]!.GetValue<string>());
        Assert.Equal("low", node["model.quality"]!.GetValue<string>());
        Assert.Equal(1, node["n"]!.GetValue<int>());
        Assert.Equal(1024, node["model.custom_width"]!.GetValue<int>());
        Assert.Equal(768, node["model.custom_height"]!.GetValue<int>());

        // Reuse the same decoded answer and inspect the same crop in document pixels.
        using var raw = Pixels.NewColor(1024, 768);
        using (var canvas = new SKCanvas(raw))
        {
            canvas.Clear(new SKColor(48, 48, 48));
            using var paint = new SKPaint { Color = new SKColor(208, 208, 208) };
            for (var x = 256; x < 768; x += 8) canvas.DrawRect(x, 256, 4, 256, paint);
        }
        using var before = Pixels.NewColor(width, height);
        using (var canvas = new SKCanvas(before))
            canvas.DrawImage(Pixels.ImageOf(raw), before.Info.Rect, new SKSamplingOptions(SKCubicResampler.Mitchell));
        var after = partner.FinishUnmasked(Pixels.Clone(raw));
        static int Detail(SKBitmap image) => Enumerable.Range(440, 240)
            .Sum(x => Math.Abs(image.GetPixel(x + 1, 600).Red - image.GetPixel(x, 600).Red));
        var beforeDetail = Detail(before);
        var afterDetail = Detail(after);
        output.WriteLine($"Luma variation over the same 240 px at 1:1: {beforeDetail} -> {afterDetail}");
        Assert.True(afterDetail > beforeDetail);
        AiTaskService.Insert(new EditorCommandService(session), request.Task, AiOutputMode.NewLayer, [after], inputs.TargetBounds);
        Assert.Same(after, session.ActiveLayer!.Pixels);
        Assert.Equal(new SKSizeI(width, height), after.Info.Size);
        Assert.Equal(SKRect.Create(width, height), session.ActiveLayer.VisibleBounds);
        Assert.Equal(SKColors.DarkSlateGray, source.GetPixel(0, 0));
    }

    [Fact]
    public void Small_original_size_gpt_edit_does_not_interpolate_source_before_upload()
    {
        var session = EditorSession.NewCanvas(800, 600, SKColors.CornflowerBlue);
        var request = new AiTaskRequest { Task = AiTaskKind.ImageEdit, Settings = new() { Values = new() { ["imageOriginalSize"] = true } } };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var partner = new PartnerImageInputs(inputs, request, "gpt-image-2.5-sunburst");
        Assert.Equal(new SKSizeI(800, 600), partner.Images["apiSource"].Info.Size);
    }

    [Theory]
    [InlineData(false,false,true)]
    [InlineData(false,true,true)]
    [InlineData(true,false,true)]
    [InlineData(true,false,false)]
    public void Original_size_pads_without_scaling_and_preserves_every_source_pixel_at_saved_coordinates(bool expand, bool tinyEdge, bool originalSetting)
    {
        var source=Pixels.NewColor(1401,1003);
        for(var y=0;y<source.Height;y++) for(var x=0;x<source.Width;x++)
            source.SetPixel(x,y,new SKColor((byte)(x%251),(byte)(y%251),(byte)((x+3*y)%251)));
        Pixels.Invalidate(source);
        var document=new Document(source.Width,source.Height); var layer=Layer.Raster("Coordinate pattern",source);
        document.Layers.Add(layer); document.SetActive(layer.Id); var session=new EditorSession(document);
        if(!expand) session.SelectRect(tinyEdge ? new SKRect(0,1,1381,979) : new SKRect(80,80,1321,923));
        var request=new AiTaskRequest
        {
            Task=expand ? AiTaskKind.GenerativeExpand : AiTaskKind.GenerativeFill,
            ExpansionBounds=expand ? new SKRectI(-17,-19,1459,1017) : null, ExpansionMinimumSide=0,
            Settings=new() { Values=new() { ["imageOriginalSize"]=originalSetting,["maskGrow"]=expand?16:4,["maskBlend"]=tinyEdge?0:expand?48:8,
                ["maskBlur"]=expand?16:4,["maskContext"]=expand?2:1.2,["colorMatch"]=expand?"subtle":"off" } }
        };
        using var inputs=AiTaskInputPreparer.Prepare(session,request);
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai","engines")); var engine=catalog.Find("flux2-klein-intel-xpu")!;
        var binding=engine.Binding(request.Task)!;
        var graph=WorkflowBinder.Bind(catalog.ReadWorkflow(engine,engine.Workflow(binding.Workflow)),binding,
            inputs.Values(inputs.Images().ToDictionary(pair=>pair.Key,pair=>pair.Key+".png")));
        WorkflowExecution.MaskedEdit(graph,inputs,request,new ComfyServerCapabilities());
        using var editable=new EditableMaskedWorkflow(inputs,request); editable.Bind(graph);
        Assert.Equal("ImagePadForOutpaint",graph["composa_edit_size"]!["class_type"]!.GetValue<string>());
        Assert.Equal(0,graph["composa_edit_size"]!["inputs"]!["left"]!.GetValue<int>());
        Assert.Equal(0,graph["composa_edit_size"]!["inputs"]!["top"]!.GetValue<int>());
        Assert.Equal("MaskComposite",graph["composa_edit_padded_mask"]!["class_type"]!.GetValue<string>());
        Assert.False(graph.ContainsKey("composa_edit_mask_size"));
        Assert.Equal(0,editable.GenerationSize.Width%16); Assert.Equal(0,editable.GenerationSize.Height%16);
        using var context=expand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
        var decoded=Pixels.NewColor(editable.GenerationSize.Width,editable.GenerationSize.Height); decoded.Erase(SKColors.CornflowerBlue);
        using(var canvas=new SKCanvas(decoded))
        {
            canvas.ClipRect(SKRect.Create(editable.Bounds.Width,editable.Bounds.Height));
            canvas.DrawImage(Pixels.ImageOf(context),-editable.Bounds.Left,-editable.Bounds.Top);
        }
        Pixels.Invalidate(decoded);
        using var raw=editable.Finish(decoded);
        var offsetX=expand?17:0; var offsetY=expand?19:0;
        for(var y=0;y<source.Height;y++) for(var x=0;x<source.Width;x++)
            Assert.Equal(source.GetPixel(x,y),raw.GetPixel(x+offsetX,y+offsetY));
        if(expand) Assert.Equal(SKColors.CornflowerBlue,raw.GetPixel(0,0));
        Assert.Equal(1,session.Document.Layers.Count); // the pipeline must not mutate the live source
    }

    [Fact]
    public void Unexpected_original_size_output_is_rejected_instead_of_silently_stretched()
    {
        var session=EditorSession.NewCanvas(173,121,SKColors.White); session.SelectRect(new SKRect(50,40,91,81));
        var request=new AiTaskRequest { Task=AiTaskKind.GenerativeFill, Settings=new() { Values=new() { ["imageOriginalSize"]=true,["maskBlend"]=8 } } };
        using var inputs=AiTaskInputPreparer.Prepare(session,request);
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai","engines")); var engine=catalog.Find("flux2-klein-intel-xpu")!; var binding=engine.Binding(request.Task)!;
        var graph=WorkflowBinder.Bind(catalog.ReadWorkflow(engine,engine.Workflow(binding.Workflow)),binding,
            inputs.Values(inputs.Images().ToDictionary(pair=>pair.Key,pair=>pair.Key+".png")));
        WorkflowExecution.MaskedEdit(graph,inputs,request,new ComfyServerCapabilities());
        using var editable=new EditableMaskedWorkflow(inputs,request); editable.Bind(graph);
        var decoded=Pixels.NewColor(editable.GenerationSize.Width-16,editable.GenerationSize.Height);
        Assert.Throws<InvalidDataException>(()=>editable.Finish(decoded));
        Assert.Single(session.Document.Layers);
    }
}
