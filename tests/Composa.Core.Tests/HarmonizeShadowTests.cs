using Composa.AI;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HarmonizeShadowTests
{
    private static EditorSession Scene(bool legs=false)
    {
        var session=EditorSession.NewCanvas(240,180,new SKColor(200,200,200));
        var png=Pixels.NewColor(100,110);
        using(var canvas=new SKCanvas(png)) using(var paint=new SKPaint {Color=new(60,60,60),IsAntialias=false})
        {
            if(legs)
            {
                canvas.DrawRect(5,5,90,50,paint);
                canvas.DrawRect(12,55,14,50,paint);
                canvas.DrawRect(74,55,14,15,paint);
            }
            else canvas.DrawRect(10,10,80,90,paint);
        }
        Pixels.Invalidate(png);session.AddImageLayer("Sharp PNG",png,new(120,80),fit:false);
        return session;
    }
    private static void Darken(SKBitmap generated,SceneEditInput input,int left,int right,int top,int bottom,byte alpha)
    {
        for(var y=top;y<bottom;y++) for(var x=left;x<right;x++)
        {
            if(input.ObjectAlpha.GetPixel(x,y).Alpha!=0)continue;
            var value=(byte)Math.Round(200*(1-alpha/255d));
            generated.SetPixel(x,y,new(value,value,value));
        }
        Pixels.Invalidate(generated);
    }
    private static void Dispose(SceneEditResult result)
    {foreach(var layer in result.Layers) {layer.Pixels.Dispose();layer.Mask?.Dispose();}}

    [Theory]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.MatchToScene)]
    public void Sharp_contact_keeps_its_density_at_the_object_edge(AiTaskKind task)
    {
        var session=Scene();var request=new AiTaskRequest {Task=task};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        Darken(generated,input,90,150,foot,foot+24,128);
        var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try
        {
            Evidence(nameof(Sharp_contact_keeps_its_density_at_the_object_edge)+task,input,generated,result);
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            var contact=shadow.GetPixel(120,foot).Alpha;var interior=shadow.GetPixel(120,foot+8).Alpha;
            Assert.InRange(interior,125,130);
            Assert.InRange(contact,interior-5,interior+5);
            Assert.Equal(0,result.ContactCorrectionX);Assert.Equal(0,result.ContactCorrectionY);
            var subject=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-object")).Pixels;
            for(var y=0;y<180;y++)for(var x=0;x<240;x++)Assert.Equal(input.ObjectAlpha.GetPixel(x,y).Alpha,subject.GetPixel(x,y).Alpha);
        }
        finally {Dispose(result);}
    }
    [Theory]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.MatchToScene)]
    public void Faint_connected_contact_does_not_move_the_stronger_cast(AiTaskKind task)
    {
        var session=Scene();var request=new AiTaskRequest {Task=task};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        Darken(generated,input,100,140,foot,foot+9,3);
        Darken(generated,input,100,140,foot+9,foot+25,120);
        var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try
        {
            Evidence(nameof(Faint_connected_contact_does_not_move_the_stronger_cast)+task,input,generated,result);
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            Assert.Equal(0,result.ContactCorrectionX);Assert.Equal(0,result.ContactCorrectionY);
            for(var y=foot;y<foot+6;y++)Assert.InRange(shadow.GetPixel(120,y).Alpha,1,8);
            for(var y=foot+6;y<foot+9;y++)Assert.InRange(shadow.GetPixel(120,y).Alpha,1,80);
            Assert.InRange(shadow.GetPixel(120,foot+17).Alpha,116,124);
            Assert.Equal(0,shadow.GetPixel(120,foot+29).Alpha);
        }
        finally {Dispose(result);}
    }
    [Theory]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.MatchToScene)]
    public void A_shorter_rear_leg_keeps_its_own_contact_shadow(AiTaskKind task)
    {
        var session=Scene(legs:true);var request=new AiTaskRequest {Task=task};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportY[150]+1;
        Assert.True(foot<input.SupportBounds.Bottom-input.SupportBounds.Height/4);
        Darken(generated,input,145,155,foot,foot+10,90);
        var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try
        {
            Evidence(nameof(A_shorter_rear_leg_keeps_its_own_contact_shadow)+task,input,generated,result);
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            Assert.True(shadow.GetPixel(150,foot).Alpha>=80);
            Assert.Equal(0,result.ContactCorrectionX);Assert.Equal(0,result.ContactCorrectionY);
            Assert.Equal(0,shadow.GetPixel(90,input.SupportBounds.Bottom).Alpha);
        }
        finally {Dispose(result);}
    }
    [Fact]
    public void Isolated_weak_background_changes_do_not_become_a_shadow()
    {
        var session=Scene();var request=new AiTaskRequest {Task=AiTaskKind.Harmonize};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);
        Darken(generated,input,100,140,input.SupportBounds.Bottom,input.SupportBounds.Bottom+12,3);
        var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try {Assert.Equal(0,result.ShadowPixels);}
        finally {Dispose(result);}
    }
    [Fact]
    public void A_detached_low_contrast_floor_patch_is_not_shifted_onto_the_png()
    {
        var session=Scene();var request=new AiTaskRequest {Task=AiTaskKind.Harmonize};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        Darken(generated,input,100,140,foot+10,foot+22,10);
        var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try {Assert.Equal(0,result.ShadowPixels);Assert.Equal(0,result.ContactCorrectionY);}
        finally {Dispose(result);}
    }
    [Theory]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.MatchToScene)]
    public void Uneven_model_edge_has_no_empty_rows_between_source_and_accepted_shadow(AiTaskKind task)
    {
        var session=Scene();var request=new AiTaskRequest {Task=task};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        for(var x=95;x<145;x++)Darken(generated,input,x,x+1,foot+(x<115?0:4),foot+24,100);
        var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            for(var x=100;x<140;x++)for(var y=foot;y<foot+7;y++)Assert.True(shadow.GetPixel(x,y).Alpha>0,$"Gap at {x},{y}");
            Assert.Equal(0,result.ContactCorrectionX);Assert.Equal(0,result.ContactCorrectionY);
            Evidence(nameof(Uneven_model_edge_has_no_empty_rows_between_source_and_accepted_shadow)+task,input,generated,result);
        }
        finally {Dispose(result);}
    }

    [Fact]
    public void Colored_ambient_shadow_reproduces_the_generated_ground_in_normal_over_compositing()
    {
        var session=Scene();var request=new AiTaskRequest {Task=AiTaskKind.Harmonize};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        for(var y=foot;y<foot+25;y++)for(var x=95;x<145;x++)generated.SetPixel(x,y,new(95,110,135));
        Pixels.Invalidate(generated);
        var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try
        {
            using var composite=Pixels.Clone(input.Background);
            using(var canvas=new SKCanvas(composite))foreach(var layer in result.Layers)canvas.DrawImage(Pixels.ImageOf(layer.Pixels),0,0);
            var color=composite.GetPixel(120,foot+12);
            Assert.InRange(color.Red,93,97);Assert.InRange(color.Green,108,112);Assert.InRange(color.Blue,133,137);
            Evidence(nameof(Colored_ambient_shadow_reproduces_the_generated_ground_in_normal_over_compositing),input,generated,result);
        }
        finally {Dispose(result);}
    }

    [Fact]
    public void Semitransparent_side_and_base_keep_the_original_alpha_and_underlaid_shadow()
    {
        var session=Scene();var source=Pixels.Clone(session.ActiveLayer!.Pixels!);
        // A partially covered side ABOVE the column's support used to be excluded entirely.
        for(var y=10;y<100;y++)for(var x=10;x<14;x++)source.SetPixel(x,y,new(60,60,60,(byte)(64+(x-10)*48)));
        for(var y=97;y<100;y++)for(var x=14;x<90;x++)source.SetPixel(x,y,new(60,60,60,(byte)(240-(y-97)*80)));
        Pixels.Invalidate(source);session.ActiveLayer.Pixels=source;
        var request=new AiTaskRequest {Task=AiTaskKind.Harmonize};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        using var generated=Pixels.Clone(input.Before);var foot=input.SupportBounds.Bottom;
        for(var y=foot-10;y<foot+20;y++)for(var x=74;x<150;x++)
        {
            var a=input.ObjectAlpha.GetPixel(x,y).Alpha/255d;
            var value=(byte)Math.Round(a*60+(1-a)*100);generated.SetPixel(x,y,new(value,value,value));
        }
        Pixels.Invalidate(generated);var result=SceneEditResult.Extract(input,generated,request,reconstruction:input.Before);
        try
        {
            var shadow=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-shadow")).Pixels;
            for(var y=foot-8;y<foot+10;y++)Assert.True(shadow.GetPixel(81,y).Alpha>0,$"Side gap at {y}");
            var subject=Assert.Single(result.Layers,layer=>layer.Tags!.Contains("scene-object")).Pixels;
            for(var y=0;y<180;y++)for(var x=0;x<240;x++)Assert.Equal(input.ObjectAlpha.GetPixel(x,y).Alpha,subject.GetPixel(x,y).Alpha);
            Evidence(nameof(Semitransparent_side_and_base_keep_the_original_alpha_and_underlaid_shadow),input,generated,result);
        }
        finally {Dispose(result);}
    }

    [Theory]
    [InlineData(45,-1,1)]
    [InlineData(135,1,1)]
    [InlineData(225,1,-1)]
    [InlineData(315,-1,-1)]
    public void Explicit_light_angles_describe_the_opposite_projected_shadow(double angle,int x,int y)
    {
        var session=Scene();var request=new AiTaskRequest {Task=AiTaskKind.Harmonize,Scene=new() {LightAngle=angle}};
        using var input=SceneEditInput.Prepare(session.Document.Clone(),request);
        Assert.True(input.LightHint.Directional);
        Assert.Equal(x,Math.Sign(input.LightHint.Direction.X));Assert.Equal(y,Math.Sign(input.LightHint.Direction.Y));
        Assert.Contains(x>0?"right":"left",input.LightHint.Instruction);
        Assert.Contains(y>0?"bottom foreground":"upper background",input.LightHint.Instruction);
    }

    [Fact]
    public void Explicit_light_rejects_opposite_background_darkening_but_preserves_soft_contact()
    {
        var session=Scene();
        using var directed=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.Relight,Scene=new() {Preset=RelightPreset.GoldenHour}});
        var b=directed.SupportBounds;
        Assert.Equal(0,directed.ShadowMask.GetPixel(0,0).Alpha);
        Assert.True(directed.ShadowMask.GetPixel(b.Right+8,b.Bottom+8).Alpha>0);
        Assert.True(directed.ShadowMask.GetPixel(b.Left-2,b.Bottom).Alpha>0);
        using var automatic=SceneEditInput.Prepare(session.Document.Clone(),new() {Task=AiTaskKind.Harmonize});
        Assert.True(automatic.ShadowMask.GetPixel(0,0).Alpha>0);
    }

    [Fact]
    public void Smooth_even_support_requests_soft_contact_but_strong_edges_and_manual_angles_do_not()
    {
        var session=Scene();var request=new AiTaskRequest {Task=AiTaskKind.Harmonize};
        using(var smooth=SceneEditInput.Prepare(session.Document.Clone(),request))Assert.True(smooth.LightHint.SoftContactOnly);
        using(var manual=SceneEditInput.Prepare(session.Document.Clone(),request with {Scene=new() {LightAngle=135}}))Assert.False(manual.LightHint.SoftContactOnly);
        var ground=Pixels.Clone(session.Document.Layers[0].Pixels!);
        for(var y=0;y<ground.Height;y++)for(var x=0;x<ground.Width;x++)ground.SetPixel(x,y,(x+y)/24%2==0?new SKColor(70,70,70):new SKColor(220,220,220));
        Pixels.Invalidate(ground);session.Document.Layers[0].Pixels=ground;
        using var textured=SceneEditInput.Prepare(session.Document.Clone(),request);
        Assert.False(textured.LightHint.SoftContactOnly);Assert.False(textured.LightHint.Directional);
    }

    [Theory]
    // source value, model subject RGB, model ground RGB, preserve source detail
    [InlineData(85, 64, 64, 64, 120, 120, 120, true)]
    [InlineData(18, 5, 5, 5, 120, 120, 120, true)]
    [InlineData(18, 5, 5, 155, 120, 120, 120, true)]
    [InlineData(18, 5, 5, 155, 120, 120, 120, false)]
    [InlineData(18, 5, 5, 155, 105, 118, 145, true)]
    public void Cast_color_comes_from_ground_rather_than_the_lit_subject_fringe(
        byte sourceValue, byte red, byte green, byte blue, byte groundRed, byte groundGreen, byte groundBlue, bool preserveDetails)
    {
        var session = Scene();
        var source = Pixels.Clone(session.ActiveLayer!.Pixels!);
        for (var y = 10; y < 100; y++)
            for (var x = 10; x < 90; x++)
                source.SetPixel(x, y, new(sourceValue, sourceValue, sourceValue, x switch { 10 => 48, 11 => 112, _ => 255 }));
        Pixels.Invalidate(source);
        session.ActiveLayer.Pixels = source;

        var request = new AiTaskRequest { Task = AiTaskKind.Harmonize, Scene = new() { PreserveDetails = preserveDetails } };
        using var input = SceneEditInput.Prepare(session.Document.Clone(), request);
        using var generated = Pixels.Clone(input.Before);
        var left = input.SubjectBounds.Left;
        var foot = input.SupportBounds.Bottom;
        var expectedGround = new SKColor(groundRed, groundGreen, groundBlue);
        for (var y = input.SubjectBounds.Top; y < foot + 24; y++)
            for (var x = left - 8; x < left + 80; x++)
            {
                var alpha = input.ObjectAlpha.GetPixel(x, y).Alpha / 255d;
                var ground = y >= foot - 12 ? expectedGround : new SKColor(200, 200, 200);
                byte Composite(byte subject, byte background) => (byte)Math.Round(subject * alpha + background * (1 - alpha));
                generated.SetPixel(x, y, new(Composite(red, ground.Red), Composite(green, ground.Green), Composite(blue, ground.Blue)));
            }
        Pixels.Invalidate(generated);

        var result = SceneEditResult.Extract(input, generated, request, reconstruction: input.Before);
        var half = SceneEditResult.Extract(input, generated, request with { Scene = request.Scene with { Strength = .5 } }, reconstruction: input.Before);
        try
        {
            var name = $"color-fringe-{sourceValue}-{red}-{green}-{blue}-{groundRed}-{groundGreen}-{groundBlue}-{preserveDetails}";
            Evidence(name, input, generated, result);
            using var ground = CompositeShadow(result);
            using var halfGround = CompositeShadow(half);
            var center = ground.GetPixel(left + 25, foot + 10);
            var fringe = ground.GetPixel(left + 1, foot - 5);
            var outside = ground.GetPixel(left - 14, foot + 10);
            Near(expectedGround, center, 3);
            Near(expectedGround, fringe, 5);
            Near(new(200, 200, 200), outside, 1);
            Near(new((byte)((200 + groundRed) / 2), (byte)((200 + groundGreen) / 2), (byte)((200 + groundBlue) / 2)),
                halfGround.GetPixel(left + 1, foot - 5), 5);
            var subject = Assert.Single(result.Layers, layer => layer.Tags!.Contains("scene-object")).Pixels;
            using var subjectAlpha = SceneEditInput.Alpha(subject);
            Assert.Equal(input.ObjectAlpha.Bytes, subjectAlpha.Bytes);
        }
        finally { Dispose(result); Dispose(half); }

        SKBitmap CompositeShadow(SceneEditResult extracted)
        {
            var shadow = Assert.Single(extracted.Layers, layer => layer.Tags!.Contains("scene-shadow")).Pixels;
            var ground = Pixels.Clone(input.Background);
            using (var canvas = new SKCanvas(ground)) canvas.DrawImage(Pixels.ImageOf(shadow), 0, 0);
            return ground;
        }

        static void Near(SKColor expected, SKColor actual, int tolerance)
        {
            Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance &&
                Math.Abs(expected.Green - actual.Green) <= tolerance &&
                Math.Abs(expected.Blue - actual.Blue) <= tolerance, $"Expected {expected}, observed {actual}");
        }
    }


    [Fact]
    public void Tinted_ground_shading_survives_insertion_in_a_display_p3_document()
    {
        var session = Scene();
        session.AssignColorProfile(RgbProfile.DisplayP3);
        var request = new AiTaskRequest { Task = AiTaskKind.Harmonize };
        using var input = SceneEditInput.Prepare(session.Document.Clone(), request);
        using var generated = Pixels.Clone(input.Before);
        var foot = input.SupportBounds.Bottom;
        var desired = new SKColor(95, 110, 135);
        for (var y = foot; y < foot + 20; y++)
            for (var x = 100; x < 140; x++)
                generated.SetPixel(x, y, desired);
        Pixels.Invalidate(generated);

        var result = SceneEditResult.Extract(input, generated, request);
        var shadow = Assert.Single(result.Layers, layer => layer.Tags!.Contains("scene-shadow"));
        Assert.Equal(RgbProfile.Srgb, Pixels.ProfileOf(shadow.Pixels));
        session.InsertSceneEdit(request.Task, input.SourceLayerId, [result], input.IsCutout, input.WholeImage);
        using var working = session.Flatten();
        Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(working));

        using var composite = Pixels.DisplayCopy(working);
        var cast = composite.GetPixel(120, foot + 10);
        Assert.InRange(cast.Red, 92, 98);
        Assert.InRange(cast.Green, 107, 113);
        Assert.InRange(cast.Blue, 132, 138);
        Assert.Equal(input.Before.GetPixel(20, 20), composite.GetPixel(20, 20));
    }


    private static void Evidence(string name,SceneEditInput input,SKBitmap generated,SceneEditResult result)
    {
        var root=Environment.GetEnvironmentVariable("COMPOSA_SHADOW_DIAGNOSTICS");if(string.IsNullOrEmpty(root))return;
        var folder=Path.Combine(root,name);Directory.CreateDirectory(folder);
        Save(input.Before,"source");Save(generated,"raw");
        using var composite=Pixels.Clone(input.Background);
        using(var canvas=new SKCanvas(composite))foreach(var layer in result.Layers)canvas.DrawImage(Pixels.ImageOf(layer.Pixels),0,0);
        Pixels.Invalidate(composite);Save(composite,"final");
        using var shadow=Pixels.NewColor(generated.Width,generated.Height);shadow.Erase(SKColors.White);
        using(var canvas=new SKCanvas(shadow))foreach(var layer in result.Layers.Where(layer=>layer.Tags!.Contains("scene-shadow")))canvas.DrawImage(Pixels.ImageOf(layer.Pixels),0,0);
        Pixels.Invalidate(shadow);Save(shadow,"shadow");
        void Save(SKBitmap bitmap,string label)
        {using var data=Pixels.ImageOf(bitmap).Encode(SKEncodedImageFormat.Png,100);using var file=File.Create(Path.Combine(folder,label+".png"));data.SaveTo(file);}
    }
}
