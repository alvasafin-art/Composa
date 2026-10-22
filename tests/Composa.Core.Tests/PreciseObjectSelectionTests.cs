using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PreciseObjectSelectionTests
{
    private static EditorSession Scene()
    {
        var document=new Document(160,120) {Depth=ColorDepth.Sixteen,Profile=RgbProfile.DisplayP3};
        var session=new EditorSession(document);
        using var image=Pixels.NewColor(160,120);image.Erase(SKColors.White);
        using(var draw=new SKCanvas(image))using(var paint=new SKPaint {Color=SKColors.DarkRed,IsAntialias=true})
            draw.DrawOval(new(39.5f,19.5f,120.5f,100.5f),paint);
        session.AddImageLayer("Original",Pixels.ConvertWorking(image,ColorDepth.Sixteen,RgbProfile.DisplayP3),new(80,60),fit:false);
        return session;
    }

    [Theory]
    [InlineData(SelectionMode.Replace)]
    [InlineData(SelectionMode.Add)]
    [InlineData(SelectionMode.Subtract)]
    [InlineData(SelectionMode.Intersect)]
    public async Task Precise_selection_preserves_RGB16_original_masks_modes_and_one_undo_step(SelectionMode mode)
    {
        var session=Scene();var layer=session.ActiveLayer!;
        session.AddMask(layer,hideAll:true);var oldMask=layer.Mask!;var maskBytes=oldMask.Bytes;
        var pixels=layer.Pixels!;var bytes=pixels.Bytes;
        session.SelectRect(new(0,0,85,120));var initial=session.Selection!;var state=session.History.CurrentId;var steps=session.History.Count;
        await session.SelectPromptObjectAsync(PromptModels.EfficientSamS,null,new(30,10,130,110),mode,preciseEdges:true);
        var selection=Assert.IsType<SKBitmap>(session.Selection);
        Assert.Equal(SKColorType.Alpha8,selection.ColorType);Assert.Equal(steps+1,session.History.Count);
        Assert.Equal(mode is SelectionMode.Add or SelectionMode.Subtract?255:0,selection.GetPixel(5,5).Alpha);
        Assert.Equal(mode==SelectionMode.Subtract?0:255,selection.GetPixel(65,60).Alpha);
        Assert.Equal(mode is SelectionMode.Replace or SelectionMode.Add?255:0,selection.GetPixel(105,60).Alpha);
        if(mode!=SelectionMode.Subtract)Assert.Contains(selection.Bytes,value=>value is >0 and <255);
        Assert.Same(pixels,layer.Pixels);Assert.Equal(bytes,pixels.Bytes);Assert.Equal(SKColorType.RgbaF32,pixels.ColorType);
        Assert.Equal(RgbProfile.DisplayP3,Pixels.ProfileOf(pixels));Assert.Same(oldMask,layer.Mask);Assert.Equal(maskBytes,oldMask.Bytes);
        session.Undo();Assert.Equal(state,session.History.CurrentId);Assert.Same(initial,session.Selection);
        session.Redo();Assert.Equal(selection.Bytes,session.Selection!.Bytes);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("edit")]
    [InlineData("layer")]
    [InlineData("tab")]
    public async Task Cancelling_or_changing_the_source_between_SAM_and_matting_does_not_apply_a_partial_selection(string change)
    {
        var session=Scene();session.AddBlankLayer();var other=session.ActiveLayer!;session.SelectLayer(session.Document.Layers[0].Id);
        session.SelectRect(new(0,0,15,15));var original=session.Selection!;
        using var cancellation=new CancellationTokenSource();using var ready=new ManualResetEventSlim();using var resume=new ManualResetEventSlim();
        var apply=true;
        var progress=new DirectProgress(message=>
        {
            if(message!="Refining object edges…")return;
            ready.Set();if(!resume.Wait(TimeSpan.FromSeconds(20)))throw new TimeoutException("The test did not resume matting.");
        });
        var pending=session.SelectPromptObjectAsync(PromptModels.EfficientSamS,null,new(30,10,130,110),SelectionMode.Replace,
            cancellation.Token,preciseEdges:true,progress:progress,canApply:()=>apply);
        try
        {
            Assert.True(await Task.Run(()=>ready.Wait(TimeSpan.FromSeconds(20))));
            if(change=="cancel")cancellation.Cancel();
            else if(change=="edit")session.Fill(SKColors.Blue);
            else if(change=="layer")session.SelectLayer(other.Id);
            else apply=false;
            var state=session.History.CurrentId;resume.Set();
            if(change=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            else await pending;
            Assert.Equal(state,session.History.CurrentId);Assert.Same(original,session.Selection);
        }
        finally {resume.Set();}
    }

    private sealed class DirectProgress(Action<string> report):IProgress<string>
    {public void Report(string value)=>report(value);}
}
