using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SceneRegressionTests
{
    private static EditorSession Scene()
    {
        var session=EditorSession.NewCanvas(200,160,new SKColor(200,200,200));
        var png=Pixels.NewColor(50,60);
        using(var canvas=new SKCanvas(png))using(var paint=new SKPaint {Color=new(100,100,100)})canvas.DrawRect(2,2,46,56,paint);
        Pixels.Invalidate(png);session.AddImageLayer("Source PNG",png,new(100,80),fit:false);return session;
    }
    [Fact]
    public void Repeated_harmonization_uses_original_pixels_and_excludes_its_previous_shadow_and_hidden_layers()
    {
        var session=Scene();var source=session.ActiveLayer!;var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};
        using var first=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(first.Before);
        for(var y=first.SupportBounds.Bottom;y<first.SupportBounds.Bottom+20;y++)for(var x=85;x<125;x++)generated.SetPixel(x,y,new(80,80,80));
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(first,generated,request);
        session.InsertSceneEdit(request.Task,source.Id,[result],true,false,first.PreviousResultIds);
        var oldObject=session.Document.AllLayers().Single(layer=>layer.Tags.Contains("scene-object"));
        var oldShadow=session.Document.AllLayers().Single(layer=>layer.Tags.Contains("scene-shadow"));
        var hidden=Layer.Raster("Hidden sentinel",Pixels.NewColor(200,160));hidden.Pixels!.Erase(SKColors.Magenta);hidden.Visible=false;session.Document.Layers.Add(hidden);
        using var second=SceneEditInput.Prepare(session.Document.Clone(),request); // Active result folder is supported.
        Assert.Equal(source.Id,second.SourceLayerId);Assert.Equal(first.Before.Bytes,second.Before.Bytes);Assert.Equal(first.Background.Bytes,second.Background.Bytes);
        Assert.Contains(oldObject.Id,second.PreviousResultIds);Assert.Contains(oldShadow.Id,second.PreviousResultIds);
        var next=SceneEditResult.Extract(second,second.Before,request);
        session.InsertSceneEdit(request.Task,source.Id,[next],true,false,second.PreviousResultIds);
        Assert.False(oldObject.Visible);Assert.False(oldShadow.Visible);Assert.Equal(2,session.Document.Layers.Count(layer=>layer.IsGroup));
        session.Undo();Assert.True(session.Document.Find(oldShadow.Id)!.Visible);session.Redo();
        using var third=SceneEditInput.Prepare(session.Document.Clone(),request);Assert.Equal(first.Before.Bytes,third.Before.Bytes);
    }
    [Fact]
    public void Legacy_result_folder_recovers_its_hidden_original_instead_of_relighting_the_last_output()
    {
        var session=Scene();var original=session.ActiveLayer!;
        var replacement=Pixels.Clone(original.Pixels!);replacement.Erase(SKColors.Blue);
        session.InsertAiOutput(AiTaskKind.MatchToScene,[new("Old subject",replacement,Tags:["scene-object"])],group:true);original.Visible=false;
        using var input=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.MatchToScene});
        Assert.Equal(original.Id,input.SourceLayerId);Assert.Equal(new SKColor(100,100,100),input.Foreground.GetPixel(100,80));
    }
    [Fact]
    public void A_selection_inside_an_opaque_photo_uses_that_photos_ground_not_the_layer_underneath()
    {
        var session=Scene();var photo=Pixels.NewColor(200,160);photo.Erase(new SKColor(180,140,100));
        session.AddImageLayer("Opaque photograph",photo,new(100,80),fit:false);
        var selection=Pixels.NewMask(200,160);using(var draw=new SKCanvas(selection))using(var paint=new SKPaint {Color=SKColors.White})draw.DrawRect(80,40,40,70,paint);
        Pixels.Invalidate(selection);session.Document.Selection=selection;
        using var input=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.MatchToScene});
        Assert.False(input.IsCutout);Assert.Equal(new SKColor(180,140,100),input.Background.GetPixel(140,120));
    }
    [Fact]
    public void Repeated_generation_keeps_the_current_placement_while_reading_the_original_texture()
    {
        var session=Scene();var original=session.ActiveLayer!;var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};
        using var first=SceneEditInput.Prepare(session.Document.Clone(),request);var result=SceneEditResult.Extract(first,first.Before,request);
        session.InsertSceneEdit(request.Task,original.Id,[result],true,false,first.PreviousResultIds,first.SourceFrame);
        var subject=session.Document.AllLayers().Single(layer=>layer.Tags.Contains("scene-object"));
        subject.Transform=subject.Transform with {X=12,Y=5};session.Document.SetActive(subject.Id);
        using var moved=SceneEditInput.Prepare(session.Document.Clone(),request);
        Assert.Equal(first.SubjectBounds.Left+12,moved.SubjectBounds.Left);Assert.Equal(first.SubjectBounds.Top+5,moved.SubjectBounds.Top);
        var next=SceneEditResult.Extract(moved,moved.Before,request);
        session.InsertSceneEdit(request.Task,original.Id,[next],true,false,moved.PreviousResultIds,moved.SourceFrame);
        using var third=SceneEditInput.Prepare(session.Document.Clone(),request);Assert.Equal(moved.Foreground.Bytes,third.Foreground.Bytes);
    }
    [Fact]
    public void Legacy_nested_results_do_not_leave_ancestor_shadows_in_the_next_input()
    {
        var session=Scene();var source=session.ActiveLayer!;using var initial=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.MatchToScene});
        var shadow=Pixels.NewColor(200,160);using(var draw=new SKCanvas(shadow))using(var paint=new SKPaint {Color=SKColors.Black})draw.DrawRect(80,110,50,20,paint);
        Pixels.Invalidate(shadow);
        session.InsertAiOutput(AiTaskKind.MatchToScene,[new("Legacy shadow",shadow,Tags:["scene-shadow"]),new("Legacy subject",Pixels.Clone(initial.Foreground),Tags:["scene-object"])],group:true);source.Visible=false;
        var old=session.Document.AllLayers().Single(layer=>layer.Tags.Contains("scene-object"));session.Document.SetActive(old.Id);
        session.InsertAiOutput(AiTaskKind.MatchToScene,[new("Nested subject",Pixels.Clone(initial.Foreground),Tags:["scene-object"])],group:true);old.Visible=false;
        using var next=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.MatchToScene});
        Assert.Equal(source.Id,next.SourceLayerId);Assert.Equal(initial.Background.Bytes,next.Background.Bytes);Assert.Equal(initial.Before.Bytes,next.Before.Bytes);
    }
    [Fact]
    public void Cleanup_does_not_restore_the_erased_objects_texture_as_a_watermark()
    {
        var session=Scene();using var input=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.ChangeBackground});
        using var plate=new SceneBackgroundPlate(input,input.Before,20);
        Assert.Equal(input.Before.GetPixel(100,80),plate.Image.GetPixel(100,80));
        using var baseline=Pixels.Clone(plate.Image);using var generated=Pixels.NewColor(200,160);generated.Erase(new SKColor(160,170,180));
        generated.SetPixel(98,80,new(165,171,181));Pixels.Invalidate(generated);
        for(var y=55;y<105;y++)for(var x=80;x<120;x++)baseline.SetPixel(x,y,new((byte)(x%2==0?20:220),40,60));
        Pixels.Invalidate(baseline);using var clean=plate.Finish(generated,input.Before,baseline);
        Assert.Equal(new SKColor(160,170,180),clean.GetPixel(100,80));Assert.Equal(clean.GetPixel(100,80),clean.GetPixel(101,80));
        Assert.Equal(new SKColor(165,171,181),clean.GetPixel(98,80)); // Keep model texture; neither old object nor ROI seam is pasted back.
    }
    [Fact]
    public void Cleanup_preserves_scenery_between_parts_of_the_subject_instead_of_erasing_its_bounding_rectangle()
    {
        var session=EditorSession.NewCanvas(300,220,SKColors.Beige);var png=Pixels.NewColor(180,160);
        using(var draw=new SKCanvas(png))using(var paint=new SKPaint {Color=SKColors.Red})
        {draw.DrawRect(0,0,20,160,paint);draw.DrawRect(160,0,20,160,paint);draw.DrawRect(0,140,180,20,paint);}
        Pixels.Invalidate(png);session.AddImageLayer("Open product",png,new(150,110),fit:false);
        using var input=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.ChangeBackground});
        using var layout=Pixels.Clone(input.Before);layout.SetPixel(150,70,SKColors.Green);Pixels.Invalidate(layout);
        using var plate=new SceneBackgroundPlate(input,layout,16);
        Assert.Equal(0,plate.Mask.GetPixel(150,70).Alpha);Assert.Equal(SKColors.Green,plate.Image.GetPixel(150,70));
        Assert.Equal(255,plate.Mask.GetPixel(70,70).Alpha);Assert.Equal(layout.GetPixel(70,70),plate.Image.GetPixel(70,70));
    }
    [Fact]
    public void Shadow_extraction_preserves_a_known_cast_on_textured_ground_without_stretching_or_rotating_it()
    {
        var session=Scene();var background=session.Document.Layers[0];var texture=Pixels.Clone(background.Pixels!);
        for(var y=0;y<texture.Height;y++)for(var x=0;x<texture.Width;x++)texture.SetPixel(x,y,new((byte)(170+x%11*3),(byte)(175+y%7*3),160));
        Pixels.Invalidate(texture);background.Pixels=texture;
        var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        bool Cast(int x,int y)=>y>=foot&&y<155&&x>=90+(y-foot)/2&&x<125+(y-foot);
        for(var y=foot;y<155;y++)for(var x=0;x<200;x++)if(Cast(x,y))
        {var p=generated.GetPixel(x,y);generated.SetPixel(x,y,new((byte)(p.Red*.65),(byte)(p.Green*.65),(byte)(p.Blue*.65)));}
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            Assert.Equal(0,result.ContactCorrectionX);Assert.Equal(0,result.ContactCorrectionY);
            var intersection=0;var union=0;
            for(var y=0;y<160;y++)for(var x=0;x<200;x++)
            {var expected=Cast(x,y);var actual=shadow.GetPixel(x,y).Alpha>=40;if(expected&&actual)intersection++;if(expected||actual)union++;}
            Assert.True(intersection/(double)union>.94,$"Shadow geometry IoU {intersection/(double)union:0.000}");
        }
        finally{foreach(var layer in result.Layers)layer.Pixels.Dispose();}
    }
    [Fact]
    public void A_faint_narrow_contact_shadow_is_not_averaged_away()
    {
        var session=Scene();var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        // Real contact shadows can be only two document pixels deep. Their darkening is
        // visible but falls below the classifier's cutoff if averaged with unchanged floor.
        for(var y=foot;y<foot+2;y++)for(var x=80;x<120;x++)generated.SetPixel(x,y,new(194,194,194));
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(input,generated,request);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            Assert.True(shadow.GetPixel(100,foot).Alpha>=3);
            Assert.Equal(0,result.ContactCorrectionX);Assert.Equal(0,result.ContactCorrectionY);
        }
        finally{foreach(var layer in result.Layers)layer.Pixels.Dispose();}
    }
    [Fact]
    public void A_shifted_background_texture_is_not_rectified_into_a_canvas_wide_shadow()
    {
        var session=Scene();var background=session.Document.Layers[0];var texture=Pixels.Clone(background.Pixels!);
        for(var y=0;y<texture.Height;y++)for(var x=0;x<texture.Width;x++)texture.SetPixel(x,y,x%2==0?new SKColor(140,140,140):new SKColor(200,200,200));
        Pixels.Invalidate(texture);background.Pixels=texture;
        var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var shifted=Pixels.Clone(input.Before);
        for(var y=0;y<shifted.Height;y++)for(var x=0;x<shifted.Width;x++)if(input.ObjectAlpha.GetPixel(x,y).Alpha==0)
            shifted.SetPixel(x,y,x%2==0?new SKColor(200,200,200):new SKColor(140,140,140));
        Pixels.Invalidate(shifted);var result=SceneEditResult.Extract(input,shifted,request,reconstruction:input.Before);
        try{Assert.True(result.ShadowPixels==0,$"pixels={result.ShadowPixels}; before={input.Before.GetPixel(10,10)}/{input.Before.GetPixel(11,10)}; background={input.Background.GetPixel(10,10)}/{input.Background.GetPixel(11,10)}; target={shifted.GetPixel(10,10)}/{shifted.GetPixel(11,10)}");}
        finally{foreach(var layer in result.Layers)layer.Pixels.Dispose();}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Global_background_color_and_contrast_drift_is_removed_while_a_local_cast_is_retained(bool cast)
    {
        var session=Scene();var background=session.Document.Layers[0];var texture=Pixels.Clone(background.Pixels!);
        for(var y=0;y<texture.Height;y++)for(var x=0;x<texture.Width;x++)texture.SetPixel(x,y,new((byte)(80+x/2),(byte)(190-x/3),(byte)(90+y/2)));
        Pixels.Invalidate(texture);background.Pixels=texture;
        var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        static double Linear(byte v){var s=v/255d;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);}
        static byte Encode(double l)=>(byte)Math.Round(255*(l<=.0031308?l*12.92:1.055*Math.Pow(l,1/2.4)-.055));
        for(var y=0;y<generated.Height;y++)for(var x=0;x<generated.Width;x++)if(input.ObjectAlpha.GetPixel(x,y).Alpha==0)
        {
            var p=input.Before.GetPixel(x,y);var attenuation=cast&&y>=foot&&y<foot+25&&x>=95&&x<130?.65:1;
            generated.SetPixel(x,y,new(Encode(.8*Linear(p.Red)*attenuation+.025),Encode(.9*Linear(p.Green)*attenuation+.015),Encode(.75*Linear(p.Blue)*attenuation+.03)));
        }
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try
        {
            if(!cast)Assert.Equal(0,result.ShadowPixels);
            else
            {
                var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
                Assert.True(shadow.GetPixel(110,foot+12).Alpha>30);Assert.Equal(0,shadow.GetPixel(160,30).Alpha);
            }
        }
        finally{foreach(var layer in result.Layers)layer.Pixels.Dispose();}
    }
    [Fact]
    public void Interior_dimming_reaches_antialiased_edges_instead_of_leaving_a_bright_original_rim()
    {
        var session=Scene();var request=new AiTaskRequest {Task=AiTaskKind.Relight,Scene=new() {AddShadows=false}};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);using var generated=Pixels.Clone(input.Before);
        var b=input.SubjectBounds;
        for(var y=b.Top+5;y<b.Bottom-5;y++)for(var x=b.Left+5;x<b.Right-5;x++)generated.SetPixel(x,y,new(50,50,50));
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(input,generated,request);
        try
        {
            var output=Assert.Single(result.Layers).Pixels;
            Assert.InRange(output.GetPixel(b.Left,b.Top+b.Height/2).Red,25,85);
            Assert.Equal(input.ObjectAlpha.GetPixel(b.Left,b.Top+b.Height/2).Alpha,output.GetPixel(b.Left,b.Top+b.Height/2).Alpha);
        }
        finally{foreach(var layer in result.Layers)layer.Pixels.Dispose();}
    }
    [Fact]
    public void Long_backward_cast_and_its_soft_tail_are_not_clipped_by_a_near_base_floor_rectangle()
    {
        var session=Scene();var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);using var generated=Pixels.Clone(input.Before);
        var foot=input.SupportBounds.Bottom;
        // Connected shadow heads around the object and then toward the distant background.
        for(var y=15;y<foot+6;y++)for(var x=124;x<142;x++)generated.SetPixel(x,y,new(100,100,100));
        for(var y=foot;y<foot+6;y++)for(var x=110;x<142;x++)generated.SetPixel(x,y,new(100,100,100));
        for(var y=20;y<80;y++)for(var x=142;x<153;x++)generated.SetPixel(x,y,new(189,189,189));
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(input,generated,request);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            Assert.True(shadow.GetPixel(133,20).Alpha>30);Assert.True(shadow.GetPixel(149,40).Alpha>0);
        }
        finally{foreach(var layer in result.Layers)layer.Pixels.Dispose();}
    }
}
