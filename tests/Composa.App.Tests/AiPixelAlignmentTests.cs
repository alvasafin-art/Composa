using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class AiPixelAlignmentTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    public void Original_size_pads_without_scaling_and_preserves_every_source_pixel_at_saved_coordinates(bool expand, bool tinyEdge)
    {
        var source=Pixels.NewColor(641,423);
        for(var y=0;y<source.Height;y++) for(var x=0;x<source.Width;x++)
            source.SetPixel(x,y,new SKColor((byte)(x%251),(byte)(y%251),(byte)((x+3*y)%251)));
        Pixels.Invalidate(source);
        var document=new Document(source.Width,source.Height); var layer=Layer.Raster("Coordinate pattern",source);
        document.Layers.Add(layer); document.SetActive(layer.Id); var session=new EditorSession(document);
        if(!expand) session.SelectRect(tinyEdge ? new SKRect(0,1,11,29) : new SKRect(250,160,331,239));
        var request=new AiTaskRequest
        {
            Task=expand ? AiTaskKind.GenerativeExpand : AiTaskKind.GenerativeFill,
            ExpansionBounds=expand ? new SKRectI(-17,-19,699,437) : null, ExpansionMinimumSide=0,
            Settings=new() { Values=new() { ["imageOriginalSize"]=true,["maskGrow"]=expand?16:4,["maskBlend"]=tinyEdge?0:expand?48:8,
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
