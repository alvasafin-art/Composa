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
    public void A_weak_contact_speck_cannot_leave_the_main_cast_component_detached()
    {
        var session=EditorSession.NewCanvas(200,180,new SKColor(200,200,200));var png=Pixels.NewColor(40,80);
        using(var draw=new SKCanvas(png))using(var paint=new SKPaint {Color=new SKColor(80,80,80)})draw.DrawRect(2,1,36,77,paint);
        Pixels.Invalidate(png);session.AddImageLayer("Product",png,new(100,80),fit:false);
        var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        generated.SetPixel(100,foot,new(190,190,190));
        for(var y=foot+11;y<foot+24;y++)for(var x=86;x<114;x++)generated.SetPixel(x,y,new(80,80,80));
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(input,generated,request);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow"));
            Assert.True(result.ContactCorrectionY<0);Assert.True(Enumerable.Range(86,28).Any(x=>shadow.Pixels.GetPixel(x,foot).Alpha>32));
        }
        finally{foreach(var layer in result.Layers)layer.Pixels.Dispose();}
    }
    [Fact]
    public void A_translucent_product_base_not_its_opaque_cap_defines_ground_support()
    {
        var session=EditorSession.NewCanvas(100,80,SKColors.White); var product=Pixels.NewColor(20,40);
        for(var y=1;y<39;y++) for(var x=3;x<17;x++) product.SetPixel(x,y,new(80,100,120,y<9?(byte)255:(byte)90));
        Pixels.Invalidate(product);session.AddImageLayer("Glass product",product,new(50,40),fit:false);
        using var input=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.MatchToScene});
        Assert.True(input.SupportAlpha<100);Assert.Equal(input.SubjectBounds.Bottom,input.SupportBounds.Bottom);
        Assert.True(input.ShadowMask.GetPixel(50,input.SupportBounds.Bottom).Alpha>0);
    }
    [Fact]
    public void Contact_shadow_is_not_cut_away_from_the_translucent_antialias_fringe()
    {
        var session=EditorSession.NewCanvas(100,80,new SKColor(200,200,200)); var png=Pixels.NewColor(20,30);
        for(var y=1;y<29;y++) for(var x=2;x<18;x++) png.SetPixel(x,y,new(80,80,80,y<25?(byte)255:y==25?(byte)192:y==26?(byte)96:y==27?(byte)32:(byte)8));
        Pixels.Invalidate(png);session.AddImageLayer("Soft PNG",png,new(50,40),fit:false);
        var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene}; using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        var foot=input.SupportBounds.Bottom;
        Assert.True(input.ObjectAlpha.GetPixel(50,foot).Alpha>16);Assert.Equal(255,input.ShadowMask.GetPixel(50,foot).Alpha);
        using var generated=Pixels.Clone(input.Before);
        for(var y=foot;y<foot+12;y++) for(var x=42;x<58;x++)
        {
            var a=input.ObjectAlpha.GetPixel(x,y).Alpha/255d;
            var value=(byte)Math.Round(80*a+100*(1-a));generated.SetPixel(x,y,new(value,value,value));
        }
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(input,generated,request);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow"));
            Assert.True(shadow.Pixels.GetPixel(50,foot).Alpha>=8);
            Assert.Equal(0,result.ContactCorrectionY);
            Assert.Equal(input.ObjectAlpha.GetPixel(50,foot).Alpha,Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-object")).Pixels.GetPixel(50,foot).Alpha);
        }
        finally {foreach(var layer in result.Layers) layer.Pixels.Dispose();}
    }
    [Fact]
    public void Small_generated_contact_gap_is_registered_to_the_source_contour_without_moving_it()
    {
        var session=Scene(); var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);
        for(var y=58;y<69;y++) for(var x=43;x<58;x++) generated.SetPixel(x,y,new(60,50,40));
        Pixels.Invalidate(generated); var result=SceneEditResult.Extract(input,generated,request);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow"));
            Assert.True(result.ContactCorrectionY<0);
            var foot=input.SubjectBounds.Bottom;
            Assert.True(Enumerable.Range(43,15).Any(x=>shadow.Pixels.GetPixel(x,foot).Alpha>=8));
            var subject=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-object"));
            for(var y=0;y<80;y++) for(var x=0;x<100;x++) Assert.Equal(input.ObjectAlpha.GetPixel(x,y).Alpha,subject.Pixels.GetPixel(x,y).Alpha);
        }
        finally {foreach(var layer in result.Layers) layer.Pixels.Dispose();}
    }
    [Fact]
    public void Codec_changes_and_outer_glow_do_not_become_subject_light_or_a_false_shadow()
    {
        var session=Scene(); var request=new AiTaskRequest {Task=AiTaskKind.Relight,Scene=new() {AddShadows=false}};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var baseline=Pixels.Clone(input.Before);
        for(var y=0;y<80;y++) for(var x=0;x<100;x++)
        {
            var color=baseline.GetPixel(x,y); baseline.SetPixel(x,y,new((byte)Math.Min(255,color.Red+20),color.Green,color.Blue));
        }
        Pixels.Invalidate(baseline); using var generated=Pixels.Clone(baseline);
        for(var y=20;y<60;y++) for(var x=25;x<75;x++) if(input.ObjectAlpha.GetPixel(x,y).Alpha==0) generated.SetPixel(x,y,SKColors.White);
        Pixels.Invalidate(generated); var result=SceneEditResult.Extract(input,generated,request,reconstruction:baseline);
        try
        {
            var subject=Assert.Single(result.Layers); Assert.Equal(0,result.ShadowPixels);
            for(var y=0;y<80;y++) for(var x=0;x<100;x++) Assert.Equal(input.Foreground.GetPixel(x,y),subject.Pixels.GetPixel(x,y));
        }
        finally {foreach(var layer in result.Layers) layer.Pixels.Dispose();}
    }
    [Fact]
    public void Background_cleanup_returns_one_complete_plate_instead_of_pasting_a_rectangle()
    {
        var session=Scene(); using var input=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.ChangeBackground});
        using var layout=Pixels.Clone(input.Before); using var plate=new SceneBackgroundPlate(input,layout,4);
        using var generated=Pixels.NewColor(100,80); generated.Erase(SKColors.Blue);
        using var clean=plate.Finish(generated,layout);
        Assert.Equal(SKColors.Blue,clean.GetPixel(50,40)); Assert.Equal(SKColors.Blue,clean.GetPixel(0,0));
        Assert.Equal(0,plate.Mask.GetPixel(0,0).Alpha); Assert.Equal(255,plate.Mask.GetPixel(50,40).Alpha);
    }
    [Fact]
    public void Change_background_retains_exact_subject_colors_and_commits_once()
    {
        var session=Scene(); var request=new AiTaskRequest {Task=AiTaskKind.ChangeBackground};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        Assert.Equal(input.Before.Bytes,input.InitialImage.Bytes);
        Assert.Equal(0,input.GenerationMask.GetPixel(50,40).Alpha); Assert.Equal(255,input.GenerationMask.GetPixel(0,0).Alpha);
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
