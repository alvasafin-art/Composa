using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class FluxMaskGeometryTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void MP_applies_to_the_total_context_crop_and_never_stretches_the_aspect(double mp)
    {
        var session=EditorSession.NewCanvas(1537,991,SKColors.White); session.SelectRect(new SKRect(600,350,864,526));
        var requested=AiDimensions.FromMegapixels(mp,264,176);
        byte[]? finalMask=null;
        foreach(var context in new[] {1.2,2.0,3.0})
        {
            var request = new AiTaskRequest { Task=AiTaskKind.GenerativeFill,
                Settings=new() { Width=requested.Width,Height=requested.Height,Values=new()
                    { ["imageOriginalSize"]=false,["maskGrow"]=4,["maskBlend"]=8,["maskBlur"]=4,["maskContext"]=context } } };
            using var inputs=AiTaskInputPreparer.Prepare(session,request); using var edit=new EditableMaskedWorkflow(inputs,request);
            Bind(edit,inputs,request);
            var sx=(double)edit.ContentSize.Width/edit.Bounds.Width; var sy=(double)edit.ContentSize.Height/edit.Bounds.Height;
            Assert.InRange(Math.Abs(sx/sy-1),0,0.003);
            Assert.InRange(edit.ContentSize.Width*(double)edit.ContentSize.Height/(requested.Width*(double)requested.Height),0.997,1.003);
            Assert.InRange(edit.GenerationSize.Width-edit.ContentSize.Width,0,15);
            Assert.InRange(edit.GenerationSize.Height-edit.ContentSize.Height,0,15);
            var pixels=edit.Mask.GetPixelSpan().ToArray();
            if(finalMask != null) Assert.Equal(finalMask,pixels); finalMask=pixels;
            Assert.Equal(255,edit.Mask.GetPixel(600,430).Alpha); // core opaque, seam outside
            Assert.Equal(0,edit.Mask.GetPixel(585,430).Alpha);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wrong_model_output_dimensions_are_rejected_in_both_size_modes(bool original)
    {
        var session=EditorSession.NewCanvas(173,121,SKColors.White); session.SelectRect(new SKRect(50,40,91,81));
        var request=new AiTaskRequest { Task=AiTaskKind.GenerativeFill,Settings=new() { Width=128,Height=128,
            Values=new() { ["imageOriginalSize"]=original,["maskBlend"]=8,["maskBlur"]=4 } } };
        using var inputs=AiTaskInputPreparer.Prepare(session,request); using var edit=new EditableMaskedWorkflow(inputs,request); Bind(edit,inputs,request);
        Assert.Throws<InvalidDataException>(()=>edit.Finish(Pixels.NewColor(edit.GenerationSize.Width-16,edit.GenerationSize.Height)));
        Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void MP_padding_is_discarded_before_placing_a_non_square_result_back_on_canvas()
    {
        var session=EditorSession.NewCanvas(173,121,SKColors.CornflowerBlue); session.SelectRect(new SKRect(50,40,81,93));
        var request=new AiTaskRequest { Task=AiTaskKind.GenerativeFill,Settings=new() { Width=144,Height=256,
            Values=new() { ["maskGrow"]=0,["maskBlend"]=0,["maskBlur"]=0,["maskContext"]=1.0 } } };
        using var inputs=AiTaskInputPreparer.Prepare(session,request); using var edit=new EditableMaskedWorkflow(inputs,request); Bind(edit,inputs,request);
        var decoded=Pixels.NewColor(edit.GenerationSize.Width,edit.GenerationSize.Height); decoded.Erase(SKColors.Magenta);
        using(var canvas=new SKCanvas(decoded)) using(var paint=new SKPaint { Color=SKColors.CornflowerBlue })
            canvas.DrawRect(SKRect.Create(edit.ContentSize.Width,edit.ContentSize.Height),paint);
        Pixels.Invalidate(decoded);
        using var raw=edit.Finish(decoded);
        Assert.Equal(SKColors.CornflowerBlue,raw.GetPixel(80,92)); Assert.Equal(SKColors.CornflowerBlue,raw.GetPixel(50,40));
        Assert.Equal(SKColors.CornflowerBlue,raw.GetPixel(edit.Bounds.Right-1,edit.Bounds.Bottom-1));
        Assert.Equal(SKColors.CornflowerBlue,raw.GetPixel(0,0));
        if (edit.Bounds.Right < raw.Width) Assert.Equal(SKColors.CornflowerBlue,raw.GetPixel(edit.Bounds.Right,edit.Bounds.Bottom-1));
        if (edit.Bounds.Bottom < raw.Height) Assert.Equal(SKColors.CornflowerBlue,raw.GetPixel(edit.Bounds.Right-1,edit.Bounds.Bottom));
    }

    [Fact]
    public void A_single_pixel_canvas_has_valid_native_padding_and_no_invalid_blur_kernel()
    {
        var session=EditorSession.NewCanvas(1,1,SKColors.White); session.SelectRect(new SKRect(0,0,1,1));
        var request=new AiTaskRequest { Task=AiTaskKind.GenerativeFill,Settings=new() { Values=new() { ["imageOriginalSize"]=true,["maskBlur"]=64 } } };
        using var inputs=AiTaskInputPreparer.Prepare(session,request); using var edit=new EditableMaskedWorkflow(inputs,request); Bind(edit,inputs,request);
        Assert.Equal((64,64),edit.GenerationSize);
        var decoded=Pixels.NewColor(64,64); decoded.Erase(SKColors.Blue);
        using var raw=edit.Finish(decoded); Assert.Equal(SKColors.Blue,raw.GetPixel(0,0));
    }

    private static void Bind(EditableMaskedWorkflow edit,AiTaskInputs inputs,AiTaskRequest request)
    {
        var catalog=new EngineCatalog(Path.Combine(AppContext.BaseDirectory,"ai","engines")); var engine=catalog.Find("flux2-klein-intel-xpu")!;
        var binding=engine.Binding(request.Task)!;
        var values=inputs.Values(inputs.Images().ToDictionary(pair=>pair.Key,pair=>pair.Key+".png"));
        foreach(var (key,value) in request.Settings.Values) values[key]=value;
        var graph=WorkflowBinder.Bind(catalog.ReadWorkflow(engine,engine.Workflow(binding.Workflow)),binding,values);
        WorkflowExecution.MaskedEdit(graph,inputs,request,new()); edit.Bind(graph);
    }
}
