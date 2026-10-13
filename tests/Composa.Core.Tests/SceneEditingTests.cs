using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SceneEditingTests
{
    private static EditorSession Scene()
    {
        var session=EditorSession.NewCanvas(100,80,new SKColor(180,160,140));
        var png=Pixels.NewColor(20,30);
        using(var canvas=new SKCanvas(png)) using(var paint=new SKPaint {Color=new SKColor(80,90,100)}) canvas.DrawRect(2,1,16,28,paint);
        Pixels.Invalidate(png); session.AddImageLayer("PNG",png,new(50,40),fit:false);
        return session;
    }
    [Fact]
    public void PNG_alpha_and_placement_define_the_subject_without_changing_the_document()
    {
        var session=Scene(); var pixels=session.ActiveLayer!.Pixels!; var bytes=pixels.Bytes; var state=session.History.CurrentId;
        using var input=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.MatchToScene});
        Assert.True(input.IsCutout); Assert.Null(session.Selection); Assert.Equal(state,session.History.CurrentId); Assert.Equal(bytes,pixels.Bytes);
        Assert.Equal(255,input.SubjectMask.GetPixel(50,40).Alpha); Assert.Equal(0,input.SubjectMask.GetPixel(10,10).Alpha);
        Assert.Equal(0,input.ShadowMask.GetPixel(50,40).Alpha);
        Assert.True(input.ShadowMask.GetPixel(35,60).Alpha>0);
    }
    [Fact]
    public void Scene_result_keeps_original_silhouette_and_separates_connected_floor_darkening()
    {
        var session=Scene(); var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);
        for(var y=53;y<67;y++) for(var x=30;x<58;x++) generated.SetPixel(x,y,new(60,50,40));
        generated.SetPixel(5,5,SKColors.Black); // A model change outside the floor must never become a shadow.
        Pixels.Invalidate(generated);
        var result=SceneEditResult.Extract(input,generated,request);
        try
        {
            Assert.True(result.ShadowPixels>0); var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow"));
            Assert.Equal(0,shadow.Pixels.GetPixel(5,5).Alpha);
            var subject=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-object"));
            for(var y=0;y<80;y++) for(var x=0;x<100;x++) Assert.Equal(input.ObjectAlpha.GetPixel(x,y).Alpha,subject.Pixels.GetPixel(x,y).Alpha);
        }
        finally {foreach(var layer in result.Layers) layer.Pixels.Dispose();}
    }
    [Fact]
    public void Extracted_shadow_alpha_matches_encoded_RGB_compositing_instead_of_over_darkening()
    {
        var session=Scene(); session.Document.Layers[0].Pixels=Pixels.NewColor(100,80); session.Document.Layers[0].Pixels!.Erase(new SKColor(200,200,200));
        Pixels.Invalidate(session.Document.Layers[0].Pixels!);
        var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);
        for(var y=53;y<68;y++) for(var x=27;x<59;x++) generated.SetPixel(x,y,new(100,100,100));
        Pixels.Invalidate(generated); var result=SceneEditResult.Extract(input,generated,request);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow"));
            Assert.InRange(shadow.Pixels.GetPixel(35,61).Alpha,125,130);
        }
        finally {foreach(var layer in result.Layers) layer.Pixels.Dispose();}
    }
    [Fact]
    public void Change_background_retains_exact_subject_colors_and_commits_once()
    {
        var session=Scene(); var request=new AiTaskRequest {Task=AiTaskKind.ChangeBackground};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        Assert.Equal(input.Background.Bytes,input.InitialImage.Bytes);
        using var generated=Pixels.NewColor(100,80); generated.Erase(SKColors.Blue);
        var result=SceneEditResult.Extract(input,generated,request);
        var original=session.ActiveLayer!; var state=session.History.CurrentId; using var before=session.Flatten();
        session.InsertSceneEdit(request.Task,original.Id,[result],input.IsCutout,false);
        Assert.False(original.Visible); Assert.Equal(SKColors.Blue,session.Composite().GetPixel(0,0));
        Assert.Equal(before.GetPixel(50,40),session.Composite().GetPixel(50,40));
        session.Undo(); Assert.Equal(state,session.History.CurrentId); Assert.Equal(before.Bytes,session.Flatten().Bytes);
        session.Redo(); Assert.Equal(SKColors.Blue,session.Composite().GetPixel(0,0));
    }
    [Theory]
    [InlineData(RelightPreset.SoftboxLeft)]
    [InlineData(RelightPreset.SoftboxRight)]
    [InlineData(RelightPreset.WindowLight)]
    [InlineData(RelightPreset.GoldenHour)]
    [InlineData(RelightPreset.Backlight)]
    public void Every_lighting_preset_has_practical_instructions_and_bounded_settings(RelightPreset preset)
    {
        var normalized=new SceneEditSettings {Preset=preset,Denoise=double.NaN,Strength=9,ShadowReach=-1}.Normalized();
        Assert.Equal(.8,normalized.Denoise); Assert.Equal(1,normalized.Strength); Assert.Equal(.1,normalized.ShadowReach);
        Assert.NotEmpty(normalized.LightingInstruction); Assert.NotEqual(preset.ToString(),SceneEditSettings.Name(preset));
    }
}
